using Jellyfin.Plugin.MediaFlow.Models;
using Jellyfin.Plugin.MediaFlow.Services;

var parser = new MediaParser();
var checks = 0;
var episodes = new (string File, string Torrent, string Title, int Season, int Episode)[]
{
    ("Kusuriya no Hitorigoto TV-1 03.mkv", "", "Kusuriya no Hitorigoto", 1, 3),
    ("Kusuriya no Hitorigoto TV-1 05.mkv", "", "Kusuriya no Hitorigoto", 1, 5),
    ("Kusuriya no Hitorigoto TV 1 05.mkv", "", "Kusuriya no Hitorigoto", 1, 5),
    ("Show Name TV-2 12.mkv", "", "Show Name", 2, 12),
    ("[SubsPlease] Kusuriya no Hitorigoto - 05 [1080p] [9ABC1234].mkv", "", "Kusuriya no Hitorigoto", 1, 5),
    ("[EMBER] Kusuriya no Hitorigoto - 05.mkv", "", "Kusuriya no Hitorigoto", 1, 5),
    ("Show Name - 05v2 [720p].mkv", "", "Show Name", 1, 5),
    ("Show Name - 12.mkv", "Show Name TV-2", "Show Name", 2, 12),
    ("Show Name 05.mkv", "Show Name TV-1", "Show Name", 1, 5),
    ("Show Name/TV-2/05.mkv", "", "Show Name", 2, 5),
    ("Show Name/Season 2/Show Name 05.mkv", "", "Show Name", 2, 5),
    ("The.Last.of.Us.S01E05.Pilot.1080p.mkv", "", "The Last of Us", 1, 5),
    ("Kusuriya no Hitorigoto S01E05.mkv", "", "Kusuriya no Hitorigoto", 1, 5),
    ("Kusuriya no Hitorigoto 01x05.mkv", "", "Kusuriya no Hitorigoto", 1, 5),
    ("Show Name Season 1 Episode 5.mkv", "", "Show Name", 1, 5),
    ("Show Name сезон 1 серия 5.mkv", "", "Show Name", 1, 5),
    ("North & South 1 серия.mkv", "", "North & South", 1, 1),
    ("1923.S01E05.mkv", "", "1923", 1, 5),
    ("Show.Name/Season 2/05 Episode Title.mkv", "", "Show Name", 2, 5)
};
foreach (var test in episodes)
{
    var media = parser.Parse("/downloads/" + test.File, test.Torrent, test.File);
    Require(media.Kind == MediaKind.Episode && media.Season == test.Season && media.Episode == test.Episode, $"episode: {test.File}");
    Require(media.Titles.Any(x => x.Value == test.Title), $"title: {test.File}: {string.Join(" | ", media.Titles.Select(x => x.Value))}");
    Require(parser.FindEpisodeNumbers(test.File, test.Torrent) == (test.Season, test.Episode), $"sequential/recovery numbers: {test.File}");
}
foreach (var name in new[] { "Show Name 05", "Show Name - 2024", "Show Name - 1080p", "Show Name TV-1", "Show Name TV-1 03-05", "Show Name TV-1-2", "Show Name - 05-06", "District 9", "Blade Runner 2049", "1917", "1984" })
{
    var file = name + ".mkv";
    Require(parser.Parse("/downloads/" + file, "", file).Kind == MediaKind.Movie, $"ambiguous/movie must not become episode: {file}");
    Require(parser.FindEpisodeNumbers(file, "") is null, $"ambiguous priority: {file}");
}
foreach (var (file, title) in new[] { ("The.Last.of.Us.2023.1080p.mkv", "The Last of Us"), ("1917.2019.1080p.mkv", "1917"), ("1984.mkv", "1984"), ("District.9.2009.mkv", "District 9") })
{
    var media = parser.Parse("/downloads/" + file, "", file);
    Require(media.Titles.Any(x => x.Value == title), $"movie title: {file}");
}
var aliases = parser.Parse("/downloads/episode.mkv", "Монолог фармацевта / Kusuriya no Hitorigoto (TV-1) / The Apothecary Diaries", "Kusuriya no Hitorigoto TV-1 05.mkv");
Require(aliases.Titles.Any(x => x.Value == "Kusuriya no Hitorigoto"), "anime torrent alias");
Require(aliases.Titles.All(x => !x.Value.Contains("TV-1")), "TV metadata must not pollute aliases");
var movieTorrent = parser.Parse("/downloads/movie.mkv", "The.Last.of.Us.2023.1080p.mkv", "movie.mkv");
Require(movieTorrent.Titles.Any(x => x.Value == "The Last of Us"), "known torrent media extension is stripped without losing title words");
Console.WriteLine($"Parser regression checks passed: {checks}");

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
