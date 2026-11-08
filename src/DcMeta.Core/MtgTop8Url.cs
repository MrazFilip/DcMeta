using System.Web;

namespace DcMeta.Core;

/// <summary>Parses an MTGTop8 archetype URL as copied from the browser.</summary>
public static class MtgTop8Url
{
    /// <summary>
    /// Validates e.g. https://www.mtgtop8.com/archetype?a=2552&amp;meta=308&amp;f=EDH&amp;color_id=BR&amp;show=pop.
    /// All query parameters are kept, so the same filters apply on every listing page.
    /// Returns the normalized https URL and the format code (<c>f=</c>, default EDH).
    /// </summary>
    public static (Uri Url, string Format) Parse(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Host.Equals("mtgtop8.com", StringComparison.OrdinalIgnoreCase)
                 || uri.Host.EndsWith(".mtgtop8.com", StringComparison.OrdinalIgnoreCase))
            || !uri.AbsolutePath.TrimEnd('/').EndsWith("/archetype", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(HttpUtility.ParseQueryString(uri.Query)["a"]))
            throw new ArgumentException(
                $"Expected an MTGTop8 archetype URL like https://www.mtgtop8.com/archetype?a=2552&meta=308&f=EDH, got: {url}");

        var normalized = new UriBuilder(uri) { Scheme = "https", Port = -1 }.Uri;
        string format = HttpUtility.ParseQueryString(uri.Query)["f"] ?? "EDH";
        return (normalized, format);
    }
}
