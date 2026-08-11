using System.Net.Mime;
using Jellyfin.Plugin.MediaFlow.Configuration;
using Jellyfin.Plugin.MediaFlow.Models;
using Jellyfin.Plugin.MediaFlow.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaFlow.Controllers;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MediaFlow/Admin")]
[Produces(MediaTypeNames.Application.Json)]
public sealed class MediaFlowAdminController : ControllerBase
{
    private const string BaselineMarkerKey = "__mediaflow_baseline_v1";
    private const string BaselineTorrentPrefix = "__mediaflow_torrent_baseline:";
    private readonly QbittorrentClient _qbittorrent;
    private readonly TmdbClient _tmdb;
    private readonly HardLinkService _hardLinks;
    private readonly ImportStateStore _state;
    private readonly ILibraryManager _libraryManager;

    public MediaFlowAdminController(
        QbittorrentClient qbittorrent,
        TmdbClient tmdb,
        HardLinkService hardLinks,
        ImportStateStore state,
        ILibraryManager libraryManager)
    {
        _qbittorrent = qbittorrent;
        _tmdb = tmdb;
        _hardLinks = hardLinks;
        _state = state;
        _libraryManager = libraryManager;
    }

    [HttpGet("overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> GetOverview(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("MediaFlow plugin configuration is not available.");

        var state = await _state.GetAllAsync(cancellationToken).ConfigureAwait(false);

        IReadOnlyList<QbTorrent> torrents = [];
        var qbittorrentConnected = false;
        var qbittorrentMessage = "Not checked";
        try
        {
            torrents = await _qbittorrent.GetTorrentsAsync(cancellationToken).ConfigureAwait(false);
            qbittorrentConnected = true;
            qbittorrentMessage = "Connected";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            qbittorrentMessage = ex.Message;
        }

        var tmdbHealth = await _tmdb.CheckHealthAsync(cancellationToken).ConfigureAwait(false);

        var rows = torrents
            .Select(torrent => BuildTorrentRow(torrent, state))
            .OrderByDescending(x => x.AddedOn)
            .ToList();

        var history = state
            .Where(x => !x.Key.StartsWith("__mediaflow_", StringComparison.Ordinal))
            .OrderByDescending(x => x.Value.UpdatedAt)
            .Take(200)
            .Select(x => new
            {
                key = x.Key,
                torrentHash = GetTorrentHash(x.Key),
                status = x.Value.Status,
                sourcePath = x.Value.SourcePath,
                destinationPath = x.Value.DestinationPath,
                tmdbId = x.Value.TmdbId,
                message = x.Value.Message,
                kind = x.Value.Kind?.ToString(),
                season = x.Value.Season,
                episode = x.Value.Episode,
                reviewCandidates = x.Value.ReviewCandidates,
                updatedAt = x.Value.UpdatedAt
            })
            .ToList();

        return Ok(new
        {
            pluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "unknown",
            workerEnabled = config.Enabled,
            dryRunMode = config.DryRunMode,
            sequentialEnabled = config.ManageSequentialEpisodes,
            baselineCreated = state.ContainsKey(BaselineMarkerKey),
            qbittorrentConnected,
            qbittorrentMessage,
            tmdbConnected = tmdbHealth.Connected,
            tmdbMessage = tmdbHealth.Message,
            torrentCount = rows.Count,
            activeCount = rows.Count(x => x.MediaFlowStatus is "Downloading" or "Ready"),
            baselineCount = rows.Count(x => x.MediaFlowStatus == "Baseline"),
            needsReviewCount = history.Count(x => x.status == "NeedsReview"),
            failedCount = history.Count(x => x.status == "Failed"),
            importedCount = history.Count(x => x.status == "Imported"),
            ignoredCount = history.Count(x => x.status == "Ignored"),
            torrents = rows,
            history
        });
    }

    [HttpPost("torrents/{hash}/reprocess")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ReprocessTorrent([FromRoute] string hash, CancellationToken cancellationToken)
    {
        ValidateHash(hash);
        var staleImported = await RemoveStaleImportedStateAsync(hash, cancellationToken).ConfigureAwait(false);
        var removed = await _state.ReprocessTorrentAsync(hash, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            success = true,
            removed,
            staleImported,
            message = "Torrent is eligible for MediaFlow processing again. Healthy Imported entries were preserved; missing/conflicting Imported state was released for a fresh search/import."
        });
    }

    [HttpPost("torrents/{hash}/reconcile")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ReconcileTorrent([FromRoute] string hash, CancellationToken cancellationToken)
    {
        ValidateHash(hash);
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("MediaFlow plugin configuration is not available.");

        var torrents = await _qbittorrent.GetTorrentsAsync(cancellationToken).ConfigureAwait(false);
        var torrent = torrents.FirstOrDefault(x => string.Equals(x.Hash, hash, StringComparison.OrdinalIgnoreCase));
        if (torrent is null)
        {
            return NotFound(new { message = "Torrent is no longer present in qBittorrent or its category is not managed by MediaFlow." });
        }

        var files = await _qbittorrent.GetFilesAsync(hash, cancellationToken).ConfigureAwait(false);
        var state = await _state.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var prefix = hash + ":";
        var entries = state
            .Where(x => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);

        var eligibleFiles = files.Where(x => IsEligibleVideoFile(x, config)).ToList();
        var completedFiles = eligibleFiles.Where(x => x.Progress >= 0.999999).ToList();
        var untrackedCompleted = completedFiles.Count(x => !entries.ContainsKey(hash + ":" + x.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        var healthyImported = 0;
        var missingInJellyfin = 0;
        var differentDestination = 0;
        var libraryOnly = 0;

        foreach (var entry in entries.Values.Where(x => string.Equals(x.Status, "Imported", StringComparison.OrdinalIgnoreCase)))
        {
            var destinationExists = !string.IsNullOrWhiteSpace(entry.DestinationPath) && System.IO.File.Exists(entry.DestinationPath);
            var sourceExists = !string.IsNullOrWhiteSpace(entry.SourcePath) && System.IO.File.Exists(entry.SourcePath);

            if (!destinationExists)
            {
                missingInJellyfin++;
                continue;
            }

            if (!sourceExists)
            {
                libraryOnly++;
                continue;
            }

            try
            {
                if (_hardLinks.IsSameFile(entry.SourcePath, entry.DestinationPath!))
                {
                    healthyImported++;
                }
                else
                {
                    differentDestination++;
                }
            }
            catch
            {
                differentDestination++;
            }
        }

        return Ok(new
        {
            success = true,
            torrent = torrent.Name,
            qBittorrentFiles = eligibleFiles.Count,
            completedFiles = completedFiles.Count,
            trackedFiles = entries.Count,
            healthyImported,
            missingInJellyfin,
            differentDestination,
            libraryOnly,
            untrackedCompleted,
            failed = entries.Values.Count(x => string.Equals(x.Status, "Failed", StringComparison.OrdinalIgnoreCase)),
            needsReview = entries.Values.Count(x => string.Equals(x.Status, "NeedsReview", StringComparison.OrdinalIgnoreCase)),
            ignored = entries.Values.Count(x => string.Equals(x.Status, "Ignored", StringComparison.OrdinalIgnoreCase)),
            message = "Comparison complete. Use Reprocess to release stale state and run TMDb matching/import again."
        });
    }

    [HttpPost("torrents/{hash}/delete-jellyfin")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> DeleteFromJellyfin([FromRoute] string hash, CancellationToken cancellationToken)
    {
        ValidateHash(hash);
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("MediaFlow plugin configuration is not available.");
        var state = await _state.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var prefix = hash + ":";
        var imported = state
            .Where(x => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Value.Status, "Imported", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(x.Value.DestinationPath))
            .ToList();

        var deleted = 0;
        var missing = 0;
        var conflicts = 0;
        var unsafePaths = 0;
        var removedState = 0;

        foreach (var item in imported)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var destination = item.Value.DestinationPath!;
            if (!IsManagedLibraryPath(destination, config))
            {
                unsafePaths++;
                continue;
            }

            if (!System.IO.File.Exists(destination))
            {
                missing++;
                if (await _state.RemoveAsync(item.Key, cancellationToken).ConfigureAwait(false))
                {
                    removedState++;
                }
                continue;
            }

            if (System.IO.File.Exists(item.Value.SourcePath))
            {
                try
                {
                    if (!_hardLinks.IsSameFile(item.Value.SourcePath, destination))
                    {
                        conflicts++;
                        continue;
                    }
                }
                catch
                {
                    conflicts++;
                    continue;
                }
            }

            System.IO.File.Delete(destination);
            deleted++;
            if (await _state.RemoveAsync(item.Key, cancellationToken).ConfigureAwait(false))
            {
                removedState++;
            }
        }

        // Suppress the torrent so the worker does not immediately recreate the Jellyfin hardlinks.
        // Reprocess removes this per-torrent baseline marker when the administrator wants the media back.
        await _state.SetAsync(new ImportStateEntry
        {
            Key = BaselineTorrentPrefix + hash,
            Status = "Baseline",
            Message = "Suppressed after administrator removed MediaFlow destinations from Jellyfin. Use Reprocess to import again."
        }, cancellationToken).ConfigureAwait(false);

        if (deleted > 0 || missing > 0)
        {
            _libraryManager.QueueLibraryScan();
        }

        return Ok(new
        {
            success = true,
            deletedFiles = deleted,
            missingFiles = missing,
            conflicts,
            unsafePaths,
            removedState,
            suppressed = true,
            message = "MediaFlow destinations were removed from the Jellyfin library where safe. The qBittorrent torrent was not changed. Use Reprocess to search/match/import it again."
        });
    }

    [HttpPost("torrents/{hash}/delete-qbittorrent")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> DeleteFromQbittorrent(
        [FromRoute] string hash,
        [FromQuery] bool deleteFiles,
        CancellationToken cancellationToken)
    {
        ValidateHash(hash);
        await _qbittorrent.DeleteTorrentAsync(hash, deleteFiles, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            success = true,
            deleteFiles,
            message = deleteFiles
                ? "Torrent and qBittorrent source data were removed. Existing Jellyfin hardlinks were not deleted."
                : "Torrent was removed from qBittorrent. Source data and Jellyfin files were left untouched."
        });
    }

    [HttpPost("torrents/{hash}/reset")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ResetTorrent([FromRoute] string hash, CancellationToken cancellationToken)
    {
        ValidateHash(hash);
        var removed = await _state.ResetTorrentAsync(hash, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            success = true,
            removed,
            message = "All MediaFlow state for this torrent was removed. Global baseline state was preserved."
        });
    }

    [HttpPost("state/retry")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> RetryState([FromQuery] string key, CancellationToken cancellationToken)
    {
        ValidateStateKey(key);
        var removed = await _state.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            success = removed,
            message = removed ? "State removed; the worker may process the file again." : "State entry was not found."
        });
    }

    [HttpGet("review/search")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> SearchReview(
        [FromQuery] string key,
        [FromQuery] string query,
        CancellationToken cancellationToken)
    {
        ValidateStateKey(key);
        if (string.IsNullOrWhiteSpace(query))
        {
            return BadRequest(new { message = "TMDb search query is required." });
        }

        var entry = await _state.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return NotFound(new { message = "Review state entry was not found." });
        }

        if (!string.Equals(entry.Status, "NeedsReview", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "The selected state entry is no longer waiting for review." });
        }

        if (entry.Kind is not MediaKind.Movie and not MediaKind.Episode)
        {
            return BadRequest(new { message = "This review entry predates manual matching metadata. Use Retry once to rebuild it." });
        }

        var found = await _tmdb.SearchAsync(entry.Kind.Value, query.Trim(), null, cancellationToken).ConfigureAwait(false);
        var candidates = new List<ReviewCandidateSnapshot>();

        foreach (var candidate in found.Take(8))
        {
            if (entry.Kind == MediaKind.Episode)
            {
                if (!entry.Season.HasValue || !entry.Episode.HasValue)
                {
                    return BadRequest(new { message = "Episode review state is missing season/episode numbers. Use Retry once to rebuild it." });
                }

                var episodeInfo = await _tmdb.GetEpisodeInfoAsync(
                    candidate.Id,
                    entry.Season.Value,
                    entry.Episode.Value,
                    cancellationToken).ConfigureAwait(false);

                candidate.EpisodeExists = episodeInfo.Exists;
                candidate.EpisodeTitle = episodeInfo.Title;
                candidate.EpisodeAirYear = episodeInfo.AirYear;
                if (!episodeInfo.Exists)
                {
                    continue;
                }
            }

            candidate.Reasons.Add("manual search");
            candidates.Add(ReviewCandidateSnapshot.FromCandidate(candidate));
        }

        return Ok(new { candidates });
    }

    [HttpPost("review/approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ApproveReview(
        [FromBody] MediaFlowReviewApprovalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateStateKey(request.Key);
        if (request.TmdbId <= 0)
        {
            return BadRequest(new { message = "A valid TMDb id is required." });
        }

        var entry = await _state.GetAsync(request.Key, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return NotFound(new { message = "Review state entry was not found." });
        }

        if (!string.Equals(entry.Status, "NeedsReview", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "The selected state entry is no longer waiting for review." });
        }

        if (entry.Kind is not MediaKind.Movie and not MediaKind.Episode)
        {
            return BadRequest(new { message = "This review entry predates manual matching metadata. Use Retry once to rebuild it." });
        }

        if (!System.IO.File.Exists(entry.SourcePath))
        {
            return BadRequest(new { message = "Source file no longer exists: " + entry.SourcePath });
        }

        var candidate = await _tmdb.GetCandidateByIdAsync(
            entry.Kind.Value,
            request.TmdbId,
            entry.Season,
            entry.Episode,
            cancellationToken).ConfigureAwait(false);

        var parsed = new ParsedMedia
        {
            Kind = entry.Kind.Value,
            SourcePath = entry.SourcePath,
            Season = entry.Season,
            Episode = entry.Episode
        };

        var destination = ImportPlanner.BuildDestination(parsed, candidate);
        _hardLinks.Create(entry.SourcePath, destination);

        await _state.SetAsync(new ImportStateEntry
        {
            Key = entry.Key,
            Status = "Imported",
            SourcePath = entry.SourcePath,
            DestinationPath = destination,
            TmdbId = candidate.Id,
            Kind = entry.Kind,
            Season = entry.Season,
            Episode = entry.Episode,
            Message = $"Manual TMDb match: {candidate.Title} ({candidate.Year}) #{candidate.Id}."
        }, cancellationToken).ConfigureAwait(false);

        _libraryManager.QueueLibraryScan();

        return Ok(new
        {
            success = true,
            tmdbId = candidate.Id,
            title = candidate.Title,
            year = candidate.Year,
            destination
        });
    }

    [HttpPost("review/ignore")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> IgnoreReview([FromQuery] string key, CancellationToken cancellationToken)
    {
        ValidateStateKey(key);
        var entry = await _state.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (entry is null)
        {
            return NotFound(new { message = "Review state entry was not found." });
        }

        if (!string.Equals(entry.Status, "NeedsReview", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "The selected state entry is no longer waiting for review." });
        }

        await _state.SetAsync(new ImportStateEntry
        {
            Key = entry.Key,
            Status = "Ignored",
            SourcePath = entry.SourcePath,
            DestinationPath = entry.DestinationPath,
            TmdbId = entry.TmdbId,
            Kind = entry.Kind,
            Season = entry.Season,
            Episode = entry.Episode,
            ReviewCandidates = entry.ReviewCandidates,
            Message = "Ignored by administrator. Use Retry in History to make this file eligible again."
        }, cancellationToken).ConfigureAwait(false);

        return Ok(new { success = true, message = "Review item ignored." });
    }

    private async Task<int> RemoveStaleImportedStateAsync(string hash, CancellationToken cancellationToken)
    {
        var state = await _state.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var prefix = hash + ":";
        var staleKeys = new List<string>();

        foreach (var item in state.Where(x => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Value.Status, "Imported", StringComparison.OrdinalIgnoreCase)))
        {
            var entry = item.Value;
            if (string.IsNullOrWhiteSpace(entry.DestinationPath) || !System.IO.File.Exists(entry.DestinationPath))
            {
                staleKeys.Add(item.Key);
                continue;
            }

            if (!string.IsNullOrWhiteSpace(entry.SourcePath) && System.IO.File.Exists(entry.SourcePath))
            {
                try
                {
                    if (!_hardLinks.IsSameFile(entry.SourcePath, entry.DestinationPath))
                    {
                        staleKeys.Add(item.Key);
                    }
                }
                catch
                {
                    staleKeys.Add(item.Key);
                }
            }
        }

        var removed = 0;
        foreach (var key in staleKeys)
        {
            if (await _state.RemoveAsync(key, cancellationToken).ConfigureAwait(false))
            {
                removed++;
            }
        }

        return removed;
    }

    private static bool IsEligibleVideoFile(QbTorrentFile file, PluginConfiguration config)
    {
        if (file.Size < config.MinimumVideoSizeMb * 1024L * 1024L)
        {
            return false;
        }

        var extension = Path.GetExtension(file.Name);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        var allowed = config.VideoExtensions
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.StartsWith('.') ? x : "." + x)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!allowed.Contains(extension))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(file.Name);
        return !fileName.Contains("sample", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsManagedLibraryPath(string path, PluginConfiguration config)
        => IsPathWithinRoot(path, config.MoviesRoot) || IsPathWithinRoot(path, config.ShowsRoot);

    private static bool IsPathWithinRoot(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root);
        var relative = Path.GetRelativePath(fullRoot, fullPath);
        return !Path.IsPathRooted(relative)
            && !string.Equals(relative, "..", StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static MediaFlowTorrentRow BuildTorrentRow(QbTorrent torrent, IReadOnlyDictionary<string, ImportStateEntry> state)
    {
        var baseline = state.ContainsKey(BaselineTorrentPrefix + torrent.Hash);
        var prefix = torrent.Hash + ":";
        var entries = state
            .Where(x => x.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(x => x.Value)
            .ToList();

        var imported = entries.Count(x => string.Equals(x.Status, "Imported", StringComparison.OrdinalIgnoreCase));
        var review = entries.Count(x => string.Equals(x.Status, "NeedsReview", StringComparison.OrdinalIgnoreCase));
        var failed = entries.Count(x => string.Equals(x.Status, "Failed", StringComparison.OrdinalIgnoreCase));

        string mediaFlowStatus;
        if (baseline)
        {
            mediaFlowStatus = "Baseline";
        }
        else if (review > 0)
        {
            mediaFlowStatus = "NeedsReview";
        }
        else if (failed > 0)
        {
            mediaFlowStatus = "Failed";
        }
        else if (entries.Count > 0 && imported == entries.Count)
        {
            mediaFlowStatus = "Imported";
        }
        else if (torrent.Progress >= 0.999999)
        {
            mediaFlowStatus = "Ready";
        }
        else
        {
            mediaFlowStatus = "Downloading";
        }

        return new MediaFlowTorrentRow
        {
            Hash = torrent.Hash,
            Name = torrent.Name,
            Category = torrent.Category,
            Progress = torrent.Progress,
            QbState = torrent.State,
            Size = torrent.Size,
            Downloaded = torrent.Downloaded,
            AddedOn = torrent.AddedOn,
            MediaFlowStatus = mediaFlowStatus,
            ImportedFiles = imported,
            ReviewFiles = review,
            FailedFiles = failed,
            TrackedFiles = entries.Count,
            IsBaseline = baseline
        };
    }

    private static string GetTorrentHash(string key)
    {
        var separator = key.IndexOf(':');
        return separator > 0 ? key[..separator] : string.Empty;
    }

    private static void ValidateHash(string hash)
    {
        if (hash.Length != 40 || hash.Any(x => !Uri.IsHexDigit(x)))
        {
            throw new ArgumentException("Invalid qBittorrent hash.", nameof(hash));
        }
    }

    private static void ValidateStateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("__mediaflow_", StringComparison.Ordinal))
        {
            throw new ArgumentException("A valid file-state key is required.", nameof(key));
        }
    }
}
