namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class PathMapper
{
    public string BuildAndMap(string savePath, string relativeFileName)
    {
        var remote = Path.GetFullPath(Path.Combine(savePath, relativeFileName.Replace('\\', Path.DirectorySeparatorChar)));
        var config = Plugin.Instance?.Configuration ?? throw new InvalidOperationException("MediaFlow configuration is unavailable.");

        if (string.IsNullOrWhiteSpace(config.QbittorrentPathPrefix) || string.IsNullOrWhiteSpace(config.LocalDownloadsPathPrefix))
        {
            return remote;
        }

        var remotePrefix = NormalizePrefix(config.QbittorrentPathPrefix);
        var localPrefix = NormalizePrefix(config.LocalDownloadsPathPrefix);
        var normalizedRemote = NormalizePrefix(remote);

        if (!IsUnderOrEqual(normalizedRemote, remotePrefix))
        {
            throw new InvalidOperationException($"qBittorrent path '{remote}' is outside configured prefix '{config.QbittorrentPathPrefix}'.");
        }

        var suffix = normalizedRemote[remotePrefix.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var mapped = Path.GetFullPath(Path.Combine(localPrefix, suffix));

        if (!IsUnderOrEqual(mapped, localPrefix))
        {
            throw new InvalidOperationException("Mapped path escaped the configured downloads root.");
        }

        return mapped;
    }

    private static bool IsUnderOrEqual(string candidate, string root)
    {
        if (string.Equals(candidate, root, StringComparison.Ordinal))
        {
            return true;
        }

        var filesystemRoot = Path.GetPathRoot(root);
        if (string.Equals(root, filesystemRoot, StringComparison.Ordinal))
        {
            return candidate.StartsWith(root, StringComparison.Ordinal);
        }

        return candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static string NormalizePrefix(string path)
    {
        var full = Path.GetFullPath(path.Replace('\\', Path.DirectorySeparatorChar));
        var root = Path.GetPathRoot(full);
        if (string.Equals(full, root, StringComparison.Ordinal))
        {
            return full;
        }

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
