using System.Globalization;
using System.Xml.Linq;
using Jellyfin.Plugin.MediaFlow.Models;

namespace Jellyfin.Plugin.MediaFlow.Services;

/// <summary>Local metadata for non-native numbering; never overwrites an unrelated NFO.</summary>
public sealed class MappedEpisodeWriter : IDisposable
{
    private const string Owner = "MediaFlow episode mapping";
    private readonly HttpClient _images;

    public MappedEpisodeWriter() : this(new HttpClient { Timeout = TimeSpan.FromSeconds(15) }) { }
    internal MappedEpisodeWriter(HttpClient images) => _images = images;
    public void Dispose() => _images.Dispose();

    public static XDocument BuildEpisode(TmdbCandidate candidate, string? thumb)
    {
        var identity = candidate.EpisodeIdentity ?? throw new InvalidOperationException("Episode identity is missing.");
        var info = candidate.EpisodeMetadata ?? throw new InvalidOperationException("Episode metadata is missing.");
        if (!info.Exists) throw new InvalidOperationException("Mapped episode does not exist.");
        var root = new XElement("episodedetails",
            new XElement("title", info.Title ?? string.Empty),
            new XElement("showtitle", candidate.Title),
            new XElement("season", identity.Library.Season),
            new XElement("episode", identity.Library.Episode),
            new XElement("plot", info.Overview ?? string.Empty),
            new XElement("lockdata", "true"));
        if (info.Id is > 0) root.Add(new XElement("uniqueid", new XAttribute("type", "tmdb"), info.Id));
        if (!string.IsNullOrEmpty(info.AirDate)) root.Add(new XElement("aired", info.AirDate));
        if (info.Rating.HasValue) root.Add(new XElement("ratings", new XElement("rating",
            new XAttribute("name", "tmdb"), new XAttribute("max", "10"),
            new XElement("value", info.Rating.Value.ToString(CultureInfo.InvariantCulture)))));
        if (thumb is not null) root.Add(new XElement("thumb", thumb));
        return new XDocument(new XComment(Owner), root);
    }

    public async Task WriteAsync(string destination, TmdbCandidate candidate, CancellationToken token)
    {
        if (candidate.EpisodeIdentity?.IsMapped != true) return;
        ValidateOwnership(destination, candidate);
        var nfo = Path.ChangeExtension(destination, ".nfo");
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string? thumb = null;
        var still = candidate.EpisodeMetadata?.StillPath;
        if (!string.IsNullOrEmpty(still))
        {
            if (!still.StartsWith('/') || still.Contains("..")) throw new InvalidDataException("Invalid TMDb still path.");
            var imagePath = Path.ChangeExtension(destination, null) + "-thumb.jpg";
            if (!File.Exists(imagePath))
            {
                using var response = await _images.GetAsync("https://image.tmdb.org/t/p/w500" + still, token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true)
                    throw new InvalidDataException("TMDb returned non-image content for an episode thumbnail.");
                var bytes = await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
                if (bytes.Length == 0) throw new InvalidDataException("TMDb returned an empty episode image.");
                await WriteAtomicAsync(imagePath, bytes, token).ConfigureAwait(false);
            }
            thumb = imagePath;
        }
        await WriteAtomicAsync(nfo, System.Text.Encoding.UTF8.GetBytes(BuildEpisode(candidate, thumb).ToString()), token).ConfigureAwait(false);
        var seasonNfo = Path.Combine(Path.GetDirectoryName(destination)!, "season.nfo");
        if (!File.Exists(seasonNfo))
        {
            var season = candidate.EpisodeIdentity.Library.Season;
            var xml = new XDocument(new XComment(Owner), new XElement("season",
                new XElement("seasonnumber", season), new XElement("title", "Season " + season),
                new XElement("lockdata", "true")));
            await WriteAtomicAsync(seasonNfo, System.Text.Encoding.UTF8.GetBytes(xml.ToString()), token).ConfigureAwait(false);
        }
    }

    public static void ValidateOwnership(string destination, TmdbCandidate candidate)
    {
        if (candidate.EpisodeIdentity?.IsMapped != true) return;
        var nfo = Path.ChangeExtension(destination, ".nfo");
        if (File.Exists(nfo))
        {
            var old = XDocument.Load(nfo);
            if (!old.Nodes().OfType<XComment>().Any(x => x.Value == Owner))
                throw new IOException("Existing NFO is not owned by MediaFlow; refusing to replace it: " + nfo);
        }
    }

    private static async Task WriteAtomicAsync(string path, byte[] bytes, CancellationToken token)
    {
        var temp = path + ".mediaflow-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, token).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
