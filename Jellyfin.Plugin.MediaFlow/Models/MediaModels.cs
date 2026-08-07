namespace Jellyfin.Plugin.MediaFlow.Models;

public enum MediaKind
{
    Unknown = 0,
    Movie = 1,
    Episode = 2
}

public sealed record TitleSignal(string Value, double Weight, string Source);

public sealed record YearSignal(int Value, double Weight, string Source);

public sealed class ParsedMedia
{
    public MediaKind Kind { get; set; }

    public string TorrentName { get; set; } = string.Empty;

    public string RelativeFileName { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public List<TitleSignal> Titles { get; } = [];

    public List<YearSignal> Years { get; } = [];
}

public sealed class TmdbCandidate
{
    public int Id { get; set; }

    public MediaKind Kind { get; set; }

    public string Title { get; set; } = string.Empty;

    public string OriginalTitle { get; set; } = string.Empty;

    public int? Year { get; set; }

    public double Popularity { get; set; }

    public HashSet<string> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool? EpisodeExists { get; set; }

    public string? EpisodeTitle { get; set; }

    public int? EpisodeAirYear { get; set; }

    public double Score { get; set; }

    public List<string> Reasons { get; } = [];
}

public sealed class ResolutionResult
{
    public bool AutoApproved { get; set; }

    public string Reason { get; set; } = string.Empty;

    public TmdbCandidate? Selected { get; set; }

    public IReadOnlyList<TmdbCandidate> Candidates { get; set; } = [];
}

public sealed class ImportStateEntry
{
    public string Key { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string SourcePath { get; set; } = string.Empty;

    public string? DestinationPath { get; set; }

    public int? TmdbId { get; set; }

    public string? Message { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}
