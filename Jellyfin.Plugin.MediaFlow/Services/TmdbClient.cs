using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.MediaFlow.Models;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MediaFlow.Services;

public sealed class TmdbClient
{
    private readonly HttpClient _client;
    private readonly ILogger<TmdbClient> _logger;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public TmdbClient(ILogger<TmdbClient> logger)
    {
        _logger = logger;
        _client = new HttpClient
        {
            BaseAddress = new Uri("https://api.themoviedb.org/3/"),
            Timeout = TimeSpan.FromSeconds(20)
        };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("Jellyfin-MediaFlow/0.1");
    }

    public async Task<IReadOnlyList<TmdbCandidate>> SearchAsync(MediaKind kind, string query, int? year, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        EnsureConfigured(config.TmdbApiKey);

        var endpoint = kind == MediaKind.Episode ? "search/tv" : "search/movie";
        var url = $"{endpoint}?api_key={Uri.EscapeDataString(config.TmdbApiKey)}&query={Uri.EscapeDataString(query)}&language={Uri.EscapeDataString(config.TmdbLanguage)}&include_adult=false";
        if (year.HasValue)
        {
            url += kind == MediaKind.Episode
                ? $"&first_air_date_year={year.Value}"
                : $"&primary_release_year={year.Value}";
        }

        using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<SearchResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);

        return payload?.Results.Select(x => new TmdbCandidate
        {
            Id = x.Id,
            Kind = kind,
            Title = kind == MediaKind.Episode ? x.Name ?? string.Empty : x.Title ?? string.Empty,
            OriginalTitle = kind == MediaKind.Episode ? x.OriginalName ?? string.Empty : x.OriginalTitle ?? string.Empty,
            Year = ParseYear(kind == MediaKind.Episode ? x.FirstAirDate : x.ReleaseDate),
            Popularity = x.Popularity
        }).ToList() ?? [];
    }

    public async Task EnrichAliasesAsync(TmdbCandidate candidate, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        candidate.Aliases.Add(candidate.Title);
        candidate.Aliases.Add(candidate.OriginalTitle);

        var prefix = candidate.Kind == MediaKind.Episode ? $"tv/{candidate.Id}" : $"movie/{candidate.Id}";
        foreach (var suffix in new[] { "alternative_titles", "translations" })
        {
            try
            {
                using var response = await _client.GetAsync($"{prefix}/{suffix}?api_key={Uri.EscapeDataString(config.TmdbApiKey)}", cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
                CollectNames(document.RootElement, candidate.Aliases);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to enrich TMDb aliases for {Kind} {Id}", candidate.Kind, candidate.Id);
            }
        }
    }

    public async Task<string?> GetEpisodeTitleAsync(int seriesId, int season, int episode, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var languages = new[] { config.TmdbLanguage, config.TmdbFallbackLanguage }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var language in languages)
        {
            var url = $"tv/{seriesId}/season/{season}/episode/{episode}?api_key={Uri.EscapeDataString(config.TmdbApiKey)}&language={Uri.EscapeDataString(language)}";
            using var response = await _client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var payload = await JsonSerializer.DeserializeAsync<EpisodeResponse>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(payload?.Name))
            {
                return payload.Name;
            }
        }

        return string.Empty;
    }

    private static void CollectNames(JsonElement element, HashSet<string> output)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if ((property.NameEquals("title") || property.NameEquals("name")) && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var value = property.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(value) && value.Length <= 200)
                        {
                            output.Add(value);
                        }
                    }
                    else
                    {
                        CollectNames(property.Value, output);
                    }
                }
                break;
            case JsonValueKind.Array:
                foreach (var child in element.EnumerateArray())
                {
                    CollectNames(child, output);
                }
                break;
        }
    }

    private static int? ParseYear(string? value)
        => DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date) ? date.Year : null;

    private static void EnsureConfigured(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("TMDb API key is not configured.");
        }
    }

    private static Configuration.PluginConfiguration GetConfig()
        => Plugin.Instance?.Configuration ?? throw new InvalidOperationException("MediaFlow plugin is not initialized.");

    private sealed class SearchResponse
    {
        [JsonPropertyName("results")]
        public List<SearchItem> Results { get; set; } = [];
    }

    private sealed class SearchItem
    {
        [JsonPropertyName("id")]
        public int Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("original_title")]
        public string? OriginalTitle { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("original_name")]
        public string? OriginalName { get; set; }

        [JsonPropertyName("release_date")]
        public string? ReleaseDate { get; set; }

        [JsonPropertyName("first_air_date")]
        public string? FirstAirDate { get; set; }

        [JsonPropertyName("popularity")]
        public double Popularity { get; set; }
    }

    private sealed class EpisodeResponse
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
