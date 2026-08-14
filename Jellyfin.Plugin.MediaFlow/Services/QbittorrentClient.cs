using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
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
    private bool _serverProbed;
    private QbittorrentServerInfo? _serverInfo;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public QbittorrentClient(ILogger<QbittorrentClient> logger)
    {
        _logger = logger;
    }

    public async Task<QbittorrentServerInfo> GetServerInfoAsync(CancellationToken cancellationToken)
    {
        await EnsureClientAndAuthAsync(cancellationToken).ConfigureAwait(false);
        return _serverInfo ?? new QbittorrentServerInfo();
    }

    public async Task<IReadOnlyList<QbTorrent>> GetTorrentsAsync(CancellationToken cancellationToken)
    {
        var config = GetConfig();
        using var response = await SendAsync(
            HttpMethod.Get,
            "api/v2/torrents/info?filter=all",
            null,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, "get torrent list", cancellationToken).ConfigureAwait(false);

        var torrents = await response.Content
            .ReadFromJsonAsync<List<QbTorrent>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? [];

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
            _logger.LogWarning(
                "MediaFlow has no qBittorrent movie/TV categories configured; no torrents will be processed.");
            return [];
        }

        return torrents
            .Where(x => allowedCategories.Contains(x.Category))
            .ToList();
    }

    public async Task<IReadOnlyList<QbTorrentFile>> GetFilesAsync(
        string hash,
        CancellationToken cancellationToken)
    {
        ValidateHash(hash);

        using var response = await SendAsync(
            HttpMethod.Get,
            $"api/v2/torrents/files?hash={Uri.EscapeDataString(hash)}",
            null,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, "get torrent files", cancellationToken).ConfigureAwait(false);

        return await response.Content
            .ReadFromJsonAsync<List<QbTorrentFile>>(JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    public Task SetFilePriorityAsync(
        string hash,
        int fileIndex,
        int priority,
        CancellationToken cancellationToken)
        => SetFilePriorityAsync(hash, [fileIndex], priority, cancellationToken);

    public async Task SetFilePriorityAsync(
        string hash,
        IReadOnlyCollection<int> fileIndexes,
        int priority,
        CancellationToken cancellationToken)
    {
        ValidateHash(hash);

        if (fileIndexes.Count == 0)
        {
            return;
        }

        if (priority is not 0 and not 1 and not 6 and not 7)
        {
            throw new ArgumentOutOfRangeException(
                nameof(priority),
                priority,
                "qBittorrent file priority must be 0, 1, 6 or 7.");
        }

        var ids = string.Join(
            '|',
            fileIndexes
                .Distinct()
                .OrderBy(x => x)
                .Select(x => x.ToString(CultureInfo.InvariantCulture)));

        var form = new Dictionary<string, string>
        {
            ["hash"] = hash,
            ["id"] = ids,
            ["priority"] = priority.ToString(CultureInfo.InvariantCulture)
        };

        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v2/torrents/filePrio",
            form,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, "set torrent file priority", cancellationToken).ConfigureAwait(false);
    }

    public async Task ToggleSequentialDownloadAsync(
        string hash,
        CancellationToken cancellationToken)
    {
        ValidateHash(hash);

        var form = new Dictionary<string, string>
        {
            ["hashes"] = hash
        };

        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v2/torrents/toggleSequentialDownload",
            form,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, "toggle sequential download", cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteTorrentAsync(
        string hash,
        bool deleteFiles,
        CancellationToken cancellationToken)
    {
        ValidateHash(hash);

        var form = new Dictionary<string, string>
        {
            ["hashes"] = hash,
            ["deleteFiles"] = deleteFiles ? "true" : "false"
        };

        using var response = await SendAsync(
            HttpMethod.Post,
            "api/v2/torrents/delete",
            form,
            cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(response, "delete torrent", cancellationToken).ConfigureAwait(false);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string relativeUrl,
        Dictionary<string, string>? form,
        CancellationToken cancellationToken)
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

    private async Task<HttpResponseMessage> SendCoreAsync(
        HttpMethod method,
        string relativeUrl,
        Dictionary<string, string>? form,
        CancellationToken cancellationToken)
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
            var signature = string.Join(
                '|',
                config.QbittorrentUrl,
                config.QbittorrentUsername,
                config.QbittorrentPassword,
                config.QbittorrentIgnoreTlsErrors);

            if (_client is null || !string.Equals(signature, _signature, StringComparison.Ordinal))
            {
                RecreateClient(config);
                _signature = signature;
                _authenticated = false;
                _serverProbed = false;
                _serverInfo = null;
            }

            if (!_authenticated)
            {
                await AuthenticateAsync(config, cancellationToken).ConfigureAwait(false);
                _authenticated = true;
            }

            if (!_serverProbed)
            {
                _serverInfo = await ProbeServerInfoAsync(cancellationToken).ConfigureAwait(false);
                _serverProbed = true;
                LogCompatibility(_serverInfo);
            }
        }
        finally
        {
            _sync.Release();
        }
    }

    private void RecreateClient(Configuration.PluginConfiguration config)
    {
        _client?.Dispose();

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true,
            AutomaticDecompression = DecompressionMethods.All
        };

        if (config.QbittorrentIgnoreTlsErrors)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        var baseUri = new Uri(config.QbittorrentUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _client = new HttpClient(handler)
        {
            BaseAddress = baseUri,
            Timeout = TimeSpan.FromSeconds(20)
        };

        // qBittorrent's WebAPI documentation requires Origin or Referer to match
        // the Host domain and port. Supplying both makes MediaFlow work reliably
        // with qBittorrent 5.x CSRF/host validation and reverse-proxy setups.
        _client.DefaultRequestHeaders.Referrer = baseUri;
        _client.DefaultRequestHeaders.TryAddWithoutValidation(
            "Origin",
            baseUri.GetLeftPart(UriPartial.Authority));

        var pluginVersion = Assembly.GetExecutingAssembly()
            .GetName()
            .Version?
            .ToString(3) ?? "dev";

        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"Jellyfin-MediaFlow/{pluginVersion}");
    }

    private async Task AuthenticateAsync(
        Configuration.PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.QbittorrentUsername))
        {
            // This remains supported for qBittorrent instances that explicitly bypass
            // authentication for the MediaFlow/Jellyfin subnet.
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

        using var response = await _client!.SendAsync(login, cancellationToken).ConfigureAwait(false);
        var body = (await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)).Trim();

        if (!response.IsSuccessStatusCode
            || !body.Equals("Ok.", StringComparison.OrdinalIgnoreCase)
                && !body.Equals("Ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"qBittorrent login failed with HTTP {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        _logger.LogDebug("Authenticated to qBittorrent at {Url}", config.QbittorrentUrl);
    }

    private async Task<QbittorrentServerInfo> ProbeServerInfoAsync(CancellationToken cancellationToken)
    {
        var appVersion = await ReadTextEndpointDirectAsync(
            "api/v2/app/version",
            cancellationToken).ConfigureAwait(false);

        var webApiVersion = await ReadTextEndpointDirectAsync(
            "api/v2/app/webapiVersion",
            cancellationToken).ConfigureAwait(false);

        return new QbittorrentServerInfo
        {
            ApplicationVersion = appVersion.Trim(),
            WebApiVersion = webApiVersion.Trim()
        };
    }

    private async Task<string> ReadTextEndpointDirectAsync(
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, relativeUrl);
        using var response = await _client!.SendAsync(request, cancellationToken).ConfigureAwait(false);

        await EnsureSuccessAsync(
            response,
            $"probe qBittorrent endpoint {relativeUrl}",
            cancellationToken).ConfigureAwait(false);

        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    private void LogCompatibility(QbittorrentServerInfo info)
    {
        var app = ParseLooseVersion(info.ApplicationVersion);
        var api = ParseLooseVersion(info.WebApiVersion);

        if (api is null || api.Major != 2)
        {
            throw new NotSupportedException(
                $"Unsupported qBittorrent WebAPI version '{info.WebApiVersion}'. MediaFlow requires WebAPI v2.");
        }

        if (app is not null && app.Major == 5 && app.Minor == 1)
        {
            _logger.LogInformation(
                "MediaFlow connected to qBittorrent {ApplicationVersion} (WebAPI {WebApiVersion}). qBittorrent 5.1.x compatibility mode is active.",
                info.ApplicationVersion,
                info.WebApiVersion);
            return;
        }

        if (app is not null && app.Major >= 5)
        {
            _logger.LogInformation(
                "MediaFlow connected to qBittorrent {ApplicationVersion} (WebAPI {WebApiVersion}) using the qBittorrent 5.x WebAPI.",
                info.ApplicationVersion,
                info.WebApiVersion);
            return;
        }

        _logger.LogWarning(
            "MediaFlow connected to legacy qBittorrent {ApplicationVersion} (WebAPI {WebApiVersion}). The integration is kept backward-compatible, but qBittorrent 5.1.x is the tested target.",
            info.ApplicationVersion,
            info.WebApiVersion);
    }

    private static Version? ParseLooseVersion(string value)
    {
        var normalized = value.Trim();

        if (normalized.StartsWith('v') || normalized.StartsWith('V'))
        {
            normalized = normalized[1..];
        }

        var end = normalized.IndexOfAny(['-', '+', '~', ' ']);
        if (end >= 0)
        {
            normalized = normalized[..end];
        }

        return Version.TryParse(normalized, out var parsed) ? parsed : null;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var details = string.IsNullOrWhiteSpace(body) ? response.ReasonPhrase : body.Trim();

        throw new HttpRequestException(
            $"qBittorrent failed to {operation}: HTTP {(int)response.StatusCode} ({response.StatusCode}). {details}",
            null,
            response.StatusCode);
    }

    private static void ValidateHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new ArgumentException("Torrent hash is required.", nameof(hash));
        }
    }

    private static Configuration.PluginConfiguration GetConfig()
        => Plugin.Instance?.Configuration
            ?? throw new InvalidOperationException("MediaFlow plugin is not initialized.");

    public void Dispose()
    {
        _client?.Dispose();
        _sync.Dispose();
    }
}
