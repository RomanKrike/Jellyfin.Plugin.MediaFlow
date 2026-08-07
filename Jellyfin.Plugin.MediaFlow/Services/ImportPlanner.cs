using System.Text.RegularExpressions;
using Jellyfin.Plugin.MediaFlow.Models;

namespace Jellyfin.Plugin.MediaFlow.Services;

public static partial class ImportPlanner
{
    public static string BuildDestination(ParsedMedia parsed, TmdbCandidate candidate)
    {
        var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("MediaFlow configuration is unavailable.");
        var extension = Path.GetExtension(parsed.SourcePath).ToLowerInvariant();
        var title = SafeName(candidate.Title);
        var year = candidate.Year?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "Unknown";

        if (parsed.Kind == MediaKind.Movie)
        {
            var folder = $"{title} ({year}) [tmdbid-{candidate.Id}]";
            var file = $"{title} ({year}) [tmdbid-{candidate.Id}]{extension}";
            return SafeUnderRoot(config.MoviesRoot, Path.Combine(config.MoviesRoot, folder, file));
        }

        if (!parsed.Season.HasValue || !parsed.Episode.HasValue)
        {
            throw new InvalidOperationException("Episode is missing season or episode number.");
        }

        var showFolder = $"{title} ({year}) [tmdbid-{candidate.Id}]";
        var seasonFolder = $"Season {parsed.Season.Value:00}";
        var episodeTitle = string.IsNullOrWhiteSpace(candidate.EpisodeTitle) ? string.Empty : " - " + SafeName(candidate.EpisodeTitle);
        var episodeFile = $"{title} ({year}) - S{parsed.Season.Value:00}E{parsed.Episode.Value:00}{episodeTitle}{extension}";
        return SafeUnderRoot(config.ShowsRoot, Path.Combine(config.ShowsRoot, showFolder, seasonFolder, episodeFile));
    }

    private static string SafeUnderRoot(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(path);
        if (!fullPath.StartsWith(fullRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Destination escaped configured media root.");
        }

        return fullPath;
    }

    private static string SafeName(string value)
    {
        var cleaned = InvalidNameRegex().Replace(value, " ");
        cleaned = WhitespaceRegex().Replace(cleaned, " ").Trim(' ', '.');
        if (cleaned.Length > 160)
        {
            cleaned = cleaned[..160].Trim();
        }

        return string.IsNullOrWhiteSpace(cleaned) ? "Unknown" : cleaned;
    }

    [GeneratedRegex("[\\\\/:*?\"<>|]")]
    private static partial Regex InvalidNameRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
