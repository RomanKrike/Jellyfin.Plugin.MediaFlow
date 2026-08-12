using System.Text.Json;
using Jellyfin.Plugin.MediaFlow.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class MediaFlowLogStore
{
    private const int MaxEntries = 2000;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ILogger<MediaFlowLogStore> _logger;
    private List<MediaFlowLogEntry>? _entries;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public MediaFlowLogStore(ILogger<MediaFlowLogStore> logger)
    {
        _logger = logger;
    }

    public async Task AddAsync(
        string level,
        string source,
        string message,
        CancellationToken cancellationToken,
        string? torrentHash = null,
        string? torrentName = null,
        string? fileName = null)
    {
        try
        {
            await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var entry = new MediaFlowLogEntry
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    Level = NormalizeLevel(level),
                    Source = source ?? string.Empty,
                    Message = message ?? string.Empty,
                    TorrentHash = torrentHash,
                    TorrentName = torrentName,
                    FileName = fileName
                };

                _entries!.Add(entry);
                if (_entries.Count > MaxEntries)
                {
                    _entries.RemoveRange(0, _entries.Count - MaxEntries);
                    await RewriteUnsafeAsync(cancellationToken).ConfigureAwait(false);
                    return;
                }

                var path = GetLogPath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
                await File.AppendAllTextAsync(path, line, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not append MediaFlow structured log entry.");
        }
    }

    public async Task<IReadOnlyList<MediaFlowLogEntry>> GetAsync(int limit, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _entries!
                .OrderByDescending(x => x.Timestamp)
                .Take(Math.Clamp(limit, 1, 1000))
                .ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _entries!.Clear();
            var path = GetLogPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, string.Empty, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_entries is not null)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_entries is not null)
            {
                return;
            }

            _entries = [];
            var path = GetLogPath();
            if (!File.Exists(path))
            {
                return;
            }

            var lines = await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false);
            foreach (var line in lines.TakeLast(MaxEntries))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<MediaFlowLogEntry>(line, JsonOptions);
                    if (entry is not null)
                    {
                        _entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // Ignore a single malformed/truncated line. The rest of the log remains usable.
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RewriteUnsafeAsync(CancellationToken cancellationToken)
    {
        var path = GetLogPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var writer = new StreamWriter(temp, false))
        {
            foreach (var entry in _entries!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(JsonSerializer.Serialize(entry, JsonOptions)).ConfigureAwait(false);
            }
        }

        File.Move(temp, path, true);
    }

    private static string NormalizeLevel(string level)
        => level?.Trim().ToLowerInvariant() switch
        {
            "debug" => "Debug",
            "warning" or "warn" => "Warning",
            "error" or "err" => "Error",
            _ => "Information"
        };

    private static string GetLogPath()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("MediaFlow plugin is not initialized.");
        return Path.Combine(plugin.DataFolderPath, "mediaflow-log.jsonl");
    }
}
