using Jellyfin.Plugin.MediaFlow.Models;

namespace Jellyfin.Plugin.MediaFlow.Services;

/// <summary>Pure numbering conversion. Never infers season boundaries from dates or series IDs.</summary>
public static class EpisodeMapper
{
    public static void Validate(TorrentEpisodeMapping mapping)
    {
        if (mapping.TmdbId <= 0 || mapping.Seasons.Count == 0 || mapping.Seasons.Count > 100)
            throw new ArgumentException("Select a TMDb series and at least one season rule.");
        if (mapping.Seasons.Select(x => x.ReleaseSeason).Distinct().Count() != mapping.Seasons.Count)
            throw new ArgumentException("Each release season can have only one rule.");
        foreach (var rule in mapping.Seasons)
            if (rule.ReleaseSeason is < 0 or > 999 || rule.LibrarySeason is < 0 or > 999
                || rule.ProviderSeason is < 0 or > 999 || rule.EpisodeOffset is < -10000 or > 10000)
                throw new ArgumentException("Season numbers must be 0–999; episode offset must be -10000–10000.");
    }

    public static EpisodeIdentity Map(int seriesId, int season, int episode, TorrentEpisodeMapping? mapping)
    {
        if (seriesId <= 0 || season < 0 || episode <= 0)
            throw new ArgumentException("Episode numbering is missing or invalid.");
        var release = new EpisodeNumber(season, episode);
        if (mapping is null)
            return new EpisodeIdentity(release, release, release, seriesId);
        Validate(mapping);
        if (mapping.TmdbId != seriesId)
            throw new ArgumentException("This torrent's numbering rule belongs to a different TMDb series.");
        var rule = mapping.Seasons.SingleOrDefault(x => x.ReleaseSeason == season);
        if (rule is null)
            throw new ArgumentException($"No numbering rule for release season {season}. Add it before importing this season.");
        var providerEpisode = checked(episode + rule.EpisodeOffset);
        if (providerEpisode <= 0)
            throw new ArgumentException("Episode offset produces a non-positive TMDb episode number.");
        return new EpisodeIdentity(release, new EpisodeNumber(rule.LibrarySeason, episode),
            new EpisodeNumber(rule.ProviderSeason, providerEpisode), seriesId, mapping.Revision);
    }

    public static void ValidateImported(ImportStateEntry entry, EpisodeIdentity identity)
    {
        var previous = entry.EpisodeIdentity;
        var library = previous?.Library ?? new EpisodeNumber(entry.Season ?? -1, entry.Episode ?? -1);
        var provider = previous?.Provider ?? library;
        if (entry.TmdbId != identity.SeriesId || library != identity.Library || provider != identity.Provider)
            throw new InvalidOperationException("Already imported: this rule would change its series or numbering. Existing files are not migrated.");
    }
}
