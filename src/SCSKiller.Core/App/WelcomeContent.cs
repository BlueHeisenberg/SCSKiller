using System.Text.Json;

namespace SCSKiller.Core.App;

public sealed record WelcomeLink(string Text, string Url);

/// <summary>The first-run welcome dialog's text: welcome.json, embedded in Core (the default) and served at
/// /v1/content/welcome.json so it can change without a release. The server's copy is
/// untrusted: shown as plain text only, and rejected whole if anything in it fails <see cref="Parse"/>.</summary>
public sealed record WelcomeContent(string Title, IReadOnlyList<string> Paragraphs, WelcomeLink? Link, string Share, string SignIn, string Dismiss)
{
    public const string ContentPath = "v1/content/welcome.json";
    static readonly string[] LinkDomains = ["scskiller.com", "scskiller.io", "scskiller.xyz", "patreon.com"];

    public static WelcomeContent Default => field ??=
        Parse(new StreamReader(typeof(WelcomeContent).Assembly.GetManifestResourceStream("SCSKiller.Core.App.welcome.json")!).ReadToEnd())!;

    /// <summary>The document if it is valid JSON of the right shape within the length caps and its link (if any) is https
    /// to one of our domains or Patreon; else null.</summary>
    public static WelcomeContent? Parse(string json)
    {
        WelcomeContent? w;
        try { w = JsonSerializer.Deserialize<WelcomeContent>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)); }
        catch (JsonException) { return null; }
        static bool Ok(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max;
        return w != null && Ok(w.Title, 100) && Ok(w.Share, 150) && Ok(w.SignIn, 40) && Ok(w.Dismiss, 40)
            && w.Paragraphs is { Count: >= 1 and <= 8 } && w.Paragraphs.All(p => Ok(p, 1000))
            && (w.Link == null || Ok(w.Link.Text, 100) && IsAllowedLink(w.Link.Url)) ? w : null;
    }

    internal static bool IsAllowedLink(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0
        && LinkDomains.Any(d => u.Host.Equals(d, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));

    /// <summary>The server's copy through <see cref="RouteFailover"/> (.com, then .io on a connect error, timeout or route
    /// error page), within <paramref name="budget"/> (3 s) in total. Null when no route answers with a valid document;
    /// never throws.</summary>
    public static async Task<WelcomeContent?> FetchAsync(RouteFailover? routes = null, TimeSpan? budget = null) =>
        await (routes ?? RouteFailover.Default).GetContentAsync(ContentPath, budget ?? TimeSpan.FromSeconds(3), 64 * 1024) is { } json ? Parse(json) : null;
}
