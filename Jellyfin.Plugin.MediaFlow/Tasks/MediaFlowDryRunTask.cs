using Jellyfin.Plugin.MediaFlow.Configuration;
using Jellyfin.Plugin.MediaFlow.Models;
using Jellyfin.Plugin.MediaFlow.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaFlow.Tasks;

/// <summary>
/// Manual, intrinsically read-only MediaFlow diagnostic task.
///
/// This task intentionally does not depend on HardLinkService, ImportStateStore,
/// ILibraryManager, or qBittorrent priority mutation methods. It can therefore be
/// used to validate parsing/TMDb matching even while the background worker is disabled.
/// </summary>
public sealed class MediaFlowDryRunTask : IScheduledTask
{
    private readonly QbittorrentClient _qbittorrent;
    private readonly MediaParser _parser;
    private readonly MediaResolver _resolver;
    private readonly PathMapper _pathMapper;
    private readonly ILogger<MediaFlowDryRunTask> _logger;

    public MediaFlowDryRunTask(
        QbittorrentClient qbittorrent,
        MediaParser parser,
        MediaResolver resolver,
        PathMapper pathMapper,
        ILogger<MediaFlowDryRunTask> logger)
    {
        _qbittorrent = qbittorrent;
        _parser = parser;
        _resolver = resolver;
        _pathMapper = pathMapper;
        _logger = logger;
    }

    public string Name => "MediaFlow: Dry Run";

    public string Key => "MediaFlowDryRun";

    public string Description =>
        "Safely analyzes one selected qBittorrent torrent with the MediaFlow parser and TMDb resolver. " +
        "Does not create hardlinks, alter qBittorrent priorities, write import state, or refresh Jellyfin libraries.";

    public string Category => "MediaFlow";

    // No automatic triggers. This task is deliberately manual-only.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("MediaFlow plugin configuration is not available.");

        ValidateConfiguration(config);

        var filter = config.DryRunTorrentFilter.Trim();
        if (string.IsNullOrWhiteSpace(filter))
        {
            throw new InvalidOperationException(
                "DryRunTorrentFilter is empty. Set a unique torrent-name fragment or exact qBittorrent hash in MediaFlow settings first.");
        }

        _logger.LogWarning(
            "MediaFlow MANUAL DRY RUN TASK started. filter={Filter}, maxFiles={MaxFiles}. " +
            "This task is read-only: no hardlinks, import state, library scans, or qBittorrent priorities will be changed.",
            filter,
            Math.Clamp(config.DryRunMaxFiles, 1, 20));

        progress.Report(2);

        var torrents = await _qbittorrent.GetTorrentsAsync(cancellationToken).ConfigureAwait(false);

        var exactHash = torrents
            .Where(x => string.Equals(x.Hash, filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var matches = exactHash.Count > 0
            ? exactHash
            : torrents
                .Where(x => x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList();

        if (matches.Count == 0)
        {
            _logger.LogWarning(
                "MediaFlow MANUAL DRY RUN: no torrent matched filter '{Filter}'.",
                filter);
            progress.Report(100);
            return;
        }

        if (matches.Count > 1)
        {
            _logger.LogWarning(
                "MediaFlow MANUAL DRY RUN: filter '{Filter}' matched {Count} torrents. " +
                "Make the filter unique or use the exact hash. Matches: {Matches}",
                filter,
                matches.Count,
                string.Join(" | ", matches.Take(8).Select(x => x.Name)));
            progress.Report(100);
            return;
        }

        var torrent = matches[0];
        var categoryKind = GetCategoryKind(torrent.Category, config);
        if (categoryKind == MediaKind.Unknown)
        {
            _logger.LogWarning(
                "MediaFlow MANUAL DRY RUN: matched torrent '{Torrent}' has category '{Category}', " +
                "which is neither configured movie category '{MovieCategory}' nor TV category '{TvCategory}'.",
                torrent.Name,
                torrent.Category,
                config.QbittorrentMovieCategory,
                config.QbittorrentTvCategory);
            progress.Report(100);
            return;
        }

        var files = await _qbittorrent.GetFilesAsync(torrent.Hash, cancellationToken).ConfigureAwait(false);
        var completedVideos = files
            .Where(file => IsVideoFile(file, config))
            .Where(file => file.Progress >= 0.999999)
            .Where(file => file.Size >= config.MinimumVideoSizeMb * 1024L * 1024L)
            .Where(file => !IsSample(file.Name))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(config.DryRunMaxFiles, 1, 20))
            .ToList();

        _logger.LogWarning(
            "MediaFlow DRY RUN START: {Torrent} | hash={Hash} | category={Category} | " +
            "completed video files selected={Count}. No files or qBittorrent priorities will be changed.",
            torrent.Name,
            torrent.Hash,
            torrent.Category,
            completedVideos.Count);

        if (completedVideos.Count == 0)
        {
            _logger.LogWarning("MediaFlow DRY RUN: matched torrent has no completed eligible video files.");
            progress.Report(100);
            return;
        }

        for (var i = 0; i < completedVideos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var file = completedVideos[i];

            try
            {
                var sourcePath = _pathMapper.BuildAndMap(torrent.SavePath, file.Name);
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException(
                        "Dry-run source file is not visible to Jellyfin/MediaFlow.",
                        sourcePath);
                }

                var parsed = _parser.Parse(sourcePath, torrent.Name, file.Name);
                ApplyCategoryKind(parsed, categoryKind);

                var resolution = await _resolver.ResolveAsync(parsed, cancellationToken).ConfigureAwait(false);

                var titles = string.Join(
                    " | ",
                    parsed.Titles
                        .OrderByDescending(x => x.Weight)
                        .Take(8)
                        .Select(x => $"{x.Value} [{x.Source}:{x.Weight:F2}]"));

                var years = string.Join(
                    " | ",
                    parsed.Years
                        .OrderByDescending(x => x.Weight)
                        .Take(6)
                        .Select(x => $"{x.Value} [{x.Source}:{x.Weight:F2}]"));

                var candidates = FormatCandidates(resolution.Candidates);
                var destination = resolution.Selected is null
                    ? "<none>"
                    : ImportPlanner.BuildDestination(parsed, resolution.Selected);

                _logger.LogWarning(
                    "MediaFlow DRY RUN FILE: {File}\n" +
                    "  kind={Kind} season={Season} episode={Episode}\n" +
                    "  titles={Titles}\n" +
                    "  years={Years}\n" +
                    "  resolver={Reason}\n" +
                    "  candidates={Candidates}\n" +
                    "  planned={Destination}",
                    file.Name,
                    parsed.Kind,
                    parsed.Season,
                    parsed.Episode,
                    titles,
                    years,
                    resolution.Reason,
                    candidates,
                    destination);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(
                    ex,
                    "MediaFlow DRY RUN failed for {Torrent}/{File}",
                    torrent.Name,
                    file.Name);
            }

            progress.Report(5 + ((i + 1) * 90.0 / completedVideos.Count));
        }

        _logger.LogWarning(
            "MediaFlow DRY RUN END: {Torrent}. No files were changed.",
            torrent.Name);
        progress.Report(100);
    }

