using Jellyfin.Plugin.MediaFlow.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class MediaResolver
{
    private readonly TmdbClient _tmdb;
    private readonly ILogger<MediaResolver> _logger;

    public MediaResolver(TmdbClient tmdb, ILogger<MediaResolver> logger)
    {
        _tmdb = tmdb;
        _logger = logger;
    }

    public async Task<ResolutionResult> ResolveAsync(ParsedMedia parsed, CancellationToken cancellationToken)
    {
        if (parsed.Kind == MediaKind.Unknown || parsed.Titles.Count == 0)
        {
            return new ResolutionResult { Reason = "Parser could not extract a usable media title." };
        }

        var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("MediaFlow configuration is unavailable.");
        var candidateMap = new Dictionary<int, TmdbCandidate>();
        var titleSignals = parsed.Titles.OrderByDescending(x => x.Weight).Take(4).ToList();
        var years = parsed.Years.OrderByDescending(x => x.Weight).Select(x => x.Value).Distinct().Take(2).ToList();

        foreach (var title in titleSignals)
        {
            foreach (var year in years.Select(x => (int?)x).Prepend(null).Distinct())
            {
                var found = await _tmdb.SearchAsync(parsed.Kind, title.Value, year, cancellationToken).ConfigureAwait(false);
                foreach (var candidate in found.Take(8))
                {
                    candidateMap.TryAdd(candidate.Id, candidate);
                }
            }
        }

        if (candidateMap.Count == 0)
        {
            return new ResolutionResult { Reason = "TMDb returned no candidates." };
        }

        var rough = candidateMap.Values
            .Select(x => (Candidate: x, Score: RoughTitleScore(parsed, x)))
            .OrderByDescending(x => x.Score)
            .Take(6)
            .Select(x => x.Candidate)
            .ToList();

        foreach (var candidate in rough)
        {
            await _tmdb.EnrichAliasesAsync(candidate, cancellationToken).ConfigureAwait(false);

            if (parsed.Kind == MediaKind.Episode && parsed.Season.HasValue && parsed.Episode.HasValue)
            {
                var episodeInfo = await _tmdb.GetEpisodeInfoAsync(
                    candidate.Id,
                    parsed.Season.Value,
                    parsed.Episode.Value,
                    cancellationToken).ConfigureAwait(false);
                candidate.EpisodeExists = episodeInfo.Exists;
                candidate.EpisodeTitle = episodeInfo.Title;
                candidate.EpisodeAirYear = episodeInfo.AirYear;
            }

            candidate.Score = FinalScore(parsed, candidate);
        }

        var ranked = rough.OrderByDescending(x => x.Score).ToList();
        var best = ranked[0];
        var second = ranked.Count > 1 ? ranked[1] : null;
        var gap = second is null ? 100 : best.Score - second.Score;
        var approved = best.Score >= config.AutoMatchScore && gap >= config.MinimumScoreGap;

        var reason = approved
            ? $"Auto match: score {best.Score:F1}, gap {gap:F1}."
            : $"Needs review: best score {best.Score:F1}, gap {gap:F1}; thresholds are {config.AutoMatchScore:F1}/{config.MinimumScoreGap:F1}.";

        _logger.LogInformation("Resolver for {File}: {Reason} Selected={Title} ({Year}) TMDb={Id}", parsed.RelativeFileName, reason, best.Title, best.Year, best.Id);

        return new ResolutionResult
        {
            AutoApproved = approved,
            Reason = reason,
            Selected = best,
            Candidates = ranked
        };
    }

    private static double RoughTitleScore(ParsedMedia parsed, TmdbCandidate candidate)
    {
        var aliases = new[] { candidate.Title, candidate.OriginalTitle }.Where(x => !string.IsNullOrWhiteSpace(x));
        return parsed.Titles.Max(signal => aliases.Max(alias => TitleSimilarity.Score(signal.Value, alias) * signal.Weight));
    }

    private static double FinalScore(ParsedMedia parsed, TmdbCandidate candidate)
    {
        var aliases = candidate.Aliases.Count > 0 ? candidate.Aliases : new HashSet<string>(new[] { candidate.Title, candidate.OriginalTitle });
        var bestTitle = 0.0;
        foreach (var signal in parsed.Titles)
        {
            foreach (var alias in aliases.Where(x => !string.IsNullOrWhiteSpace(x)))
            {
                bestTitle = Math.Max(bestTitle, TitleSimilarity.Score(signal.Value, alias) * signal.Weight);
            }
        }

        var titlePoints = bestTitle * (parsed.Kind == MediaKind.Episode ? 65.0 : 75.0);
        candidate.Reasons.Add($"title={titlePoints:F1}");

        var yearPoints = 0.0;
        if (parsed.Years.Count > 0)
        {
            if (parsed.Kind == MediaKind.Movie && candidate.Year.HasValue)
            {
                var bestDistance = parsed.Years.Min(x => Math.Abs(x.Value - candidate.Year.Value));
                yearPoints = bestDistance switch
                {
                    0 => 15,
                    1 => 4,
                    _ => -4
                };
                candidate.Reasons.Add($"year={yearPoints:+0.0;-0.0;0.0}");
            }
            else if (parsed.Kind == MediaKind.Episode && candidate.EpisodeExists == true)
            {
                // A torrent year for TV commonly describes the season/episode release year,
                // while TMDb candidate.Year is the series first-air year. Never penalize a
                // structurally confirmed episode for that mismatch. If TMDb exposes the
                // actual episode air year, matching it is useful positive evidence.
                if (candidate.EpisodeAirYear.HasValue)
                {
                    var bestDistance = parsed.Years.Min(x => Math.Abs(x.Value - candidate.EpisodeAirYear.Value));
                    yearPoints = bestDistance switch
                    {
                        0 => 10,
                        1 => 3,
                        _ => 0
                    };
                    candidate.Reasons.Add($"episodeYear={yearPoints:+0.0;-0.0;0.0}");
                }
                else
                {
                    candidate.Reasons.Add("year=ignored");
                }
            }
        }

        var structurePoints = 0.0;
        if (parsed.Kind == MediaKind.Episode && parsed.Season.HasValue && parsed.Episode.HasValue)
        {
            structurePoints = candidate.EpisodeExists switch
            {
                true => 20,
                false => -35,
                _ => 0
            };
            candidate.Reasons.Add($"episode={structurePoints:+0.0;-0.0;0.0}");
        }

        var consensusPoints = HasTitleConsensus(parsed.Titles) ? (parsed.Kind == MediaKind.Episode ? 5 : 10) : 0;
        if (consensusPoints > 0)
        {
            candidate.Reasons.Add($"context=+{consensusPoints}");
        }

        return Math.Clamp(titlePoints + yearPoints + structurePoints + consensusPoints, 0, 100);
    }

    private static bool HasTitleConsensus(IReadOnlyList<TitleSignal> signals)
    {
        for (var i = 0; i < signals.Count; i++)
        {
            for (var j = i + 1; j < signals.Count; j++)
            {
                if (TitleSimilarity.Score(signals[i].Value, signals[j].Value) >= 0.90)
                {
                    return true;
                }
            }
        }

        return false;
    }
}
