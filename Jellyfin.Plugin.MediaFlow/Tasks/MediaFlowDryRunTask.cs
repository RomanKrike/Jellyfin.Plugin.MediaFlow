using System.Text;
using Jellyfin.Plugin.MediaFlow.Configuration;
using Jellyfin.Plugin.MediaFlow.Models;
using Jellyfin.Plugin.MediaFlow.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaFlow.Tasks;

/// <summary>
/// Manual, intrinsically read-only MediaFlow diagnostic task.
///
/// This task intentionally does not depend on HardLinkService, ImportStateStore,
/// ILibraryManager, or qBittorrent priority mutation methods. It can therefore be
/// used to validate parsing/TMDb matching even while the background worker is disabled.
/// Every run also writes a human-readable report into Jellyfin's plugin configuration folder.
/// </summary>
public sealed class MediaFlowDryRunTask : IScheduledTask
{
    private const string ReportFileName = "MediaFlow-dryrun-report.txt";

    private readonly QbittorrentClient _qbittorrent;
    private readonly MediaParser _parser;
    private readonly MediaResolver _resolver;
    private readonly PathMapper _pathMapper;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILogger<MediaFlowDryRunTask> _logger;

    public MediaFlowDryRunTask(
        QbittorrentClient qbittorrent,
        MediaParser parser,
        MediaResolver resolver,
        PathMapper pathMapper,
        IApplicationPaths applicationPaths,
        ILogger<MediaFlowDryRunTask> logger)
    {
        _qbittorrent = qbittorrent;
        _parser = parser;
        _resolver = resolver;
        _pathMapper = pathMapper;
        _applicationPaths = applicationPaths;
        _logger = logger;
    }

    public string Name => "MediaFlow: Dry Run";

    public string Key => "MediaFlowDryRun";

    public string Description =>
        "Safely analyzes one selected qBittorrent torrent with the MediaFlow parser and TMDb resolver. " +
        "Does not create hardlinks, alter qBittorrent priorities, write import state, or refresh Jellyfin libraries. " +
        "Writes MediaFlow-dryrun-report.txt to the Jellyfin plugin configuration directory.";

    public string Category => "MediaFlow";

    // No automatic triggers. This task is deliberately manual-only.
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var report = new List<string>();
        var reportPath = Path.Combine(_applicationPaths.PluginConfigurationsPath, ReportFileName);

        report.Add("MediaFlow Dry Run Report");
        report.Add("========================");
        report.Add($"Generated UTC: {DateTimeOffset.UtcNow:O}");
        report.Add($"Report path: {reportPath}");
        report.Add("Safety: READ-ONLY (no hardlinks, no import-state writes, no library scan, no qBittorrent priority changes)");
        report.Add(string.Empty);

