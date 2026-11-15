using System.Security.Cryptography;

namespace SCSKiller.Core.App;

/// <summary>A pinned third-party file fetched at run time, on first need: the public archive keys of a game whose exe
/// doesn't carry them in the clear, from the community tool that publishes them. The request is a bare GET of a constant
/// URL (no headers, cookies, credentials or redirects), so it carries nothing about the user or the game. One try, 10 s:
/// a failure leaves the game on its keyless route until the next detect.</summary>
public static class Download
{
    // no trace context header either (an Activity would add one), and no proxy, which could be handed this PC's credentials
    static readonly SocketsHttpHandler Handler = new() { AllowAutoRedirect = false, UseCookies = false, Credentials = null, PreAuthenticate = false, ActivityHeadersPropagator = null, UseProxy = false };
    // the timeout covers the body too: it is read whole before Send returns
    static readonly HttpClient Http = new(Handler) { Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = MaxBytes };
    const int MaxBytes = 1 << 20;

    /// <summary>The app's log, for the readers' Detect, which has none of its own.</summary>
    internal static IProgress<string>? Log { get; set; }

    /// <summary>A keyless game's <see cref="EngineInfo.Note"/> once its download gave no key that opens it.</summary>
    public const string FailedNote = "the key download failed, you can add the key by hand";

    /// <summary>What the readers fetch with: <see cref="Fetch"/>. The tests replace it, so no test goes online.</summary>
    internal static Func<string, byte[]?> Get = Fetch;

    /// <summary>The body at <paramref name="url"/> as served; null when it can't be had (offline, timeout, HTTP error, over 1 MB).</summary>
    public static byte[]? Fetch(string url)
    {
        try
        {
            using var r = Http.Send(new HttpRequestMessage(HttpMethod.Get, url));
            if (!r.IsSuccessStatusCode) return null;
            using var s = r.Content.ReadAsStream();
            using var m = new MemoryStream();
            s.CopyTo(m);
            return m.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or NotSupportedException or InvalidOperationException) { return null; }
    }

    /// <summary>The file at <paramref name="url"/> when its SHA-256 as served is <paramref name="sha256"/>, else null: a changed
    /// upstream file is never parsed. A mismatch goes to <paramref name="log"/> with the hash only.</summary>
    public static byte[]? Pinned(Func<string, byte[]?>? get, string url, string sha256, IProgress<string>? log)
    {
        log ??= Log;
        // test runs point the host at loopback (http://127.0.0.1:9 for the failure path), the pin still applies
        url = Url(url, Environment.GetEnvironmentVariable("SCSKILLER_TEST_KEY_DOWNLOAD"));
        if ((get ?? Get)(url) is not { } got) { log?.Report($"key download {url}: not available, the game stays keyless"); return null; }
        if (Convert.ToHexStringLower(SHA256.HashData(got)) is var sha && sha == sha256) { log?.Report($"key download {url}: {got.Length} bytes, pinned SHA-256"); return got; }
        log?.Report($"{url}: SHA-256 {sha}, not the pinned {sha256}: not used");
        return null;
    }

    // loopback only: the override can block the download, never send it anywhere else
    internal static string Url(string url, string? host) => Uri.TryCreate(host, UriKind.Absolute, out var u) && u.IsLoopback
        ? url.Replace("https://raw.githubusercontent.com", u.GetLeftPart(UriPartial.Authority)) : url;
}
