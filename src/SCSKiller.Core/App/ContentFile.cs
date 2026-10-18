using System.Text.Json;

namespace SCSKiller.Core.App;

/// <summary>A list the app embeds and the server also serves under /v1/content/ so it can change without a release
/// (Games.StutterList, Planning.ConfirmedEngines). The server's copy is untrusted: a document over <see cref="MaxBytes"/>,
/// with an unknown field or failing the list's own parser is rejected whole. The last valid one is kept on disk for
/// offline starts.</summary>
public static class ContentFile
{
    public const int MaxBytes = 64 * 1024;

    static readonly JsonSerializerOptions Strict = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true,
    };

    /// <summary>The document when it is within <paramref name="maxBytes"/> (<see cref="MaxBytes"/>) and valid JSON of <typeparamref name="T"/>'s shape; else null.</summary>
    public static T? Read<T>(string json, int maxBytes = MaxBytes) where T : class
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > maxBytes) return null;
        try { return JsonSerializer.Deserialize<T>(json, Strict); }
        catch (JsonException) { return null; }
    }

    /// <summary>One element of a document read with the same strictness; null when it doesn't have <typeparamref name="T"/>'s shape.</summary>
    public static T? Read<T>(JsonElement e) where T : class
    {
        try { return e.Deserialize<T>(Strict); }
        catch (JsonException) { return null; }
    }

    // No control characters: the CLI prints these to a terminal.
    public static bool Text(string? s, int max) => !string.IsNullOrWhiteSpace(s) && s.Length <= max && !s.Any(char.IsControl);

    public static string Embedded(string resource) =>
        new StreamReader(typeof(ContentFile).Assembly.GetManifestResourceStream(resource)!).ReadToEnd();

    /// <summary>The server copy kept at <paramref name="file"/> by <see cref="FetchAsync"/>; null when missing or invalid.</summary>
    public static T? Cached<T>(string file, Func<string, T?> parse) where T : class
    {
        try { return parse(File.ReadAllText(file)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The server's copy of <paramref name="path"/> through <see cref="RouteFailover"/> within <paramref name="budget"/>
    /// (3 s), written to <paramref name="cacheFile"/> when valid. Null when no route answers with a valid document; never throws.</summary>
    public static async Task<T?> FetchAsync<T>(string path, string cacheFile, Func<string, T?> parse, RouteFailover? routes = null, TimeSpan? budget = null) where T : class
    {
        if (await (routes ?? RouteFailover.Default).GetContentAsync(path, budget ?? TimeSpan.FromSeconds(3), MaxBytes) is not { } json
            || parse(json) is not { } list) return null;
        Keep(cacheFile, json);
        return list;
    }

    /// <summary>A valid server copy to <paramref name="cacheFile"/>, for <see cref="Cached"/>.</summary>
    public static void Keep(string cacheFile, string json)
    {
        try
        {
            var tmp = cacheFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, cacheFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // used anyway; the next start fetches again
    }
}
