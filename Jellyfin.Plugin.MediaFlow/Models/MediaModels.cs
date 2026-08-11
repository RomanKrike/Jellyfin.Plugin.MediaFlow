using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaFlow.Models;

public enum MediaKind
{
    Unknown = 0,
    Movie = 1,
    Episode = 2
}

public sealed record TitleSignal(string Value, double Weight, string Source);

public sealed record YearSignal(int Value, double Weight, string Source);

public sealed class ParsedMedia
{
    public MediaKind Kind { get; set; }

    public string TorrentName { get; set; } = string.Empty;

    public string RelativeFileName { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public List<TitleSignal> Titles { get; } = [];

    public List<YearSignal> Years { get; } = [];
}

public sealed class TmdbCandidate
{
    public int Id { get; set; }

    public MediaKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string OriginalTitle { get; set; } = string.Empty;

    public int? Year { get; set; }

    public double Popularity { get; set; }

    public string? PosterPath { get; set; }

    public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool? EpisodeExists { get; set; }

    public string? EpisodeTitle { get; set; }

    public int? EpisodeAirYear { get; set; }

    public double Score { get; set; }

    public List<string> Reasons { get; } = [];
}

public sealed class ResolutionResult
{
    public bool AutoApproved { get; set; }

    public string Reason { get; set; } = string.Empty;

    public TmdbCandidate? Selected { get; set; }

    public IReadOnlyList<TmdbCandidate> Candidates { get; set; } = [];
}

public sealed class ReviewCandidateSnapshot
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("kind")]
    public MediaKind Kind { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("originalTitle")]
    public string OriginalTitle { get; set; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; set; }

    [JsonPropertyName("posterPath")]
    public string? PosterPath { get; set; }

    [JsonPropertyName("episodeTitle")]
    public string? EpisodeTitle { get; set; }

    [JsonPropertyName("episodeAirYear")]
    public int? EpisodeAirYear { get; set; }

    [JsonPropertyName("score")]
    public double Score { get; set; }

    [JsonPropertyName("reasons")]
    public List<string> Reasons { get; set; } = [];

    public static ReviewCandidateSnapshot FromCandidate(TmdbCandidate candidate)
        => new()
        {
            Id = candidate.Id,
            Kind = candidate.Kind,
            Title = candidate.Title,
            OriginalTitle = candidate.OriginalTitle,
            Year = candidate.Year,
            PosterPath = candidate.PosterPath,
            EpisodeTitle = candidate.EpisodeTitle,
            EpisodeAirYear = candidate.EpisodeAirYear,
            Score = candidate.Score,
            Reasons = candidate.Reasons.ToList()
        };
}

public sealed class ImportStateEntry
{
    public string Key { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string? DestinationPath { get; set; }

    public int? TmdbId { get; set; }

    public string? Message { get; set; }

    public MediaKind? Kind { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public List<ReviewCandidateSnapshot> ReviewCandidates { get; set; } = [];

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
