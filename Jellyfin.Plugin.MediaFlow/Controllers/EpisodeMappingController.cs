using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.MediaFlow.Models;
using Jellyfin.Plugin.MediaFlow.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaFlow.Controllers;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MediaFlow/Admin/torrents/{hash}/episode-mapping")]
public sealed class EpisodeMappingController(
    QbittorrentClient qb, MediaParser parser, PathMapper paths,
    TmdbClient tmdb, EpisodeMetadataService episodes, EpisodeMappingStore mappings,
    ImportStateStore state, HardLinkService hardlinks) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult> Get(string hash, CancellationToken token)
    {
        ValidateHash(hash);
        var torrent = await GetTorrentAsync(hash, token).ConfigureAwait(false);
        var files = await qb.GetFilesAsync(hash, token).ConfigureAwait(false);
        var entries = await state.GetAllAsync(token).ConfigureAwait(false);
        var mapping = await mappings.GetAsync(hash, token).ConfigureAwait(false);
        var id = mapping?.TmdbId ?? entries.Values.Where(x => x.Key.StartsWith(hash + ":", StringComparison.OrdinalIgnoreCase))
            .Select(x => x.TmdbId ?? x.ReviewCandidates.FirstOrDefault()?.Id ?? 0).FirstOrDefault(x => x > 0);
        var seasons = files.Where(IsEligible).Select(x => parser.FindEpisodeNumbers(x.Name, torrent.Name)?.Season)
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().OrderBy(x => x).ToList();
        return Ok(new { hash, name = torrent.Name, tmdbId = id, mapping,
            seasons, rules = mapping?.Seasons ?? seasons.Select(x => new SeasonMapping(x, x, x, 0)).ToList() });
    }

    [HttpPost("preview")]
    public async Task<ActionResult> Preview(string hash, EpisodeMappingRequest request, CancellationToken token)
    {
        try
        {
            ValidateHash(hash);
            using var lease = await mappings.AcquireImportAsync(hash, token).ConfigureAwait(false);
            return Ok(await BuildPreviewAsync(hash, request, token).ConfigureAwait(false));
        }
        catch (HttpRequestException ex)
        { return StatusCode(502, new { message = "TMDb request failed: " + ex.Message }); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return StatusCode(504, new { message = "Metadata request timed out. Preview again when TMDb is reachable." }); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPost]
    public async Task<ActionResult> Save(string hash, EpisodeMappingRequest request, CancellationToken token)
    {
        try
        {
            ValidateHash(hash);
            using var lease = await mappings.AcquireImportAsync(hash, token).ConfigureAwait(false);
            // Preview is advisory, never a way to bypass server validation or imported-file protection.
            var preview = await BuildPreviewAsync(hash, request, token).ConfigureAwait(false);
            if (!preview.CanSave || string.IsNullOrEmpty(request.PreviewToken)
                || !string.Equals(preview.PreviewToken, request.PreviewToken, StringComparison.Ordinal))
                return Conflict(new { message = "Preview is missing, invalid or outdated. Check all rows and preview again.", preview });
            await mappings.SetAsync(hash, preview.Mapping with { Revision = Guid.NewGuid().ToString("N") }, token).ConfigureAwait(false);
            await state.ReprocessTorrentAsync(hash, token).ConfigureAwait(false);
            return Ok(new { success = true, message = "Numbering saved. Pending files will be analysed using this rule; imported files are preserved." });
        }
        catch (HttpRequestException ex)
        { return StatusCode(502, new { message = "TMDb request failed: " + ex.Message }); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { return StatusCode(504, new { message = "Metadata request timed out. Preview again when TMDb is reachable." }); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        { return BadRequest(new { message = ex.Message }); }
    }

    private async Task<EpisodeMappingPreview> BuildPreviewAsync(string hash, EpisodeMappingRequest request, CancellationToken token)
    {
        var mapping = new TorrentEpisodeMapping(request.TmdbId, request.Seasons);
        EpisodeMapper.Validate(mapping);
        var torrent = await GetTorrentAsync(hash, token).ConfigureAwait(false);
        var files = (await qb.GetFilesAsync(hash, token).ConfigureAwait(false)).Where(IsEligible).OrderBy(x => x.Index).ToList();
        if (files.Count > 1000) throw new ArgumentException("Split torrents containing more than 1000 eligible video files before mapping.");
        var allState = await state.GetAllAsync(token).ConfigureAwait(false);
        var series = await tmdb.GetMediaSummaryByIdAsync(MediaKind.Episode, mapping.TmdbId, token).ConfigureAwait(false);
        var rows = new List<EpisodeMappingRow>();
        var destinations = new HashSet<string>(StringComparer.Ordinal);
        var providerNumbers = new HashSet<EpisodeNumber>();
        var libraryNumbers = new HashSet<EpisodeNumber>();
        foreach (var file in files)
        {
            token.ThrowIfCancellationRequested();
            allState.TryGetValue(hash + ":" + file.Index, out var entry);
            var imported = entry?.Status == "Imported";
            EpisodeIdentity? identity = null;
            string? destination = null;
            string? title = null;
            string? error = null;
            try
            {
                var source = paths.BuildAndMap(torrent.SavePath, file.Name);
                var parsed = parser.Parse(source, torrent.Name, file.Name);
                parsed.TorrentHash = hash;
                parsed.Kind = MediaKind.Episode;
                identity = EpisodeMapper.Map(mapping.TmdbId, parsed.Season ?? -1, parsed.Episode ?? -1, mapping);
                if (!libraryNumbers.Add(identity.Library))
                    throw new InvalidOperationException("Two files map to the same Jellyfin episode. Resolve overlapping season rules before saving.");
                if (!providerNumbers.Add(identity.Provider))
                    throw new InvalidOperationException("Two files map to the same TMDb episode. Resolve duplicate versions before saving.");
                if (imported) EpisodeMapper.ValidateImported(entry!, identity);
                var candidate = new TmdbCandidate { Id = series.Id, Kind = MediaKind.Episode,
                    Title = series.Title, Year = series.Year, PosterPath = series.PosterPath };
                await episodes.EnrichWithMappingAsync(candidate, parsed, mapping, token).ConfigureAwait(false);
                if (candidate.EpisodeExists != true) throw new InvalidOperationException("Episode does not exist in TMDb.");
                title = candidate.EpisodeTitle;
                destination = imported ? entry!.DestinationPath : ImportPlanner.BuildDestination(parsed, candidate);
                if (destination is null || !destinations.Add(destination))
                    throw new InvalidOperationException("Duplicate or missing library destination.");
                if (!imported && System.IO.File.Exists(destination) && !hardlinks.IsSameFile(source, destination))
                    throw new InvalidOperationException("Destination is already occupied by another file.");
                if (!imported && System.IO.File.Exists(Path.ChangeExtension(destination, ".nfo")))
                    MappedEpisodeWriter.ValidateOwnership(destination, candidate);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
            { error = ex.Message; }
            rows.Add(new EpisodeMappingRow(file.Index, file.Name, identity, title, destination, imported, error));
        }
        var fingerprint = JsonSerializer.Serialize(new { hash, mapping, rows,
            sources = files.Select(x => new { x.Index, x.Name, x.Size }), torrent.SavePath });
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprint)));
        return new EpisodeMappingPreview(hash, series.Title, mapping, rows, digest);
    }

    private async Task<QbTorrent> GetTorrentAsync(string hash, CancellationToken token)
    {
        var config = Plugin.Instance!.Configuration;
        var torrent = (await qb.GetTorrentsAsync(token).ConfigureAwait(false))
            .FirstOrDefault(x => string.Equals(x.Hash, hash, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Torrent was not found.");
        if (!string.Equals(torrent.Category, config.QbittorrentTvCategory.Trim(), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Episode numbering is only available for the configured TV category.");
        return torrent;
    }

    private static bool IsEligible(QbTorrentFile file)
    {
        var config = Plugin.Instance!.Configuration;
        return config.VideoExtensions.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Contains(Path.GetExtension(file.Name), StringComparer.OrdinalIgnoreCase)
            && file.Size >= config.MinimumVideoSizeMb * 1024L * 1024L
            && !Path.GetFileName(file.Name).Contains("sample", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateHash(string hash)
    {
        if (hash.Length is not 40 and not 64 || !hash.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid torrent hash.");
    }
}
