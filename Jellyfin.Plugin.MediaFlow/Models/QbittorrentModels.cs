using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaFlow.Models;

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
}
