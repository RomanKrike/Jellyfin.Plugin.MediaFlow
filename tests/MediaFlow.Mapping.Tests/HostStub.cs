// Only the Jellyfin host is substituted. Mapping, metadata, naming and writing are production sources.
namespace MediaBrowser.Model.Plugins { public class BasePluginConfiguration { } }
namespace Jellyfin.Plugin.MediaFlow
{
    public sealed class Plugin
    {
        public static Plugin? Instance { get; set; }
        public string DataFolderPath { get; set; } = "";
        public Configuration.PluginConfiguration Configuration { get; } = new();
    }
}

namespace MediaBrowser.Common.Api { public static class Policies { public const string RequiresElevation = "RequiresElevation"; } }
namespace Jellyfin.Plugin.MediaFlow.Services
{
    public sealed class QbittorrentClient
    {
        public List<Models.QbTorrent> Torrents { get; } = [];
        public List<Models.QbTorrentFile> Files { get; } = [];
        public Task<IReadOnlyList<Models.QbTorrent>> GetTorrentsAsync(CancellationToken token) => Task.FromResult<IReadOnlyList<Models.QbTorrent>>(Torrents);
        public Task<IReadOnlyList<Models.QbTorrentFile>> GetFilesAsync(string hash, CancellationToken token) => Task.FromResult<IReadOnlyList<Models.QbTorrentFile>>(Files);
    }
}
