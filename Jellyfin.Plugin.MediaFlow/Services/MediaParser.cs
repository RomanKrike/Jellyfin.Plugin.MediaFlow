using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MediaFlow.Models;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed partial class MediaParser
{
    private static readonly string[] NoiseTokens =
    [
        "2160p", "1080p", "1080i", "720p", "576p", "480p", "4k", "uhd",
        "web-dl", "webdl", "webrip", "web-rip", "bluray", "blu-ray", "bdrip", "brrip", "remux", "hdtv", "dvdrip",
        "x264", "x265", "h264", "h265", "hevc", "avc", "av1", "xvid",
        "hdr", "hdr10", "hdr10+", "sdr", "dv", "dolbyvision", "dolby-vision",
        "aac", "ac3", "eac3", "ddp", "ddp5.1", "truehd", "atmos", "dts", "dts-hd", "flac",
        "proper", "repack", "internal", "extended", "uncut", "director's", "directors", "limited",
        "complete", "collection", "pack", "seasonpack", "season-pack", "fullseason", "full-season",
        "rus", "russian", "eng", "english", "multi", "dual", "sub", "subs",
        "дубляж", "лицензия", "многоголосый", "многоголосая", "профессиональный", "профессиональная",
        "netflix", "nf", "amzn", "amazon", "dsnp", "hmax", "atvp", "itunes"
    ];

    public ParsedMedia Parse(string sourcePath, string torrentName, string relativeFileName)
    {
        var result = new ParsedMedia
        {
            SourcePath = sourcePath,
            TorrentName = torrentName,
            RelativeFileName = relativeFileName
        };

        var fileBase = Path.GetFileNameWithoutExtension(relativeFileName);
        var directories = GetDirectorySegments(relativeFileName);

        var episode = FindEpisode(fileBase)
            ?? directories.Select(FindEpisode).FirstOrDefault(x => x.HasValue)
            ?? FindEpisode(torrentName)
            ?? FindEpisodeUsingContext(fileBase, directories, torrentName);

        if (episode is not null)
        {
            result.Kind = MediaKind.Episode;
            result.Season = episode.Value.Season;
            result.Episode = episode.Value.Episode;
        }
        else
        {
            result.Kind = MediaKind.Movie;
        }

        AddYears(result, fileBase, 1.0, "filename");
        AddYears(result, torrentName, 0.8, "torrent");
        for (var i = 0; i < directories.Count; i++)
        {
            AddYears(result, directories[i], Math.Max(0.45, 0.7 - (i * 0.08)), $"folder:{i}");
        }

        // For episodes, everything after the episode marker is frequently the episode title,
        // not the series title (e.g. Show.S01E01.Pilot.1080p). Prefer only the prefix.
        var fileTitleSource = result.Kind == MediaKind.Episode ? ExtractSeriesTitlePrefix(fileBase) : fileBase;
        if (!LooksLikeEpisodeNumberOnly(fileTitleSource))
        {
            AddTitle(result, fileTitleSource, 0.95, "filename");
        }

        var addedTorrentAliases = AddTorrentTitleCandidates(result, torrentName);
        if (!addedTorrentAliases)
        {
            AddTitle(result, torrentName, 1.0, "torrent");
        }

        var folderWeight = 0.88;
        foreach (var directory in directories)
        {
            if (!SeasonOnlyRegex().IsMatch(directory))
            {
                AddTitle(result, directory, folderWeight, "folder");
                folderWeight = Math.Max(0.65, folderWeight - 0.08);
            }
        }

        return result;
    }

    public (int Season, int Episode)? FindEpisodeNumbers(string fileName, string torrentName)
    {
        var fileBase = Path.GetFileNameWithoutExtension(fileName);
        var directories = GetDirectorySegments(fileName);
        return FindEpisode(fileBase)
            ?? directories.Select(FindEpisode).FirstOrDefault(x => x.HasValue)
            ?? FindEpisode(torrentName)
            ?? FindEpisodeUsingContext(fileBase, directories, torrentName);
    }

    private static List<string> GetDirectorySegments(string relativeFileName)
    {
        var directory = Path.GetDirectoryName(relativeFileName);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return [];
        }

        return directory
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries)
            .AsEnumerable()
            .Reverse()
            .Take(4)
            .ToList();
    }

    private static bool AddTorrentTitleCandidates(ParsedMedia target, string torrentName)
    {
        var parts = SlashTitleSeparatorRegex().Split(torrentName)
            .Select(x => x.Trim())
            .Where(x => x.Length > 0)
            .Take(8)
            .ToList();

        if (parts.Count < 2)
        {
            return false;
        }

        var added = 0;
        for (var i = 0; i < parts.Count && added < 4; i++)
        {
            var part = parts[i];
            if (TorrentMetadataSegmentRegex().IsMatch(part))
            {
                break;
            }

            var stopAfter = HasUnclosedParenthesis(part);
            var candidate = part;
            if (stopAfter)
            {
                var paren = candidate.IndexOf(" (", StringComparison.Ordinal);
                if (paren > 1)
                {
                    candidate = candidate[..paren];
                }
            }

            var bracket = candidate.IndexOf(" [", StringComparison.Ordinal);
            if (bracket > 1)
            {
                candidate = candidate[..bracket];
                stopAfter = true;
            }

            candidate = TvRoleSuffixRegex().Replace(candidate, " ");
            var cleaned = CleanTitle(candidate);
            if (cleaned.Length >= 2)
            {
                AddTitle(target, cleaned, Math.Max(0.88, 1.0 - (added * 0.03)), $"torrent:alias:{added}");
                added++;
            }

            if (stopAfter)
            {
                break;
            }
        }

        return added > 0;
    }

    private static bool HasUnclosedParenthesis(string value)
        => value.Count(x => x == '(') > value.Count(x => x == ')');

    private static void AddTitle(ParsedMedia target, string raw, double weight, string source)
    {
        if (TorrentMetadataSegmentRegex().IsMatch(raw))
        {
            return;
        }

        var cleaned = CleanTitle(raw);
        if (cleaned.Length < 2 || TorrentMetadataSegmentRegex().IsMatch(cleaned))
        {
            return;
        }

        if (target.Titles.Any(x => string.Equals(NormalizeForCompare(x.Value), NormalizeForCompare(cleaned), StringComparison.Ordinal)))
        {
            return;
        }

        target.Titles.Add(new TitleSignal(cleaned, weight, source));
    }

    private static void AddYears(ParsedMedia target, string raw, double weight, string source)
    {
        var cleanedTitle = NormalizeForCompare(CleanTitle(raw));
        foreach (Match match in YearRegex().Matches(raw))
        {
            if (!int.TryParse(match.Value, CultureInfo.InvariantCulture, out var year))
            {
                continue;
            }

            // Numeric titles such as 1917, 1923 and 1984 are common. If the parser
            // deliberately preserved the number as the title, do not also treat it
            // as a reliable release year signal.
            if (string.Equals(cleanedTitle, match.Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (target.Years.All(x => x.Value != year || x.Source != source))
            {
                target.Years.Add(new YearSignal(year, weight, source));
            }
        }
    }

    private static string CleanTitle(string raw)
    {
        var original = Path.GetFileNameWithoutExtension(raw);
        var value = BracketNoiseRegex().Replace(original, " ");
        var hasEpisodeMarker = EpisodeRegex().IsMatch(value)
            || AltEpisodeRegex().IsMatch(value)
            || RussianEpisodeRegex().IsMatch(value)
            || TrailingEpisodeOnlyRegex().IsMatch(value);
        value = EpisodeRegex().Replace(value, " ");
        value = AltEpisodeRegex().Replace(value, " ");
        value = RussianEpisodeRegex().Replace(value, " ");
        value = TrailingEpisodeOnlyRegex().Replace(value, " ");
        value = SeasonPackRegex().Replace(value, " ");
        value = StandaloneSeasonRegex().Replace(value, " ");
        value = SeparatorRegex().Replace(value, " ");
        value = RemoveLikelyReleaseYears(value, hasEpisodeMarker);

        foreach (var token in NoiseTokens)
        {
            value = Regex.Replace(value, $@"(?i)(?<![\p{{L}}\p{{N}}]){Regex.Escape(token)}(?![\p{{L}}\p{{N}}])", " ");
        }

        value = CodecGarbageRegex().Replace(value, " ");
        value = WhitespaceRegex().Replace(value, " ").Trim(' ', '-', '.', '_');
        return value;
    }

    private static string RemoveLikelyReleaseYears(string value, bool hasEpisodeMarker)
    {
        var matches = YearRegex().Matches(value).Cast<Match>().ToList();
        if (matches.Count == 0)
        {
            return value;
        }

        var firstNonWhitespace = value.TakeWhile(char.IsWhiteSpace).Count();
        var preserveFirst = matches[0].Index == firstNonWhitespace && (matches.Count > 1 || hasEpisodeMarker || IsMostlySingleYearTitle(value, matches[0]));

        var sb = new StringBuilder(value);
        foreach (var match in matches.OrderByDescending(x => x.Index))
        {
            if (preserveFirst && match.Index == matches[0].Index)
            {
                continue;
            }

            sb.Remove(match.Index, match.Length).Insert(match.Index, new string(' ', match.Length));
        }

        return sb.ToString();
    }

    private static bool IsMostlySingleYearTitle(string value, Match match)
    {
        var without = value.Remove(match.Index, match.Length);
        foreach (var token in NoiseTokens)
        {
            without = Regex.Replace(without, $@"(?i)(?<![\p{{L}}\p{{N}}]){Regex.Escape(token)}(?![\p{{L}}\p{{N}}])", " ");
        }

        without = CodecGarbageRegex().Replace(without, " ");
        without = WhitespaceRegex().Replace(without, " ").Trim();
        return without.Length == 0;
    }


    private static string ExtractSeriesTitlePrefix(string value)
    {
        var matches = new[]
        {
            EpisodeRegex().Match(value),
            AltEpisodeRegex().Match(value),
            RussianEpisodeRegex().Match(value),
            TrailingEpisodeOnlyRegex().Match(value)
        }.Where(x => x.Success).OrderBy(x => x.Index).ToList();

        if (matches.Count == 0)
        {
            return value;
        }

        var prefix = value[..matches[0].Index].Trim(' ', '.', '_', '-');
        return prefix.Length >= 2 ? prefix : value;
    }

    private static bool LooksLikeEpisodeNumberOnly(string value)
    {
        var normalized = NormalizeForCompare(value);
        return int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
    }

    private static (int Season, int Episode)? FindEpisodeUsingContext(string fileBase, IReadOnlyList<string> directories, string torrentName)
    {
        var season = FindSeason(fileBase)
            ?? directories.Select(FindSeason).FirstOrDefault(x => x.HasValue)
            ?? FindSeason(torrentName);

        // Explicit episode-only filenames are common for miniseries and Rutracker-style
        // releases, e.g. "North & South ... 1 серия.mkv". When no season is declared
        // anywhere, treat an explicit episode marker as season 1.
        var explicitEpisode = FindExplicitEpisodeOnly(fileBase);
        if (explicitEpisode.HasValue)
        {
            return (season ?? 1, explicitEpisode.Value);
        }

        // Bare leading numbers are weaker evidence, so only accept them when a season
        // is known from the filename/folder/torrent context.
        var leadingEpisode = FindLeadingEpisodeOnly(fileBase);
        return season.HasValue && leadingEpisode.HasValue
            ? (season.Value, leadingEpisode.Value)
            : null;
    }

    private static (int Season, int Episode)? FindEpisode(string value)
    {
        var match = EpisodeRegex().Match(value);
        if (match.Success)
        {
            return (ParseInt(match.Groups["s"].Value), ParseInt(match.Groups["e"].Value));
        }

        match = AltEpisodeRegex().Match(value);
        if (match.Success)
        {
            return (ParseInt(match.Groups["s"].Value), ParseInt(match.Groups["e"].Value));
        }

        match = RussianEpisodeRegex().Match(value);
        if (match.Success)
        {
            return (ParseInt(match.Groups["s"].Value), ParseInt(match.Groups["e"].Value));
        }

        return null;
    }

    private static int? FindSeason(string value)
    {
        if (SeasonRangeDetectionRegex().IsMatch(value))
        {
            return null;
        }

        var match = SeasonRegex().Match(value);
        return match.Success ? ParseInt(match.Groups["s"].Value) : null;
    }

    private static int? FindExplicitEpisodeOnly(string value)
    {
        var match = EpisodeOnlyRegex().Match(value);
        if (match.Success)
        {
            return ParseInt(match.Groups["e"].Value);
        }

        match = TrailingEpisodeOnlyRegex().Match(value);
        return match.Success ? ParseInt(match.Groups["e"].Value) : null;
    }

    private static int? FindLeadingEpisodeOnly(string value)
    {
        var match = LeadingEpisodeRegex().Match(value);
        return match.Success ? ParseInt(match.Groups["e"].Value) : null;
    }

    private static int ParseInt(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    public static string NormalizeForCompare(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return WhitespaceRegex().Replace(sb.ToString(), " ").Trim();
    }

    [GeneratedRegex(@"(?i)(?:^|[^A-Za-z0-9])S(?<s>\d{1,2})[ ._\-]*E(?<e>\d{1,3})(?:[^0-9]|$)")]
    private static partial Regex EpisodeRegex();

    [GeneratedRegex(@"(?i)(?:^|[^0-9])(?<s>\d{1,2})x(?<e>\d{1,3})(?:[^0-9]|$)")]
    private static partial Regex AltEpisodeRegex();

    [GeneratedRegex(@"(?i)(?:сезон|season)[ ._:\-]*(?<s>\d{1,2}).{0,30}?(?:серия|episode|ep)[ ._:\-]*(?<e>\d{1,3})(?![ ._:\-]*(?:-|–|—)\d)")]
    private static partial Regex RussianEpisodeRegex();

    [GeneratedRegex(@"(?i)(?:^|[^A-Za-z0-9])(?:S|Season[ ._:\-]*|Сезон[ ._:\-]*)(?<s>\d{1,2})(?:[^0-9]|$)")]
    private static partial Regex SeasonRegex();

    [GeneratedRegex(@"(?i)(?:^|[^A-Za-z0-9])(?:S|Season[ ._:\-]*|Сезон[ ._:\-]*)\d{1,2}[ ._:\-]*(?:-|–|—|to|до)[ ._:\-]*(?:S|Season[ ._:\-]*|Сезон[ ._:\-]*)?\d{1,2}")]
    private static partial Regex SeasonRangeDetectionRegex();

    [GeneratedRegex(@"(?i)(?:^|[ ._\-\[])(?:E|EP|Episode|Серия)[ ._:\-]*(?<e>\d{1,3})(?:[ ._\-\]]|$)")]
    private static partial Regex EpisodeOnlyRegex();

    [GeneratedRegex(@"(?i)(?:^|[ ._\-\[])(?<e>\d{1,3})[ ._:\-]*(?:серия|серии|episode|ep)(?:[ ._\-\]]|$)")]
    private static partial Regex TrailingEpisodeOnlyRegex();

    [GeneratedRegex(@"^(?<e>\d{1,3})(?:[ ._\-]|$)")]
    private static partial Regex LeadingEpisodeRegex();

    [GeneratedRegex(@"(?i)(?:^|[ ._\-\[])(?:S|Season[ ._:\-]*|Сезон[ ._:\-]*)(?:\d{1,2})[ ._:\-]*(?:-|–|—|to|до)[ ._:\-]*(?:S|Season[ ._:\-]*|Сезон[ ._:\-]*)?\d{1,2}(?:[ ._\-\]]|$)")]
    private static partial Regex SeasonPackRegex();

    [GeneratedRegex(@"(?i)(?:^|[ ._\-\[])(?:S|Season[ ._:\-]*|Сезон[ ._:\-]*)(?:\d{1,2})(?:[ ._\-\]]|$)")]
    private static partial Regex StandaloneSeasonRegex();

    [GeneratedRegex(@"(?<!\d)(?:19\d{2}|20\d{2})(?!\d)")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"(?i)^\s*(?:S|Season[ ._:\-]*|Сезон[ ._:\-]*)\d{1,2}\s*$")]
    private static partial Regex SeasonOnlyRegex();

    [GeneratedRegex(@"[._]+")]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"(?i)\[(?:[^\]]*(?:1080|2160|720|web|bluray|x26|hevc|hdr|rus|eng|aac|dts)[^\]]*)\]")]
    private static partial Regex BracketNoiseRegex();

    [GeneratedRegex(@"(?i)(?:\b(?:10bit|8bit|hi10p|10-bit|5\.1|7\.1|2\.0|60fps|50fps|24fps)\b|\b(?:h|x)\s*26[45]\b)")]
    private static partial Regex CodecGarbageRegex();

    [GeneratedRegex(@"\s+/\s+")]
    private static partial Regex SlashTitleSeparatorRegex();

    [GeneratedRegex(@"(?i)^\s*(?:сезон|season|серии|серия|episodes?|эпизоды?)\s*[:\-]")]
    private static partial Regex TorrentMetadataSegmentRegex();

    [GeneratedRegex(@"(?i)\s*\((?:тв|tv)[ ._\-]*\d+\)\s*$")]
    private static partial Regex TvRoleSuffixRegex();
}
