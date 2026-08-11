namespace Jellyfin.Plugin.MediaFlow.Models;

/// <summary>
/// Admin dashboard projection of a qBittorrent torrent plus MediaFlow state.
/// </summary>
public sealed class MediaFlowTorrentRow
{
    public string Hash { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public double Progress { get; init; }

    public string QbState { get; init; } = string.Empty;

    public long Size { get; init; }

    public long Downloaded { get; init; }

    public long AddedOn { get; init; }

    public string MediaFlowStatus { get; init; } = string.Empty;

    public int ImportedFiles { get; init; }

    public int ReviewFiles { get; init; }

    public int FailedFiles { get; init; }

    public int TrackedFiles { get; init; }

    public bool IsBaseline { get; init; }
}