        try
        {
            var config = Plugin.Instance?.Configuration
                ?? throw new InvalidOperationException("MediaFlow plugin configuration is not available.");

            ValidateConfiguration(config);

            var filter = config.DryRunTorrentFilter.Trim();
            if (string.IsNullOrWhiteSpace(filter))
            {
                report.Add("RESULT: INVALID_CONFIGURATION");
                report.Add("Reason: DryRunTorrentFilter is empty.");
                await WriteReportAsync(reportPath, report, CancellationToken.None).ConfigureAwait(false);
                throw new InvalidOperationException(
                    "DryRunTorrentFilter is empty. Set a unique torrent-name fragment or exact qBittorrent hash in MediaFlow settings first.");
            }

            var maxFiles = Math.Clamp(config.DryRunMaxFiles, 1, 20);
            report.Add($"Filter: {filter}");
            report.Add($"Max files: {maxFiles}");
            report.Add($"Movie category: {config.QbittorrentMovieCategory}");
            report.Add($"TV category: {config.QbittorrentTvCategory}");
            report.Add(string.Empty);

            _logger.LogWarning(
                "MediaFlow MANUAL DRY RUN TASK started. filter={Filter}, maxFiles={MaxFiles}. " +
                "This task is read-only: no hardlinks, import state, library scans, or qBittorrent priorities will be changed.",
                filter,
                maxFiles);

            progress.Report(2);

            var torrents = await _qbittorrent.GetTorrentsAsync(cancellationToken).ConfigureAwait(false);
            report.Add($"qBittorrent torrents returned: {torrents.Count}");

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
                report.Add(string.Empty);
                report.Add("RESULT: NO_MATCH");
                report.Add($"No torrent matched filter: {filter}");
                await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "MediaFlow MANUAL DRY RUN: no torrent matched filter '{Filter}'. Report: {ReportPath}",
                    filter,
                    reportPath);
                progress.Report(100);
                return;
            }

            if (matches.Count > 1)
            {
                report.Add(string.Empty);
                report.Add("RESULT: MULTIPLE_MATCHES");
                report.Add($"Filter matched {matches.Count} torrents. Use a more unique fragment or exact hash.");
                report.Add("Matches:");
                foreach (var match in matches.Take(20))
                {
                    report.Add($"  - {match.Name} | hash={match.Hash} | category={match.Category}");
                }

                await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "MediaFlow MANUAL DRY RUN: filter '{Filter}' matched {Count} torrents. Report: {ReportPath}",
                    filter,
                    matches.Count,
                    reportPath);
                progress.Report(100);
                return;
            }

            var torrent = matches[0];
            report.Add(string.Empty);
            report.Add("TORRENT");
            report.Add($"Name: {torrent.Name}");
            report.Add($"Hash: {torrent.Hash}");
            report.Add($"Category: {torrent.Category}");
            report.Add($"Save path: {torrent.SavePath}");

            var categoryKind = GetCategoryKind(torrent.Category, config);
            report.Add($"Category kind: {categoryKind}");

            if (categoryKind == MediaKind.Unknown)
            {
                report.Add(string.Empty);
                report.Add("RESULT: UNSUPPORTED_CATEGORY");
                report.Add(
                    $"Torrent category '{torrent.Category}' is neither configured movie category " +
                    $"'{config.QbittorrentMovieCategory}' nor TV category '{config.QbittorrentTvCategory}'.");
                await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "MediaFlow MANUAL DRY RUN: matched torrent '{Torrent}' has unsupported category '{Category}'. Report: {ReportPath}",
                    torrent.Name,
                    torrent.Category,
                    reportPath);
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
                .Take(maxFiles)
                .ToList();

            report.Add($"All torrent files: {files.Count}");
            report.Add($"Completed eligible videos selected: {completedVideos.Count}");

            _logger.LogWarning(
                "MediaFlow DRY RUN START: {Torrent} | hash={Hash} | category={Category} | " +
                "completed video files selected={Count}. No files or qBittorrent priorities will be changed.",
                torrent.Name,
                torrent.Hash,
                torrent.Category,
                completedVideos.Count);

            if (completedVideos.Count == 0)
            {
                report.Add(string.Empty);
                report.Add("RESULT: NO_ELIGIBLE_FILES");
                report.Add("The matched torrent has no completed video files that pass extension, size, and sample filters.");
                await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);

                _logger.LogWarning(
                    "MediaFlow DRY RUN: matched torrent has no completed eligible video files. Report: {ReportPath}",
                    reportPath);
                progress.Report(100);
                return;
            }

            var successfulFiles = 0;
            var failedFiles = 0;

            for (var i = 0; i < completedVideos.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var file = completedVideos[i];

                report.Add(string.Empty);
                report.Add($"FILE {i + 1}/{completedVideos.Count}");
                report.Add(new string('-', 72));
                report.Add($"qBittorrent file: {file.Name}");
                report.Add($"Size: {file.Size / 1024d / 1024d:F1} MiB");
                report.Add($"Progress: {file.Progress:P2}");

                try
                {
                    var sourcePath = _pathMapper.BuildAndMap(torrent.SavePath, file.Name);
                    report.Add($"Mapped source: {sourcePath}");

                    if (!File.Exists(sourcePath))
                    {
                        throw new FileNotFoundException(
                            "Dry-run source file is not visible to Jellyfin/MediaFlow.",
                            sourcePath);
                    }

                    var parsed = _parser.Parse(sourcePath, torrent.Name, file.Name);
                    ApplyCategoryKind(parsed, categoryKind);

                    var resolution = await _resolver.ResolveAsync(parsed, cancellationToken).ConfigureAwait(false);

                    var titles = parsed.Titles
                        .OrderByDescending(x => x.Weight)
                        .Take(12)
                        .Select(x => $"{x.Value} [{x.Source}:{x.Weight:F2}]")
                        .ToList();
                    var years = parsed.Years
                        .OrderByDescending(x => x.Weight)
                        .Take(8)
                        .Select(x => $"{x.Value} [{x.Source}:{x.Weight:F2}]")
                        .ToList();

                    report.Add($"Parsed kind: {parsed.Kind}");
                    report.Add($"Season: {parsed.Season?.ToString() ?? "<none>"}");
                    report.Add($"Episode: {parsed.Episode?.ToString() ?? "<none>"}");
                    report.Add("Titles:");
                    if (titles.Count == 0)
                    {
                        report.Add("  <none>");
                    }
                    else
                    {
                        foreach (var title in titles)
                        {
                            report.Add($"  - {title}");
                        }
                    }

                    report.Add("Years:");
                    if (years.Count == 0)
                    {
                        report.Add("  <none>");
                    }
                    else
                    {
                        foreach (var year in years)
                        {
                            report.Add($"  - {year}");
                        }
                    }

                    report.Add($"Resolver: {resolution.Reason}");
                    report.Add($"Auto-approved: {resolution.AutoApproved}");
                    report.Add("TMDb candidates:");
                    if (resolution.Candidates.Count == 0)
                    {
                        report.Add("  <none>");
                    }
                    else
                    {
                        foreach (var candidate in resolution.Candidates.Take(10))
                        {
                            report.Add(
                                $"  - {candidate.Title} ({candidate.Year}) #{candidate.Id} " +
                                $"score={candidate.Score:F1} [{string.Join(",", candidate.Reasons)}]");
                        }
                    }

                    if (resolution.Selected is null)
                    {
                        report.Add("Selected: <none>");
                        report.Add("Planned destination: <none>");
                    }
                    else
                    {
                        report.Add(
                            $"Selected: {resolution.Selected.Title} ({resolution.Selected.Year}) " +
                            $"#{resolution.Selected.Id} score={resolution.Selected.Score:F1}");
                        report.Add($"Planned destination: {ImportPlanner.BuildDestination(parsed, resolution.Selected)}");
                    }

                    successfulFiles++;

                    _logger.LogWarning(
                        "MediaFlow DRY RUN FILE: {File}\n" +
                        "  kind={Kind} season={Season} episode={Episode}\n" +
                        "  resolver={Reason}\n" +
                        "  planned={Destination}",
                        file.Name,
                        parsed.Kind,
                        parsed.Season,
                        parsed.Episode,
                        resolution.Reason,
                        resolution.Selected is null ? "<none>" : ImportPlanner.BuildDestination(parsed, resolution.Selected));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failedFiles++;
                    report.Add($"FILE RESULT: ERROR");
                    report.Add($"Error type: {ex.GetType().FullName}");
                    report.Add($"Error: {ex.Message}");
                    if (ex is FileNotFoundException fileNotFound && !string.IsNullOrWhiteSpace(fileNotFound.FileName))
                    {
                        report.Add($"Missing path: {fileNotFound.FileName}");
                    }

                    _logger.LogError(
                        ex,
                        "MediaFlow DRY RUN failed for {Torrent}/{File}",
                        torrent.Name,
                        file.Name);
                }

                progress.Report(5 + ((i + 1) * 90.0 / completedVideos.Count));
            }

            report.Add(string.Empty);
            report.Add("SUMMARY");
            report.Add($"Files selected: {completedVideos.Count}");
            report.Add($"Files analyzed successfully: {successfulFiles}");
            report.Add($"Files with errors: {failedFiles}");
            report.Add($"RESULT: {(failedFiles == 0 ? "SUCCESS" : successfulFiles > 0 ? "PARTIAL_ERRORS" : "ERROR")}");
            report.Add("No files were changed.");

            await WriteReportAsync(reportPath, report, cancellationToken).ConfigureAwait(false);

            _logger.LogWarning(
                "MediaFlow DRY RUN END: {Torrent}. No files were changed. Report: {ReportPath}",
                torrent.Name,
                reportPath);

            progress.Report(100);
        }
        catch (OperationCanceledException)
        {
            report.Add(string.Empty);
            report.Add("RESULT: CANCELLED");
            await TryWriteReportAsync(reportPath, report).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            report.Add(string.Empty);
            report.Add("RESULT: FATAL_ERROR");
            report.Add($"Error type: {ex.GetType().FullName}");
            report.Add($"Error: {ex.Message}");
            await TryWriteReportAsync(reportPath, report).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task WriteReportAsync(
        string reportPath,
        IReadOnlyCollection<string> report,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(reportPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var text = string.Join(Environment.NewLine, report) + Environment.NewLine;
        await File.WriteAllTextAsync(
            reportPath,
            text,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task TryWriteReportAsync(string reportPath, IReadOnlyCollection<string> report)
    {
        try
        {
            await WriteReportAsync(reportPath, report, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Never hide the original task failure just because the diagnostic report could not be written.
        }
    }

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
