using Jellyfin.Plugin.MediaFlow.Models;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class EpisodeMetadataService(TmdbClient tmdb, EpisodeMappingStore mappings)
{
    public async Task<TmdbCandidate> GetCandidateAsync(ParsedMedia parsed, int id, CancellationToken token)
    {
        var candidate = await tmdb.GetMediaSummaryByIdAsync(parsed.Kind, id, token).ConfigureAwait(false);
        await EnrichAsync(candidate, parsed, token).ConfigureAwait(false);
        if (parsed.Kind == MediaKind.Episode && candidate.EpisodeExists != true)
            throw new InvalidOperationException("Mapped episode does not exist in TMDb.");
        return candidate;
    }

    public async Task EnrichAsync(TmdbCandidate candidate, ParsedMedia parsed, CancellationToken token)
    {
        if (parsed.Kind != MediaKind.Episode) return;
        var mapping = await mappings.GetAsync(parsed.TorrentHash, token).ConfigureAwait(false);
        await EnrichWithMappingAsync(candidate, parsed, mapping, token).ConfigureAwait(false);
    }

    public async Task EnrichWithMappingAsync(TmdbCandidate candidate, ParsedMedia parsed,
        TorrentEpisodeMapping? mapping, CancellationToken token)
    {
        var identity = EpisodeMapper.Map(candidate.Id,
            parsed.Season ?? -1, parsed.Episode ?? -1, mapping);
        var metadata = await tmdb.GetEpisodeInfoAsync(candidate.Id,
            identity.Provider.Season, identity.Provider.Episode, token).ConfigureAwait(false);
        candidate.EpisodeIdentity = identity;
        candidate.EpisodeMetadata = metadata;
        candidate.EpisodeExists = metadata.Exists;
        candidate.EpisodeTitle = metadata.Title;
        candidate.EpisodeAirYear = metadata.AirYear;
    }
}
