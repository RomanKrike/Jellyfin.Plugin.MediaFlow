using System.Text.Json;
using Jellyfin.Plugin.MediaFlow.Models;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class EpisodeMappingStore
{
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    // Serializes mapping changes with background/manual imports. No stale plan can commit after a rule change.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> _importGates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private Dictionary<string, TorrentEpisodeMapping>? _rules;

    public async Task<IDisposable> AcquireImportAsync(string hash, CancellationToken token)
    {
        var gate = _importGates.GetOrAdd(hash, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token).ConfigureAwait(false);
        return new Lease(gate);
    }

    public async Task<TorrentEpisodeMapping?> GetAsync(string? hash, CancellationToken token)
    {
        if (string.IsNullOrEmpty(hash)) return null;
        await _fileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadAsync(token).ConfigureAwait(false);
            return _rules!.GetValueOrDefault(hash.ToLowerInvariant());
        }
        finally { _fileGate.Release(); }
    }

    public async Task SetAsync(string hash, TorrentEpisodeMapping mapping, CancellationToken token)
    {
        EpisodeMapper.Validate(mapping);
        await _fileGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await LoadAsync(token).ConfigureAwait(false);
            var next = new Dictionary<string, TorrentEpisodeMapping>(_rules!, StringComparer.OrdinalIgnoreCase)
            { [hash.ToLowerInvariant()] = mapping };
            var path = GetPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(next, JsonOptions), token).ConfigureAwait(false);
            File.Move(temp, path, true);
            _rules = next;
        }
        finally { _fileGate.Release(); }
    }

    private async Task LoadAsync(CancellationToken token)
    {
        if (_rules is not null) return;
        var path = GetPath();
        // A malformed mapping file must fail visibly, never silently revert imports to native numbering.
        _rules = File.Exists(path)
            ? JsonSerializer.Deserialize<Dictionary<string, TorrentEpisodeMapping>>(
                await File.ReadAllTextAsync(path, token).ConfigureAwait(false), JsonOptions)
                ?? throw new InvalidDataException("Episode mapping file is empty.")
            : new Dictionary<string, TorrentEpisodeMapping>(StringComparer.OrdinalIgnoreCase);
    }

    private static string GetPath() => Path.Combine(
        (Plugin.Instance ?? throw new InvalidOperationException("MediaFlow is not initialized.")).DataFolderPath,
        "episode-mappings.json");

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
