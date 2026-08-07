using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jellyfin.Plugin.MediaFlow.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class QbittorrentClient : IDisposable
{
    private readonly ILogger<QbittorrentClient> _logger;
    private readonly SemaphoreSlim _sync = new(1, 1);
    private HttpClient? _client;
    private string _signature = string.Empty;
    private bool _authenticated;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public QbittorrentClient(ILogger<QbittorrentClient> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<QbTorrent>> GetTorrentsAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        using var response = await SendAsync(HttpMethod.Get, "api/v2/torrents/info?filter=all", null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var torrents = await response.Content.ReadFromJsonAsync<List<QbTorrent>>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
        var allowedCategories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(config.QbittorrentMovieCategory))
        {
            allowedCategories.Add(config.QbittorrentMovieCategory.Trim());
        }

        if (!string.IsNullOrWhiteSpace(config.QbittorrentTvCategory))
        {
            allowedCategories.Add(config.QbittorrentTvCategory.Trim());
        }

        if (allowedCategories.Count == 0)
        {
            _logger.LogWarning("MediaFlow has no qBittorrent movie/TV categories configured; no torrents will be processed.");
            return [];
        }

        return torrents
            .Where(x => allowedCategories.Contains(x.Category))
            .ToList();
    }

    public async Task<IReadOnlyList<QbTorrentFile>> GetFilesAsync(string hash, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, $"api/v2/torrents/files?hash={Uri.EscapeDataString(hash)}", null, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<QbTorrentFile>>(JsonOptions, cancellationToken).ConfigureAwait(false) ?? [];
    }

    public async Task SetFilePriorityAsync(string hash, int fileIndex, int priority, CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["hash"] = hash,
            ["id"] = fileIndex.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["priority"] = priority.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        using var response = await SendAsync(HttpMethod.Post, "api/v2/torrents/filePrio", form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string relativeUrl, Dictionary<string, string>? form, CancellationToken cancellationToken)
    {
        await EnsureClientAndAuthAsync(cancellationToken).ConfigureAwait(false);
        var response = await SendCoreAsync(method, relativeUrl, form, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            _authenticated = false;
            await EnsureClientAndAuthAsync(cancellationToken).ConfigureAwait(false);
            return await SendCoreAsync(method, relativeUrl, form, cancellationToken).ConfigureAwait(false);
        }

        return response;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string relativeUrl, Dictionary<string, string>? form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);
        if (form is not null)
        {
            request.Content = new FormUrlEncodedContent(form);
        }

        return await _client!.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureClientAndAuthAsync(CancellationToken cancellationToken)
    {
        await _sync.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var config = GetConfig();
            var signature = string.Join('|', config.QbittorrentUrl, config.QbittorrentUsername, config.QbittorrentPassword, config.QbittorrentIgnoreTlsErrors);
            if (_client is null || !string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                _client?.Dispose();
                var handler = new HttpClientHandler
                {
                    CookieContainer = new CookieContainer(),
                    UseCookies = true
                };
                if (config.QbittorrentIgnoreTlsErrors)
                {
                    handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
                }

                _client = new HttpClient(handler)
                {
                    BaseAddress = new Uri(config.QbittorrentUrl.TrimEnd('/') + "/", UriKind.Absolute),
                    Timeout = TimeSpan.FromSeconds(20)
                };
                _client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-MediaFlow/0.1.1");
                _signature = signature;
                _authenticated = false;
            }

            if (_authenticated)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(config.QbittorrentUsername))
            {
                _authenticated = true;
                return;
            }

            using var login = new HttpRequestMessage(HttpMethod.Post, "api/v2/auth/login")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = config.QbittorrentUsername,
                    ["password"] = config.QbittorrentPassword
                })
            };
            using var response = await _client.SendAsync(login, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || !body.Contains("Ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"qBittorrent login failed with HTTP {(int)response.StatusCode}.");
            }

            _authenticated = true;
            _logger.LogDebug("Authenticated to qBittorrent at {Url}", config.QbittorrentUrl);
        }
        finally
        {
            _sync.Release();
        }
    }

    private static Configuration.PluginConfiguration GetConfig()
        => Plugin.Instance?.Configuration ?? throw new InvalidOperationException("MediaFlow plugin is not initialized.");

    public void Dispose()
    {
        _client?.Dispose();
        _sync.Dispose();
    }
}
