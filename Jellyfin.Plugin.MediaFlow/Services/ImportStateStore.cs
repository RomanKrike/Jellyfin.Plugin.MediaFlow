using System.Text.Json;
using Jellyfin.Plugin.MediaFlow.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class ImportStateStore
{
    private readonly ILogger<ImportStateStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Dictionary<string, ImportStateEntry>? _entries;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public ImportStateStore(ILogger<ImportStateStore> logger)
    {
        _logger = logger;
    }

    public async Task<ImportStateEntry?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _entries!.TryGetValue(key, out var entry) ? entry : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(ImportStateEntry entry, CancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            entry.UpdatedAt = DateTimeOffset.UtcNow;
            _entries![entry.Key] = entry;
            await SaveUnsafeAsync(cancellationToken).ConfigureAwait(false);
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

            var path = GetStatePath();
            if (!File.Exists(path))
            {
                _entries = new Dictionary<string, ImportStateEntry>(StringComparer.Ordinal);
                return;
            }

            try
            {
                await using var stream = File.OpenRead(path);
                _entries = await JsonSerializer.DeserializeAsync<Dictionary<string, ImportStateEntry>>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                    ?? new Dictionary<string, ImportStateEntry>(StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Could not read MediaFlow state file. Starting with an empty state.");
                _entries = new Dictionary<string, ImportStateEntry>(StringComparer.Ordinal);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveUnsafeAsync(CancellationToken cancellationToken)
    {
        var path = GetStatePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(stream, _entries, JsonOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(temp, path, true);
    }

    private static string GetStatePath()
    {
        var plugin = Plugin.Instance ?? throw new InvalidOperationException("MediaFlow plugin is not initialized.");
        return Path.Combine(plugin.DataFolderPath, "state.json");
    }
}
