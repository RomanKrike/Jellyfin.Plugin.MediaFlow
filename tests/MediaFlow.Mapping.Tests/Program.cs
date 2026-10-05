using System.Net;
using Jellyfin.Plugin.MediaFlow.Controllers;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Xml.Linq;
using Jellyfin.Plugin.MediaFlow;
using Jellyfin.Plugin.MediaFlow.Models;
using Jellyfin.Plugin.MediaFlow.Services;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
void Check(bool condition, string message) { checks++; if (!condition) throw new Exception(message); }
void Reject(Action action, string message) { try { action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { checks++; return; } throw new Exception(message); }
var root = Path.Combine(Path.GetTempPath(), "mediaflow-mapping-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
Plugin.Instance = new Plugin { DataFolderPath = root };
Plugin.Instance.Configuration.ShowsRoot = Path.Combine(root, "TV");
Plugin.Instance.Configuration.TmdbApiKey = "test-key";
var hash = new string('a', 40);
var rule = new TorrentEpisodeMapping(220542, [new SeasonMapping(2, 2, 1, 24)], "revision-1");
try
{
    var native = EpisodeMapper.Map(220542, 1, 3, null);
    Check(native.Library == native.Provider && !native.IsMapped, "native numbering changed");
    for (var episode = 1; episode <= 24; episode++)
    {
        var mapped = EpisodeMapper.Map(220542, 2, episode, rule);
        Check(mapped.Release == new EpisodeNumber(2, episode), "release changed");
        Check(mapped.Library == new EpisodeNumber(2, episode), "library changed");
        Check(mapped.Provider == new EpisodeNumber(1, episode + 24), "incorrect offset");
    }
    Check(EpisodeMapper.Map(10, 0, 1, null).Library.Season == 0, "specials not supported");
    Reject(() => EpisodeMapper.Map(11, 2, 1, rule), "accepted wrong series");
    Reject(() => EpisodeMapper.Map(220542, 1, 1, rule), "guessed uncovered season");
    Reject(() => EpisodeMapper.Map(220542, 2, 1, rule with { Seasons = [new SeasonMapping(2, 2, 1, -1)] }), "accepted episode zero");
    Reject(() => EpisodeMapper.Validate(rule with { Seasons = [rule.Seasons[0], rule.Seasons[0]] }), "accepted duplicate rule");
    Reject(() => EpisodeMapper.Map(220542, 2, 0, rule), "accepted invalid release episode");
    var imported = new ImportStateEntry { TmdbId = 220542, Season = 1, Episode = 1 };
    EpisodeMapper.ValidateImported(imported, EpisodeMapper.Map(220542, 1, 1, null));
    Reject(() => EpisodeMapper.ValidateImported(imported, EpisodeMapper.Map(220542, 2, 1, rule)), "changed imported numbering");
    imported.EpisodeIdentity = EpisodeMapper.Map(220542, 2, 1, rule);
    EpisodeMapper.ValidateImported(imported, EpisodeMapper.Map(220542, 2, 1, rule with { Revision = "new" }));
    Check(true, "revision-only change should be allowed");

    var store = new EpisodeMappingStore();
    await store.SetAsync(hash, rule, default);
    var reloaded = new EpisodeMappingStore();
    Check((await reloaded.GetAsync(hash.ToUpperInvariant(), default))?.Revision == "revision-1", "mapping did not persist");
    using (await store.AcquireImportAsync(hash, default))
    {
        using (await store.AcquireImportAsync(new string('b', 40), default))
            Check(true, "different torrents should not block each other");
        var waiting = store.AcquireImportAsync(hash, default);
        Check(!waiting.IsCompleted, "mapping/import gate did not serialize");
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await store.AcquireImportAsync(hash, cancel.Token); throw new Exception("ignored cancellation"); }
        catch (OperationCanceledException) { checks++; }
        // Dispose current lease before awaiting the second one below.
        _ = waiting.ContinueWith(x => x.Result.Dispose(), TaskScheduler.Default);
    }

    var handler = new TmdbHandler();
    var tmdb = new TmdbClient(NullLogger<TmdbClient>.Instance, new HttpClient(handler) { BaseAddress = new Uri("https://tmdb.test/3/") });
    var metadata = new EpisodeMetadataService(tmdb, store);
    var parsed = new MediaParser().Parse(Path.Combine(root, "Kusuriya TV-2 01.mkv"), "Kusuriya TV-2", "Kusuriya TV-2 01.mkv");
    parsed.TorrentHash = hash;
    var resolver = new MediaResolver(tmdb, metadata, store, NullLogger<MediaResolver>.Instance);
    var resolved = await resolver.ResolveAsync(parsed, default);
    Check(resolved.AutoApproved && resolved.Selected?.Id == 220542, "confirmed rule did not resolve");
    var candidate = resolved.Selected!;
    Check(handler.Requests.Any(x => x.Contains("/season/1/episode/25")), "wrong TMDb address");
    Check(!handler.Requests.Any(x => x.Contains("/season/2")), "release numbering leaked to TMDb");
    var destination = ImportPlanner.BuildDestination(parsed, candidate);
    Check(destination.Contains("Season 02") && destination.Contains("S02E01"), "provider numbering leaked into path");
    using var writer = new MappedEpisodeWriter(new HttpClient(new ImageHandler()));
    await writer.WriteAsync(destination, candidate, default);
    await File.WriteAllTextAsync(parsed.SourcePath, "source video fixture");
    var hardlinks = new HardLinkService(); hardlinks.Create(parsed.SourcePath, destination);
    Check(hardlinks.IsSameFile(parsed.SourcePath, destination), "video is not a hardlink");
    var nfoPath = Path.ChangeExtension(destination, ".nfo");
    var nfo = XDocument.Load(nfoPath);
    Check(nfo.Root!.Element("season")!.Value == "2" && nfo.Root.Element("episode")!.Value == "1", "wrong NFO numbering");
    Check(nfo.Root.Element("title")!.Value == "Mapped & episode", "XML title escaping failed");
    Check(nfo.Root.Element("plot")!.Value == "Plot <with> symbols", "wrong metadata");
    Check(nfo.Root.Element("uniqueid")!.Value == "123456", "wrong episode ID");
    Check(nfo.Root.Element("lockdata")!.Value == "true", "mapped metadata is not protected");
    Check(File.ReadAllBytes(nfo.Root.Element("thumb")!.Value).Length == 4, "mapped thumbnail was not saved locally");
    await writer.WriteAsync(destination, candidate, default);
    Check(File.ReadAllText(parsed.SourcePath) == "source video fixture", "metadata writer changed video");
    Check(XDocument.Load(Path.Combine(Path.GetDirectoryName(destination)!, "season.nfo")).Root!.Element("seasonnumber")!.Value == "2", "wrong season NFO");
    await File.WriteAllTextAsync(nfoPath, "<episodedetails><title>User metadata</title></episodedetails>");
    try { await writer.WriteAsync(destination, candidate, default); throw new Exception("overwrote foreign NFO"); }
    catch (IOException) { Check(File.ReadAllText(nfoPath).Contains("User metadata"), "foreign NFO changed"); }
    handler.NotFound = true;
    try { await metadata.GetCandidateAsync(parsed, 220542, default); throw new Exception("approved missing episode"); }
    catch (InvalidOperationException) { checks++; }
    handler.NotFound = false; handler.Fail = true;
    try { await metadata.GetCandidateAsync(parsed, 220542, default); throw new Exception("approved failed provider"); }
    catch (HttpRequestException) { checks++; }
    handler.Fail = false;

    // Exercise the production preview/save endpoints with controlled external-service results.
    var qb = new QbittorrentClient();
    qb.Torrents.Add(new QbTorrent { Hash = hash, Name = "Kusuriya TV-2", Category = "tv", SavePath = root });
    qb.Files.Add(new QbTorrentFile { Index = 0, Name = "Kusuriya TV-2 02.mkv", Size = 100_000_000 });
    var states = new ImportStateStore(NullLogger<ImportStateStore>.Instance);
    await states.SetAsync(new ImportStateEntry { Key = hash + ":0", Status = "NeedsReview", Season = 2, Episode = 2 }, default);
    var controller = new EpisodeMappingController(qb, new MediaParser(), new PathMapper(), tmdb, metadata, store, states, hardlinks);
    var request = new EpisodeMappingRequest { TmdbId = 220542, Seasons = rule.Seasons.ToList() };
    Check(await controller.Save(hash, request, default) is ConflictObjectResult, "saved without preview");
    var result = await controller.Preview(hash, request, default) as OkObjectResult;
    var preview = (EpisodeMappingPreview)result!.Value!;
    Check(preview.CanSave && preview.Rows[0].Identity!.Provider.Episode == 26, "endpoint preview incorrect");
    request.PreviewToken = preview.PreviewToken;
    qb.Files[0].Size++;
    Check(await controller.Save(hash, request, default) is ConflictObjectResult, "accepted stale source snapshot");
    qb.Files[0].Size--;
    Check(await controller.Save(hash, request, default) is OkObjectResult, "valid checked plan could not be saved");
    Check(await states.GetAsync(hash + ":0", default) is null, "pending review not reset");
    var savedRule = await new EpisodeMappingStore().GetAsync(hash, default);
    Check(savedRule?.Seasons[0].EpisodeOffset == 24 && !string.IsNullOrEmpty(savedRule.Revision), "saved rule incomplete");
    await states.SetAsync(new ImportStateEntry { Key = hash + ":0", Status = "Imported", TmdbId = 220542,
        Season = 2, Episode = 2, EpisodeIdentity = preview.Rows[0].Identity, DestinationPath = preview.Rows[0].Destination }, default);
    request.Seasons = [new SeasonMapping(2, 2, 1, 12)];
    result = await controller.Preview(hash, request, default) as OkObjectResult;
    preview = (EpisodeMappingPreview)result!.Value!;
    Check(!preview.CanSave && preview.Rows[0].Error!.Contains("Already imported"), "allowed imported metadata remap");
    request.PreviewToken = preview.PreviewToken;
    Check(await controller.Save(hash, request, default) is ConflictObjectResult, "saved conflicting imported rule");
    Check((await states.GetAsync(hash + ":0", default))?.Status == "Imported", "imported state lost");
    await states.RemoveAsync(hash + ":0", default);
    request.Seasons = rule.Seasons.ToList();
    qb.Files.Add(new QbTorrentFile { Index = 1, Name = "Other release TV-2 02.mkv", Size = 100_000_000 });
    result = await controller.Preview(hash, request, default) as OkObjectResult;
    Check(!((EpisodeMappingPreview)result!.Value!).CanSave, "duplicate episodes allowed");
    qb.Files.RemoveAt(1);
    qb.Torrents[0].Category = "movie";
    Check(await controller.Preview(hash, request, default) is BadRequestObjectResult, "mapping applied to Movie");
    qb.Torrents[0].Category = "tv";
    handler.Fail = true;
    Check((await controller.Preview(hash, request, default) as ObjectResult)?.StatusCode == 502, "network failure mislabeled as missing episode");
    handler.Fail = false;
    await File.WriteAllTextAsync(Path.Combine(root, "episode-mappings.json"), "invalid json");
    try { await new EpisodeMappingStore().GetAsync(hash, default); throw new Exception("silently erased corrupt mapping"); }
    catch (JsonException) { checks++; }
    Console.WriteLine($"Mapping integration: {checks} checks passed.");
}
finally { Directory.Delete(root, true); }

sealed class TmdbHandler : HttpMessageHandler
{
    public List<string> Requests { get; } = [];
    public bool NotFound { get; set; }
    public bool Fail { get; set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath; Requests.Add(path);
        var episode = path.Contains("/episode/");
        var status = episode && NotFound ? HttpStatusCode.NotFound : episode && Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK;
        var content = episode
            ? "{\"id\":123456,\"name\":\"Mapped & episode\",\"overview\":\"Plot <with> symbols\",\"air_date\":\"2025-01-10\",\"vote_average\":8.3,\"still_path\":\"/still.jpg\"}"
            : "{\"id\":220542,\"name\":\"Монолог фармацевта\",\"first_air_date\":\"2023-10-22\"}";
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content) });
    }
}

sealed class ImageHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0xff, 0xd8, 0xff, 0xd9]) };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        return Task.FromResult(response);
    }
}
