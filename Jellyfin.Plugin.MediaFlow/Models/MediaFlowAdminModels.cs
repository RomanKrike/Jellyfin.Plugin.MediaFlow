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
