using Jellyfin.Plugin.MediaFlow.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class MediaFlowWorker : BackgroundService
{
    private const string BaselineMarkerKey = "__mediaflow_baseline_v1";
    private const string BaselineTorrentPrefix = "__mediaflow_torrent_baseline:";
    private const string TorrentIdentityPrefix = "__mediaflow_torrent_identity:";

    private readonly QbittorrentClient _qbittorrent;
    private readonly MediaParser _parser;
    private readonly MediaResolver _resolver;
    private readonly TmdbClient _tmdb;
    private readonly PathMapper _pathMapper;
    private readonly HardLinkService _hardLinks;
    private readonly ImportStateStore _state;
    private readonly MediaFlowLogStore _activityLog;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaFlowWorker> _logger;
    private string _lastDryRunSignature = string.Empty;

    public MediaFlowWorker(
        QbittorrentClient qbittorrent,
        MediaParser parser,
        MediaResolver resolver,
        TmdbClient tmdb,
        PathMapper pathMapper,
        HardLinkService hardLinks,
        ImportStateStore state,
        MediaFlowLogStore activityLog,
        ILibraryManager libraryManager,
        ILogger<MediaFlowWorker> logger)
    {
        _qbittorrent = qbittorrent;
        _parser = parser;
        _resolver = resolver;
        _tmdb = tmdb;
        _pathMapper = pathMapper;
        _hardLinks = hardLinks;
        _state = state;
        _activityLog = activityLog;
        _libraryManager = libraryManager;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning("MediaFlow worker started. Background polling service is active.");
        await _activityLog.AddAsync("Information", "Worker", "MediaFlow worker started.", stoppingToken).ConfigureAwait(false);

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
                await _activityLog.AddAsync("Error", "Worker", "MediaFlow cycle failed: " + ex.Message, stoppingToken).ConfigureAwait(false);
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

            // Identify the torrent before the first media file finishes downloading.
            // This gives the admin UI a TMDb title/poster immediately and also lets
            // completed episodes reuse one torrent-level series match instead of
            // running a full TMDb search for every episode.
            var torrentIdentity = await EnsureTorrentIdentityAsync(
                torrent,
                files,
                categoryKind,
                cancellationToken).ConfigureAwait(false);

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
                    if (existing.Status is "Imported" or "NeedsReview" or "Ignored")
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

                    ResolutionResult resolution;
                    if (CanReuseTorrentIdentity(torrentIdentity, parsed))
                    {
                        var candidate = await _tmdb.GetCandidateByIdAsync(
                            parsed.Kind,
                            torrentIdentity!.TmdbId!.Value,
                            parsed.Season,
                            parsed.Episode,
                            cancellationToken).ConfigureAwait(false);
                        candidate.Score = 100;
                        candidate.Reasons.Add("torrentIdentity=cached");
                        resolution = new ResolutionResult
                        {
                            AutoApproved = true,
                            Selected = candidate,
                            Candidates = [candidate],
                            Reason = "Reused torrent-level TMDb identity #" + candidate.Id + "."
                        };
                    }
                    else
                    {
                        resolution = await _resolver.ResolveAsync(parsed, cancellationToken).ConfigureAwait(false);
                    }

                    if (!resolution.AutoApproved || resolution.Selected is null)
                    {
                        var top = FormatCandidates(resolution.Candidates);
                        await _state.SetAsync(new ImportStateEntry
                        {
                            Key = key,
                            Status = "NeedsReview",
                            SourcePath = sourcePath,
                            Message = resolution.Reason + " " + top,
                            Kind = parsed.Kind,
                            Season = parsed.Season,
                            Episode = parsed.Episode,
                            ReviewCandidates = resolution.Candidates
                                .Take(6)
                                .Select(ReviewCandidateSnapshot.FromCandidate)
                                .ToList()
                        }, cancellationToken).ConfigureAwait(false);
                        _logger.LogWarning("NEEDS REVIEW: {File}. {Reason}. Candidates: {Candidates}", file.Name, resolution.Reason, top);
                        await _activityLog.AddAsync("Warning", "Resolver", "Needs review: " + resolution.Reason, cancellationToken, torrent.Hash, torrent.Name, file.Name).ConfigureAwait(false);
                        continue;
                    }

                    if (torrentIdentity?.TmdbId is not > 0)
                    {
                        torrentIdentity = await SaveMatchedTorrentIdentityAsync(
                            torrent,
                            files,
                            categoryKind,
                            resolution.Selected,
                            resolution.Reason,
                            cancellationToken).ConfigureAwait(false);
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
                        Kind = parsed.Kind,
                        Season = parsed.Season,
                        Episode = parsed.Episode,
                        MediaTitle = resolution.Selected.Title,
                        MediaYear = resolution.Selected.Year,
                        PosterPath = resolution.Selected.PosterPath,
                        EpisodeTitle = resolution.Selected.EpisodeTitle,
                        Message = resolution.Reason
                    }, cancellationToken).ConfigureAwait(false);
                    _logger.LogInformation("Imported {Source} -> {Destination} (TMDb {TmdbId}, score {Score:F1})", sourcePath, destination, resolution.Selected.Id, resolution.Selected.Score);
                    await _activityLog.AddAsync("Information", "Importer", "Imported to Jellyfin: " + destination + " (TMDb #" + resolution.Selected.Id + ")", cancellationToken, torrent.Hash, torrent.Name, file.Name).ConfigureAwait(false);
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
                    await _activityLog.AddAsync("Error", "Importer", ex.Message, cancellationToken, torrent.Hash, torrent.Name, file.Name).ConfigureAwait(false);
                }
            }
        }

        if (importedAny)
        {
            _libraryManager.QueueLibraryScan();
            _logger.LogInformation("Queued Jellyfin library scan after MediaFlow imports.");
            await _activityLog.AddAsync("Information", "Jellyfin", "Queued library scan after MediaFlow imports.", cancellationToken).ConfigureAwait(false);
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

    private async Task<ImportStateEntry?> EnsureTorrentIdentityAsync(
        QbTorrent torrent,
        IReadOnlyList<QbTorrentFile> files,
        MediaKind categoryKind,
        CancellationToken cancellationToken)
    {
        var key = TorrentIdentityPrefix + torrent.Hash;
        var existing = await _state.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (existing?.TmdbId is > 0 && string.Equals(existing.Status, "IdentityMatched", StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        // A low-confidence identity should not hammer TMDb every poll cycle. Reprocess
        // explicitly removes the identity record when the user wants another search.
        if (existing is not null
            && string.Equals(existing.Status, "IdentityNeedsReview", StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        var config = Plugin.Instance!.Configuration;
        if (existing is not null
            && string.Equals(existing.Status, "IdentityFailed", StringComparison.OrdinalIgnoreCase)
            && DateTimeOffset.UtcNow - existing.UpdatedAt < TimeSpan.FromMinutes(config.RetryFailedAfterMinutes))
        {
            return existing;
        }

        var eligible = files
            .Where(IsVideoFile)
            .Where(x => x.Size >= config.MinimumVideoSizeMb * 1024L * 1024L)
            .Where(x => !IsSample(x.Name))
            .ToList();
        if (eligible.Count == 0)
        {
            return existing;
        }

        var seasons = DetectTorrentSeasons(torrent, eligible);
        var representative = categoryKind == MediaKind.Episode
            ? eligible
                .Select(x => (File: x, Numbers: _parser.FindEpisodeNumbers(x.Name, torrent.Name)))
                .OrderBy(x => x.Numbers?.Season ?? int.MaxValue)
                .ThenBy(x => x.Numbers?.Episode ?? int.MaxValue)
                .ThenBy(x => x.File.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.File)
                .First()
            : eligible.OrderByDescending(x => x.Size).First();

        try
        {
            var sourcePath = _pathMapper.BuildAndMap(torrent.SavePath, representative.Name);
            var parsed = _parser.Parse(sourcePath, torrent.Name, representative.Name);
            ApplyCategoryKind(parsed, categoryKind);
            if (parsed.Kind == MediaKind.Unknown || parsed.Titles.Count == 0)
            {
                return existing;
            }

            var resolution = await _resolver.ResolveAsync(parsed, cancellationToken).ConfigureAwait(false);
            if (!resolution.AutoApproved || resolution.Selected is null)
            {
                var review = new ImportStateEntry
                {
                    Key = key,
                    Status = "IdentityNeedsReview",
                    SourcePath = torrent.SavePath,
                    Kind = categoryKind,
                    Season = seasons.Count == 1 ? seasons[0] : null,
                    Seasons = seasons,
                    Message = resolution.Reason,
                    ReviewCandidates = resolution.Candidates
                        .Take(6)
                        .Select(ReviewCandidateSnapshot.FromCandidate)
                        .ToList()
                };
                await _state.SetAsync(review, cancellationToken).ConfigureAwait(false);
                await _activityLog.AddAsync(
                    "Warning",
                    "Identity",
                    "Torrent identity needs review: " + resolution.Reason,
                    cancellationToken,
                    torrent.Hash,
                    torrent.Name,
                    representative.Name).ConfigureAwait(false);
                return review;
            }

            return await SaveMatchedTorrentIdentityAsync(
                torrent,
                files,
                categoryKind,
                resolution.Selected,
                resolution.Reason,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var failed = new ImportStateEntry
            {
                Key = key,
                Status = "IdentityFailed",
                SourcePath = torrent.SavePath,
                Kind = categoryKind,
                Season = seasons.Count == 1 ? seasons[0] : null,
                Seasons = seasons,
                Message = ex.Message
            };
            await _state.SetAsync(failed, cancellationToken).ConfigureAwait(false);
            _logger.LogWarning(ex, "Could not resolve torrent-level identity for {Torrent}", torrent.Name);
            await _activityLog.AddAsync(
                "Warning",
                "Identity",
                "Torrent-level TMDb identification failed: " + ex.Message,
                cancellationToken,
                torrent.Hash,
                torrent.Name,
                representative.Name).ConfigureAwait(false);
            return failed;
        }
    }

    private async Task<ImportStateEntry> SaveMatchedTorrentIdentityAsync(
        QbTorrent torrent,
        IReadOnlyList<QbTorrentFile> files,
        MediaKind categoryKind,
        TmdbCandidate candidate,
        string reason,
        CancellationToken cancellationToken)
    {
        var seasons = DetectTorrentSeasons(torrent, files.Where(IsVideoFile));
        var identity = new ImportStateEntry
        {
            Key = TorrentIdentityPrefix + torrent.Hash,
            Status = "IdentityMatched",
            SourcePath = torrent.SavePath,
            TmdbId = candidate.Id,
            Kind = categoryKind,
            Season = seasons.Count == 1 ? seasons[0] : null,
            Seasons = seasons,
            MediaTitle = candidate.Title,
            MediaYear = candidate.Year,
            PosterPath = candidate.PosterPath,
            Message = reason
        };
        await _state.SetAsync(identity, cancellationToken).ConfigureAwait(false);
        await _activityLog.AddAsync(
            "Information",
            "Identity",
            "Torrent identified as " + candidate.Title + " (TMDb #" + candidate.Id + ").",
            cancellationToken,
            torrent.Hash,
            torrent.Name).ConfigureAwait(false);
        return identity;
    }

    private List<int> DetectTorrentSeasons(QbTorrent torrent, IEnumerable<QbTorrentFile> files)
    {
        var seasons = new SortedSet<int>();
        foreach (var file in files)
        {
            var numbers = _parser.FindEpisodeNumbers(file.Name, torrent.Name);
            if (numbers.HasValue && numbers.Value.Season >= 0 && numbers.Value.Season <= 999)
            {
                seasons.Add(numbers.Value.Season);
            }
        }

        // Some multi-season packs use generic episode filenames but declare the
        // season range in the torrent name, e.g. "Season 1-3" / "Сезон: 1-3".
        // Merge that declaration as a fallback so the card can still show all seasons.
        var rangeMatches = System.Text.RegularExpressions.Regex.Matches(
            torrent.Name,
            @"(?i)(?:S|Season[ ._:\-]*|Сезон[ ._:\-]*)(?<from>\d{1,2})[ ._:\-]*(?:-|–|—|to|до)[ ._:\-]*(?:S|Season[ ._:\-]*|Сезон[ ._:\-]*)?(?<to>\d{1,2})");
        foreach (System.Text.RegularExpressions.Match match in rangeMatches)
        {
            if (!int.TryParse(match.Groups["from"].Value, out var from)
                || !int.TryParse(match.Groups["to"].Value, out var to))
            {
                continue;
            }

            var low = Math.Min(from, to);
            var high = Math.Max(from, to);
            if (low < 0 || high > 99 || high - low > 30)
            {
                continue;
            }

            for (var season = low; season <= high; season++)
            {
                seasons.Add(season);
            }
        }

        return seasons.ToList();
    }

    private static bool CanReuseTorrentIdentity(ImportStateEntry? identity, ParsedMedia parsed)
    {
        if (identity?.TmdbId is not > 0 || !string.Equals(identity.Status, "IdentityMatched", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (parsed.Kind == MediaKind.Movie)
        {
            return identity.Kind == MediaKind.Movie;
        }

        return parsed.Kind == MediaKind.Episode
            && identity.Kind == MediaKind.Episode
            && parsed.Season.HasValue
            && parsed.Episode.HasValue;
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
        await _activityLog.AddAsync("Information", "Worker", "Initial baseline created for " + count + " torrents.", cancellationToken).ConfigureAwait(false);
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

        // Keep every recognized episode selected in qBittorrent. Previous MediaFlow versions
        // used priority 0 (Do not download) for future episodes. qBittorrent then reported the
        // torrent as 100% complete as soon as the currently selected subset finished, even though
        // later episodes were still excluded. That made both qBittorrent and MediaFlow show a
        // misleading completed/uploading state.
        //
        // Priority-based sequencing keeps the whole season selected:
        //   current incomplete episode -> Maximum (7)
        //   every other episode        -> Normal  (1)
        // qBittorrent's native sequential piece mode is also enabled once for the torrent.
        // This strongly prefers the earliest/current episode without corrupting overall progress.
        if (!torrent.SequentialDownload)
        {
            await _qbittorrent.ToggleSequentialDownloadAsync(torrent.Hash, cancellationToken).ConfigureAwait(false);
            torrent.SequentialDownload = true;
            _logger.LogInformation("Sequential mode: enabled qBittorrent sequential download for {Torrent}", torrent.Name);
            await _activityLog.AddAsync("Information", "Sequential", "Enabled qBittorrent sequential download.", cancellationToken, torrent.Hash, torrent.Name).ConfigureAwait(false);
        }

        var next = incomplete[0];
        foreach (var episode in episodes)
        {
            var targetPriority = episode.File.Index == next.File.Index ? 7 : 1;
            if (episode.File.Priority == targetPriority)
            {
                continue;
            }

            await _qbittorrent.SetFilePriorityAsync(torrent.Hash, episode.File.Index, targetPriority, cancellationToken).ConfigureAwait(false);
            episode.File.Priority = targetPriority;
        }

        _logger.LogInformation("Sequential mode: prioritizing {Torrent} S{Season:00}E{Episode:00}; future episodes remain selected at normal priority", torrent.Name, next.Season, next.Episode);
        await _activityLog.AddAsync("Information", "Sequential", $"Prioritized S{next.Season:00}E{next.Episode:00}; future episodes remain selected.", cancellationToken, torrent.Hash, torrent.Name, next.File.Name).ConfigureAwait(false);
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
