using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.MediaFlow.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public bool Enabled { get; set; } = false;

    public string QbittorrentUrl { get; set; } = "http://127.0.0.1:8080";

    public string QbittorrentUsername { get; set; } = string.Empty;

    public string QbittorrentPassword { get; set; } = string.Empty;

    public string QbittorrentMovieCategory { get; set; } = "movie";

    public string QbittorrentTvCategory { get; set; } = "tv";

    public bool QbittorrentIgnoreTlsErrors { get; set; } = false;

    public string QbittorrentPathPrefix { get; set; } = string.Empty;

    public string LocalDownloadsPathPrefix { get; set; } = string.Empty;

    public string TmdbApiKey { get; set; } = string.Empty;

    public string TmdbLanguage { get; set; } = "ru-RU";

    public string TmdbFallbackLanguage { get; set; } = "en-US";

    public string MoviesRoot { get; set; } = "/media/Movies";

    public string ShowsRoot { get; set; } = "/media/Shows";

    public int PollIntervalSeconds { get; set; } = 10;

    public double AutoMatchScore { get; set; } = 82.0;

    public double MinimumScoreGap { get; set; } = 8.0;

    public bool ManageSequentialEpisodes { get; set; } = true;

    public bool BaselineExistingTorrentsOnFirstRun { get; set; } = true;

    public bool DryRunMode { get; set; } = true;

    public string DryRunTorrentFilter { get; set; } = string.Empty;

    public int DryRunMaxFiles { get; set; } = 3;

    public long MinimumVideoSizeMb { get; set; } = 50;

    public int RetryFailedAfterMinutes { get; set; } = 10;

    public string VideoExtensions { get; set; } = ".mkv,.mp4,.m4v,.avi,.mov,.ts,.m2ts,.webm,.mpg,.mpeg";
}