    private static string FormatCandidates(IReadOnlyList<TmdbCandidate> candidates)
        => string.Join(
            " | ",
            candidates.Take(5).Select(
                x => $"{x.Title} ({x.Year}) #{x.Id} score={x.Score:F1} [{string.Join(",", x.Reasons)}]"));

    private static bool IsVideoFile(QbTorrentFile file, PluginConfiguration config)
    {
        var ext = Path.GetExtension(file.Name);
        return (config.VideoExtensions ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => string.Equals(
                x.StartsWith('.') ? x : "." + x,
                ext,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSample(string name)
    {
        var value = name.ToLowerInvariant();
        return value.Contains("sample", StringComparison.Ordinal)
            || value.Contains("trailer", StringComparison.Ordinal);
    }

    private static MediaKind GetCategoryKind(string category, PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.QbittorrentMovieCategory)
            && string.Equals(
                category,
                config.QbittorrentMovieCategory.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Movie;
        }

        if (!string.IsNullOrWhiteSpace(config.QbittorrentTvCategory)
            && string.Equals(
                category,
                config.QbittorrentTvCategory.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Episode;
        }

        return MediaKind.Unknown;
    }

    private static void ApplyCategoryKind(ParsedMedia parsed, MediaKind categoryKind)
    {
        if (categoryKind == MediaKind.Movie)
        {
            parsed.Kind = MediaKind.Movie;
            parsed.Season = null;
            parsed.Episode = null;
            return;
        }

        if (categoryKind == MediaKind.Episode)
        {
            parsed.Kind = parsed.Season.HasValue && parsed.Episode.HasValue
                ? MediaKind.Episode
                : MediaKind.Unknown;
        }
    }

    private static void ValidateConfiguration(PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.QbittorrentUrl))
        {
            throw new InvalidOperationException("qBittorrent URL is not configured.");
        }

        if (string.IsNullOrWhiteSpace(config.QbittorrentMovieCategory)
            && string.IsNullOrWhiteSpace(config.QbittorrentTvCategory))
        {
            throw new InvalidOperationException(
                "At least one qBittorrent movie/TV category must be configured.");
        }

        if (!string.IsNullOrWhiteSpace(config.QbittorrentMovieCategory)
            && !string.IsNullOrWhiteSpace(config.QbittorrentTvCategory)
            && string.Equals(
                config.QbittorrentMovieCategory.Trim(),
                config.QbittorrentTvCategory.Trim(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "qBittorrent movie and TV categories must be different.");
        }

        if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            throw new InvalidOperationException("TMDb API key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(config.MoviesRoot)
            || string.IsNullOrWhiteSpace(config.ShowsRoot))
        {
            throw new InvalidOperationException(
                "MoviesRoot and ShowsRoot must be configured.");
        }
    }
}
