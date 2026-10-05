namespace Jellyfin.Plugin.MediaFlow.Models;

public sealed record EpisodeNumber(int Season, int Episode);

public sealed record EpisodeIdentity(
    EpisodeNumber Release,
    EpisodeNumber Library,
    EpisodeNumber Provider,
    int SeriesId,
    string? MappingRevision = null)
{
    public bool IsMapped => Library != Provider;
}

public sealed record SeasonMapping(int ReleaseSeason, int LibrarySeason, int ProviderSeason, int EpisodeOffset);

public sealed record TorrentEpisodeMapping(int TmdbId, IReadOnlyList<SeasonMapping> Seasons, string Revision = "");

public sealed class EpisodeMappingRequest
{
    public int TmdbId { get; set; }
    public List<SeasonMapping> Seasons { get; set; } = [];
    public string? PreviewToken { get; set; }
}

public sealed record EpisodeMappingRow(
    int Index, string File, EpisodeIdentity? Identity, string? Title,
    string? Destination, bool Imported, string? Error);

public sealed record EpisodeMappingPreview(
    string Hash, string Title, TorrentEpisodeMapping Mapping,
    IReadOnlyList<EpisodeMappingRow> Rows, string PreviewToken)
{
    public bool CanSave => Rows.Count > 0 && Rows.All(x => x.Error is null);
}

public sealed record TmdbEpisodeInfo(bool Exists, string? Title, int? AirYear,
    int? Id = null, string? Overview = null, string? AirDate = null, string? StillPath = null, double? Rating = null);
