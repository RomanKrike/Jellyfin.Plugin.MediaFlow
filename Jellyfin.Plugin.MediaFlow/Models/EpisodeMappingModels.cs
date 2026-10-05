using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.MediaFlow.Models;

// Explicit JSON names keep the admin contract independent of Jellyfin naming policy.
public sealed record EpisodeNumber(
    [property: JsonPropertyName("season")] int Season,
    [property: JsonPropertyName("episode")] int Episode);

public sealed record EpisodeIdentity(
    [property: JsonPropertyName("release")] EpisodeNumber Release,
    [property: JsonPropertyName("library")] EpisodeNumber Library,
    [property: JsonPropertyName("provider")] EpisodeNumber Provider,
    [property: JsonPropertyName("seriesId")] int SeriesId,
    [property: JsonPropertyName("mappingRevision")] string? MappingRevision = null)
{
    [JsonPropertyName("isMapped")]
    public bool IsMapped => Library != Provider;
}

public sealed record SeasonMapping(
    [property: JsonPropertyName("releaseSeason")] int ReleaseSeason,
    [property: JsonPropertyName("librarySeason")] int LibrarySeason,
    [property: JsonPropertyName("providerSeason")] int ProviderSeason,
    [property: JsonPropertyName("episodeOffset")] int EpisodeOffset);

public sealed record TorrentEpisodeMapping(
    [property: JsonPropertyName("tmdbId")] int TmdbId,
    [property: JsonPropertyName("seasons")] IReadOnlyList<SeasonMapping> Seasons,
    [property: JsonPropertyName("revision")] string Revision = "");

public sealed class EpisodeMappingRequest
{
    [JsonPropertyName("tmdbId")]
    public int TmdbId { get; set; }
    [JsonPropertyName("seasons")]
    public List<SeasonMapping> Seasons { get; set; } = [];
    [JsonPropertyName("previewToken")]
    public string? PreviewToken { get; set; }
}

public sealed record EpisodeMappingRow(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("identity")] EpisodeIdentity? Identity,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("destination")] string? Destination,
    [property: JsonPropertyName("imported")] bool Imported,
    [property: JsonPropertyName("error")] string? Error);

public sealed record EpisodeMappingPreview(
    [property: JsonPropertyName("hash")] string Hash,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("mapping")] TorrentEpisodeMapping Mapping,
    [property: JsonPropertyName("rows")] IReadOnlyList<EpisodeMappingRow> Rows,
    [property: JsonPropertyName("previewToken")] string PreviewToken)
{
    [JsonPropertyName("canSave")]
    public bool CanSave => Rows.Count > 0 && Rows.All(x => x.Error is null);
}

public sealed record TmdbEpisodeInfo(bool Exists, string? Title, int? AirYear,
    int? Id = null, string? Overview = null, string? AirDate = null, string? StillPath = null, double? Rating = null);
