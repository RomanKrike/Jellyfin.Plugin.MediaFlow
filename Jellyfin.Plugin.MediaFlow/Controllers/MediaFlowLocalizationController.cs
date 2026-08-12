using System.Net.Mime;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.MediaFlow.Controllers;

[ApiController]
[Authorize(Policy = Policies.RequiresElevation)]
[Route("MediaFlow/Admin/i18n")]
public sealed class MediaFlowLocalizationController : ControllerBase
{
    private const string DefaultCulture = "en-US";

    [HttpGet]
    [Produces(MediaTypeNames.Application.Json)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetStrings([FromQuery] string? culture)
    {
        var requested = NormalizeCulture(culture);
        var assembly = typeof(Plugin).Assembly;
        var resourceName = FindResource(assembly.GetManifestResourceNames(), requested)
            ?? FindResource(assembly.GetManifestResourceNames(), DefaultCulture)
            ?? throw new InvalidOperationException("MediaFlow localization resources are missing.");

        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Unable to open MediaFlow localization resource.");
        using var reader = new StreamReader(stream);
        return Content(reader.ReadToEnd(), MediaTypeNames.Application.Json);
    }

    private static string NormalizeCulture(string? culture)
    {
        if (string.IsNullOrWhiteSpace(culture))
        {
            return DefaultCulture;
        }

        var value = culture.Trim().Replace('_', '-');
        if (value.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
        {
            return "ru-RU";
        }

        if (value.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return "en-US";
        }

        return value;
    }

    private static string? FindResource(IEnumerable<string> names, string culture)
    {
        var suffix = ".Localization." + culture + ".json";
        return names.FirstOrDefault(x => x.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    }
}
