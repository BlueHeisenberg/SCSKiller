namespace SCSKiller.Core.App;

/// <summary>A small text file fetched at run time, on first need (like the codecs CUE4Parse downloads next to the app):
/// the public archive keys of a game whose exe doesn't carry them, from the community tool that publishes them. One try,
/// 10 s: a failure only leaves the game Unsupported until the next Detect.</summary>
public static class Download
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The body at <paramref name="url"/>; null when it can't be had (offline, timeout, HTTP error).</summary>
    public static string? Text(string url) => Bytes(url) is { } b ? new StreamReader(new MemoryStream(b)).ReadToEnd() : null;

    /// <summary>The body at <paramref name="url"/> as served; null when it can't be had (offline, timeout, HTTP error).</summary>
    public static byte[]? Bytes(string url)
    {
        try
        {
            using var r = Http.Send(new HttpRequestMessage(HttpMethod.Get, url));
            if (!r.IsSuccessStatusCode) return null;
            using var s = new MemoryStream();
            r.Content.ReadAsStream().CopyTo(s);
            return s.ToArray();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or NotSupportedException) { return null; }
    }
}
