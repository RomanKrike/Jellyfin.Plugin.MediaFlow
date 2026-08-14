using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaFlow.Models;

public sealed class QbittorrentServerInfo
{
    public string ApplicationVersion { get; set; } = string.Empty;

    public string WebApiVersion { get; set; } = string.Empty;
}

public sealed class QbTorrent
{
    [JsonPropertyName("hash")]
    public string Hash { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("save_path")]
    public string SavePath { get; set; } = string.Empty;

    [JsonPropertyName("content_path")]
    public string ContentPath { get; set; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("state")]
    public string State { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("total_size")]
    public long TotalSize { get; set; }

    [JsonPropertyName("downloaded")]
    public long Downloaded { get; set; }

    [JsonPropertyName("amount_left")]
    public long AmountLeft { get; set; }

    [JsonPropertyName("dlspeed")]
    public long DownloadSpeed { get; set; }

    [JsonPropertyName("eta")]
    public long Eta { get; set; }

    [JsonPropertyName("added_on")]
    public long AddedOn { get; set; }

    [JsonPropertyName("seq_dl")]
    public bool SequentialDownload { get; set; }

    [JsonPropertyName("isPrivate")]
    public bool IsPrivate { get; set; }
}

public sealed class QbTorrentFile
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("progress")]
    public double Progress { get; set; }

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("is_seed")]
    public bool IsSeed { get; set; }

    [JsonPropertyName("availability")]
    public double Availability { get; set; }

    [JsonPropertyName("piece_range")]
    public int[] PieceRange { get; set; } = [];
}
