using Jellyfin.Plugin.MediaFlow.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class MediaFlowWorker : BackgroundService
{
    private const string BaselineMarkerKey = "__mediaflow_baseline_v1";
    private const string BaselineTorrentPrefix = "__mediaflow_torrent_baseline:";

    private readonly QbittorrentClient _qbittorrent;
    private readonly MediaParser _parser;
    private readonly MediaResolver _resolver;
    private readonly PathMapper _pathMapper;
    private readonly HardLinkService _hardLinks;
    private readonly ImportStateStore _state;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaFlowWorker> _logger;
    private string _lastDryRunSignature = string.Empty;

    public MediaFlowWorker(
        QbittorrentClient qbittorrent,
        MediaParser parser,
        MediaResolver resolver,
        PathMapper pathMapper,
        HardLinkService hardLinks,
        ImportStateStore state,
        ILibraryManager libraryManager,
        ILogger<MediaFlowWorker> logger)
    {
        _qbittorrent = qbittorrent;
        _parser = parser;
        _resolver = resolver;
        _pathMapper = pathMapper;
        _hardLinks = hardLinks;
        _state = state;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning("MediaFlow worker started. Background polling service is active.");

        while (!stoppingToken.IsCancellationRequested)
        {
            var config = Plugin.Instance?.Configuration;
            var delay = Math.Clamp(config?.PollIntervalSeconds ?? 10, 3, 300);
            try
            {
                if (config?.Enabled == true)
                {
                    ValidateConfiguration(config);
                    await ProcessCycleAsync(stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "MediaFlow cycle failed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task ProcessCycleAsync(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var torrents = await _qbittorrent.GetTorrentsAsync(cancellationToken).ConfigureAwait(false);

        if (config.DryRunMode)
        {
            await ProcessDryRunAsync(torrents, cancellationToken).ConfigureAwait(false);
            return;
        }

        _lastDryRunSignature = string.Empty;

        if (await CreateInitialBaselineIfNeededAsync(torrents, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var importedAny = false;
        foreach (var torrent in torrents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var categoryKind = GetCategoryKind(torrent.Category, config);
            if (categoryKind == MediaKind.Unknown)
            {
                continue;
            }

            if (await IsBaselineTorrentAsync(torrent.Hash, cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            var files = await _qbittorrent.GetFilesAsync(torrent.Hash, cancellationToken).ConfigureAwait(false);

            if (config.ManageSequentialEpisodes && categoryKind == MediaKind.Episode)
            {
                await ApplyStrictEpisodeSequenceAsync(torrent, files, cancellationToken).ConfigureAwait(false);
            }

            foreach (var file in files.Where(IsVideoFile).Where(x => x.Progress >= 0.999999))
            {
                if (file.Size < config.MinimumVideoSizeMb * 1024L * 1024L || IsSample(file.Name))
                {
                    continue;
                }

                var key = $"{torrent.Hash}:{file.Index}";
                var existing = await _state.GetAsync(key, cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    if (existing.Status is "Imported" or "NeedsReview")
                    {
                        continue;
                    }

                    if (existing.Status == "Failed" && DateTimeOffset.UtcNow - existing.UpdatedAt < TimeSpan.FromMinutes(config.RetryFailedAfterMinutes))
                    {
                        continue;
                    }
                }

                var sourcePath = string.Empty;
                try
                {
                    sourcePath = _pathMapper.BuildAndMap(torrent.SavePath, file.Name);
                    if (!File.Exists(sourcePath))
                    {
                        throw new FileNotFoundException("qBittorrent reports file complete but it is not visible to Jellyfin/MediaFlow.", sourcePath);
                    }

                    var parsed = _parser.Parse(sourcePath, torrent.Name, file.Name);
                    ApplyCategoryKind(parsed, categoryKind);
                    var resolution = await _resolver.ResolveAsync(parsed, cancellationToken).ConfigureAwait(false);
                    if (!resolution.AutoApproved || resolution.Selected is null)
                    {
                        var top = FormatCandidates(resolution.Candidates);
                        await _state.SetAsync(new ImportStateEntry
                        {
                            Key = key,
                            Status = "NeedsReview",
                            SourcePath = sourcePath,
                            Message = resolution.Reason + " " + top
                        }, cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning("NEEDS REVIEW: {File}. {Reason}. Candidates: {Candidates}", file.Name, resolution.Reason, top);
                        continue;
                    }

                    var destination = ImportPlanner.BuildDestination(parsed, resolution.Selected);
                    _hardLinks.Create(sourcePath, destination);
                    importedAny = true;
                    await _state.SetAsync(new ImportStateEntry
                    {
                        Key = key,
                        Status = "Imported",
                        SourcePath = sourcePath,
                        DestinationPath = destination,
                        TmdbId = resolution.Selected.Id,
                        Message = resolution.Reason
                    }, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Imported {Source} -> {Destination} (TMDb {TmdbId}, score {Score:F1})", sourcePath, destination, resolution.Selected.Id, resolution.Selected.Score);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await _state.SetAsync(new ImportStateEntry
                    {
                        Key = key,
                        Status = "Failed",
                        SourcePath = sourcePath,
                        Message = ex.Message
                    }, cancellationToken).ConfigureAwait(false);
                    _logger.LogError(ex, "Failed to import torrent file {Torrent}/{File}", torrent.Name, file.Name);
                }
            }
        }

        if (importedAny)
        {
            _libraryManager.QueueLibraryScan();
            _logger.LogInformation("Queued Jellyfin library scan after MediaFlow imports.");
        }
    }

    private async Task ProcessDryRunAsync(IReadOnlyList<QbTorrent> torrents, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        var filter = config.DryRunTorrentFilter.Trim();
        var signature = filter + "|" + config.DryRunMaxFiles.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (string.Equals(signature, _lastDryRunSignature, StringComparison.Ordinal))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(filter))
        {
            _lastDryRunSignature = signature;
            _logger.LogWarning("MediaFlow DRY RUN is enabled, but DryRunTorrentFilter is empty. Nothing will be analyzed.");
            return;
        }

        var exactHash = torrents.Where(x => string.Equals(x.Hash, filter, StringComparison.OrdinalIgnoreCase)).ToList();
        var matches = exactHash.Count > 0
            ? exactHash
            : torrents.Where(x => x.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();

        if (matches.Count == 0)
        {
            _lastDryRunSignature = signature;
            _logger.LogWarning("MediaFlow DRY RUN: no movie/TV torrent matched filter '{Filter}'.", filter);
            return;
        }

        if (matches.Count > 1)
        {
            _lastDryRunSignature = signature;
            _logger.LogWarning(
                "MediaFlow DRY RUN: filter '{Filter}' matched {Count} torrents. Make the filter unique or use the exact hash. Matches: {Matches}",
                filter,
                matches.Count,
                string.Join(" | ", matches.Take(8).Select(x => x.Name)));
            return;
        }

        var torrent = matches[0];
        var categoryKind = GetCategoryKind(torrent.Category, config);
        var files = await _qbittorrent.GetFilesAsync(torrent.Hash, cancellationToken).ConfigureAwait(false);
        var completedVideos = files
            .Where(IsVideoFile)
            .Where(x => x.Progress >= 0.999999)
            .Where(x => x.Size >= config.MinimumVideoSizeMb * 1024L * 1024L)
            .Where(x => !IsSample(x.Name))
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(config.DryRunMaxFiles, 1, 20))
            .ToList();

        _logger.LogWarning(
            "MediaFlow DRY RUN START: {Torrent} | hash={Hash} | category={Category} | completed video files selected={Count}. No files or qBittorrent priorities will be changed.",
            torrent.Name,
            torrent.Hash,
            torrent.Category,
            completedVideos.Count);

        if (completedVideos.Count == 0)
        {
            _lastDryRunSignature = signature;
            _logger.LogWarning("MediaFlow DRY RUN: matched torrent has no completed eligible video files.");
            return;
        }

        foreach (var file in completedVideos)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var sourcePath = _pathMapper.BuildAndMap(torrent.SavePath, file.Name);
                if (!File.Exists(sourcePath))
                {
                    throw new FileNotFoundException("Dry-run source file is not visible to Jellyfin/MediaFlow.", sourcePath);
                }

                var parsed = _parser.Parse(sourcePath, torrent.Name, file.Name);
                ApplyCategoryKind(parsed, categoryKind);
                var resolution = await _resolver.ResolveAsync(parsed, cancellationToken).ConfigureAwait(false);

                var titles = string.Join(" | ", parsed.Titles.OrderByDescending(x => x.Weight).Take(8).Select(x => $"{x.Value} [{x.Source}:{x.Weight:F2}]"));
                var years = string.Join(" | ", parsed.Years.OrderByDescending(x => x.Weight).Take(6).Select(x => $"{x.Value} [{x.Source}:{x.Weight:F2}]"));
                var top = FormatCandidates(resolution.Candidates);
                var destination = resolution.Selected is null ? "<none>" : ImportPlanner.BuildDestination(parsed, resolution.Selected);

                _logger.LogWarning(
                    "MediaFlow DRY RUN FILE: {File}\n  kind={Kind} season={Season} episode={Episode}\n  titles={Titles}\n  years={Years}\n  resolver={Reason}\n  candidates={Candidates}\n  planned={Destination}",
                    file.Name,
                    parsed.Kind,
                    parsed.Season,
                    parsed.Episode,
                    titles,
                    years,
                    resolution.Reason,
                    top,
                    destination);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "MediaFlow DRY RUN failed for {Torrent}/{File}", torrent.Name, file.Name);
            }
        }

        _lastDryRunSignature = signature;
        _logger.LogWarning("MediaFlow DRY RUN END: {Torrent}. No files were changed.", torrent.Name);
    }

    private async Task<bool> CreateInitialBaselineIfNeededAsync(IReadOnlyList<QbTorrent> torrents, CancellationToken cancellationToken)
    {
        var config = Plugin.Instance!.Configuration;
        if (!config.BaselineExistingTorrentsOnFirstRun)
        {
            return false;
        }

        var marker = await _state.GetAsync(BaselineMarkerKey, cancellationToken).ConfigureAwait(false);
        if (marker is not null)
        {
            return false;
        }

        var count = 0;
        foreach (var torrent in torrents)
        {
            if (GetCategoryKind(torrent.Category, config) == MediaKind.Unknown)
            {
                continue;
            }

            await _state.SetAsync(new ImportStateEntry
            {
                Key = BaselineTorrentPrefix + torrent.Hash,
                Status = "BaselineTorrent",
                SourcePath = torrent.SavePath,
                Message = torrent.Name
            }, cancellationToken).ConfigureAwait(false);
            count++;
        }

        await _state.SetAsync(new ImportStateEntry
        {
            Key = BaselineMarkerKey,
            Status = "BaselineComplete",
            SourcePath = string.Empty,
            Message = $"Marked {count} existing qBittorrent movie/TV torrents as baseline."
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "MediaFlow initial baseline created: {Count} existing movie/TV torrents will be ignored. Only torrents added after this point will be automated.",
            count);
        return true;
    }

    private async Task<bool> IsBaselineTorrentAsync(string hash, CancellationToken cancellationToken)
    {
        var entry = await _state.GetAsync(BaselineTorrentPrefix + hash, cancellationToken).ConfigureAwait(false);
        return entry?.Status == "BaselineTorrent";
    }

    private async Task ApplyStrictEpisodeSequenceAsync(QbTorrent torrent, IReadOnlyList<QbTorrentFile> files, CancellationToken cancellationToken)
    {
        var episodes = files
            .Where(IsVideoFile)
            .Select(file => (File: file, Numbers: _parser.FindEpisodeNumbers(file.Name, torrent.Name)))
            .Where(x => x.Numbers.HasValue)
            .Select(x => (x.File, Season: x.Numbers!.Value.Season, Episode: x.Numbers.Value.Episode))
            .OrderBy(x => x.Season)
            .ThenBy(x => x.Episode)
            .ToList();
        if (episodes.Count < 2)
        {
            return;
        }

        var incomplete = episodes.Where(x => x.File.Progress < 0.999999).ToList();
        if (incomplete.Count == 0)
        {
            return;
        }

        var next = incomplete[0];
        if (next.File.Priority != 7)
        {
            await _qbittorrent.SetFilePriorityAsync(torrent.Hash, next.File.Index, 7, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Sequential mode: prioritizing {Torrent} S{Season:00}E{Episode:00}", torrent.Name, next.Season, next.Episode);
        }

        foreach (var later in incomplete.Skip(1))
        {
            if (later.File.Priority != 0)
            {
                await _qbittorrent.SetFilePriorityAsync(torrent.Hash, later.File.Index, 0, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsVideoFile(QbTorrentFile file)
    {
        var config = Plugin.Instance?.Configuration;
        var ext = Path.GetExtension(file.Name);
        return (config?.VideoExtensions ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(x => string.Equals(x.StartsWith('.') ? x : "." + x, ext, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsSample(string name)
    {
        var value = name.ToLowerInvariant();
        return value.Contains("sample", StringComparison.Ordinal) || value.Contains("trailer", StringComparison.Ordinal);
    }

    private static string FormatCandidates(IReadOnlyList<TmdbCandidate> candidates)
        => string.Join(" | ", candidates.Take(5).Select(x => $"{x.Title} ({x.Year}) #{x.Id} score={x.Score:F1} [{string.Join(",", x.Reasons)}]"));

    private static MediaKind GetCategoryKind(string category, Configuration.PluginConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.QbittorrentMovieCategory)
            && string.Equals(category, config.QbittorrentMovieCategory.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return MediaKind.Movie;
        }

        if (!string.IsNullOrWhiteSpace(config.QbittorrentTvCategory)
            && string.Equals(category, config.QbittorrentTvCategory.Trim(), StringComparison.OrdinalIgnoreCase))
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

    private static void ValidateConfiguration(Configuration.PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.QbittorrentUrl))
        {
            throw new InvalidOperationException("qBittorrent URL is not configured.");
        }

        if (string.IsNullOrWhiteSpace(config.QbittorrentMovieCategory) && string.IsNullOrWhiteSpace(config.QbittorrentTvCategory))
        {
            throw new InvalidOperationException("At least one qBittorrent movie/TV category must be configured.");
        }

        if (!string.IsNullOrWhiteSpace(config.QbittorrentMovieCategory)
            && !string.IsNullOrWhiteSpace(config.QbittorrentTvCategory)
            && string.Equals(config.QbittorrentMovieCategory.Trim(), config.QbittorrentTvCategory.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("qBittorrent movie and TV categories must be different.");
        }

        if (string.IsNullOrWhiteSpace(config.TmdbApiKey))
        {
            throw new InvalidOperationException("TMDb API key is not configured.");
        }

        if (string.IsNullOrWhiteSpace(config.MoviesRoot) || string.IsNullOrWhiteSpace(config.ShowsRoot))
        {
            throw new InvalidOperationException("MoviesRoot and ShowsRoot must be configured.");
        }
    }
}
