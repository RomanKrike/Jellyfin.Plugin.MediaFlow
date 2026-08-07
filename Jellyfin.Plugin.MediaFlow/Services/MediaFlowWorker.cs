using Jellyfin.Plugin.MediaFlow.Models;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class MediaFlowWorker : BackgroundService
{
    private readonly QbittorrentClient _qbittorrent;
    private readonly MediaParser _parser;
    private readonly MediaResolver _resolver;
    private readonly PathMapper _pathMapper;
    private readonly HardLinkService _hardLinks;
    private readonly ImportStateStore _state;
    private readonly ILibraryManager _libraryManager;
    private readonly ILogger<MediaFlowWorker> _logger;

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
        _logger.LogInformation("MediaFlow worker started.");

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
        var importedAny = false;

        foreach (var torrent in torrents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var files = await _qbittorrent.GetFilesAsync(torrent.Hash, cancellationToken).ConfigureAwait(false);

            if (config.ManageSequentialEpisodes)
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
                    var resolution = await _resolver.ResolveAsync(parsed, cancellationToken).ConfigureAwait(false);
                    if (!resolution.AutoApproved || resolution.Selected is null)
                    {
                        var top = string.Join(" | ", resolution.Candidates.Take(3).Select(x => $"{x.Title} ({x.Year}) #{x.Id} score={x.Score:F1}"));
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

    private static void ValidateConfiguration(Configuration.PluginConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.QbittorrentUrl))
        {
            throw new InvalidOperationException("qBittorrent URL is not configured.");
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
