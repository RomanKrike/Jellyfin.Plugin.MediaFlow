using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaFlow.Models;

/// <summary>
/// Admin dashboard projection of a qBittorrent torrent plus MediaFlow state.
/// The JSON names are explicit so the admin UI does not depend on Jellyfin's
/// global ASP.NET JSON naming policy.
/// </summary>
public sealed class MediaFlowTorrentRow
{
    [JsonPropertyName("hash")]
    public string Hash { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("progress")]
    public double Progress { get; init; }

    [JsonPropertyName("qbState")]
    public string QbState { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("downloaded")]
    public long Downloaded { get; init; }

    [JsonPropertyName("addedOn")]
    public long AddedOn { get; init; }

    [JsonPropertyName("mediaFlowStatus")]
    public string MediaFlowStatus { get; init; } = string.Empty;

    [JsonPropertyName("importedFiles")]
    public int ImportedFiles { get; init; }

    [JsonPropertyName("reviewFiles")]
    public int ReviewFiles { get; init; }

    [JsonPropertyName("failedFiles")]
    public int FailedFiles { get; init; }

    [JsonPropertyName("trackedFiles")]
    public int TrackedFiles { get; init; }

    [JsonPropertyName("isBaseline")]
    public bool IsBaseline { get; init; }
}

public sealed class MediaFlowReviewApprovalRequest
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = string.Empty;

    [JsonPropertyName("tmdbId")]
    public int TmdbId { get; set; }
}

public sealed class MediaFlowTorrentFileRow
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; init; }

    [JsonPropertyName("progress")]
    public double Progress { get; init; }

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("season")]
    public int? Season { get; init; }

    [JsonPropertyName("episode")]
    public int? Episode { get; init; }

    [JsonPropertyName("stateStatus")]
    public string? StateStatus { get; init; }

    [JsonPropertyName("tmdbId")]
    public int? TmdbId { get; init; }

    [JsonPropertyName("sourcePath")]
    public string? SourcePath { get; init; }

    [JsonPropertyName("destinationPath")]
    public string? DestinationPath { get; init; }

    [JsonPropertyName("sourceExists")]
    public bool SourceExists { get; init; }

    [JsonPropertyName("destinationExists")]
    public bool DestinationExists { get; init; }

    [JsonPropertyName("sameHardlink")]
    public bool? SameHardlink { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed class MediaFlowMediaSummary
{
    [JsonPropertyName("tmdbId")]
    public int TmdbId { get; init; }

    [JsonPropertyName("kind")]
    public string Kind { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("posterPath")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("season")]
    public int? Season { get; init; }
}

public sealed class MediaFlowLogEntry
{
    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; set; }

    [JsonPropertyName("level")]
    public string Level { get; set; } = "Information";

    [JsonPropertyName("source")]
    public string Source { get; set; } = string.Empty;

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("torrentHash")]
    public string? TorrentHash { get; set; }

    [JsonPropertyName("torrentName")]
    public string? TorrentName { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }
}
