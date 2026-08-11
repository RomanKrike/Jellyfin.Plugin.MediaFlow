using System.Net.Mime;
using Jellyfin.Plugin.MediaFlow.Models;
using Jellyfin.Plugin.MediaFlow.Services;
using MediaBrowser.Common.Api;
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
    private readonly ImportStateStore _state;

    public MediaFlowAdminController(QbittorrentClient qbittorrent, ImportStateStore state)
    {
        _qbittorrent = qbittorrent;
        _state = state;
    }

    [HttpGet("overview")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> GetOverview(CancellationToken cancellationToken)
    {
        var config = Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("MediaFlow plugin configuration is not available.");

        var torrents = await _qbittorrent.GetTorrentsAsync(cancellationToken).ConfigureAwait(false);
        var state = await _state.GetAllAsync(cancellationToken).ConfigureAwait(false);

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
            torrentCount = rows.Count,
            activeCount = rows.Count(x => x.MediaFlowStatus is "Downloading" or "Ready"),
            baselineCount = rows.Count(x => x.MediaFlowStatus == "Baseline"),
            needsReviewCount = history.Count(x => x.status == "NeedsReview"),
            failedCount = history.Count(x => x.status == "Failed"),
            importedCount = history.Count(x => x.status == "Imported"),
            torrents = rows,
            history
        });
    }

    [HttpPost("torrents/{hash}/reprocess")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> ReprocessTorrent([FromRoute] string hash, CancellationToken cancellationToken)
    {
        ValidateHash(hash);
        var removed = await _state.ReprocessTorrentAsync(hash, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            success = true,
            removed,
            message = "Torrent is eligible for MediaFlow processing again. Imported file state was preserved."
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
        if (string.IsNullOrWhiteSpace(key) || key.StartsWith("__mediaflow_", StringComparison.Ordinal))
        {
            return BadRequest(new { message = "A valid file-state key is required." });
        }

        var removed = await _state.RemoveAsync(key, cancellationToken).ConfigureAwait(false);
        return Ok(new
        {
            success = removed,
            message = removed ? "State removed; the worker may process the file again." : "State entry was not found."
        });
    }

    private static object BuildTorrentRow(QbTorrent torrent, IReadOnlyDictionary<string, ImportStateEntry> state)
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

        return new
        {
            hash = torrent.Hash,
            name = torrent.Name,
            category = torrent.Category,
            progress = torrent.Progress,
            qbState = torrent.State,
            size = torrent.Size,
            downloaded = torrent.Downloaded,
            addedOn = torrent.AddedOn,
            mediaFlowStatus,
            importedFiles = imported,
            reviewFiles = review,
            failedFiles = failed,
            trackedFiles = entries.Count,
            isBaseline = baseline
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
}
