using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using NSec.Cryptography;

namespace SCSKiller.Core.App;

/// <summary>A content file that changes what gets compiled (plan-rules.json, game-verdicts.json) or speaks for SCSKiller (news.json) travels signed, since the edge serves only
/// .json and can't be trusted: {"v":1,"name","signed_at","sha256","kid","sig","body"}, body the document as a string,
/// sha256 the hex SHA-256 of its UTF-8 bytes, sig Ed25519 over "scskiller-content-v1\n" + name + "\n" + signed_at + "\n"
/// + sha256. Signed offline (tools/release-sign sign-content) with the rules keys, never the release keys. A body is
/// used only when the signature verifies with a pinned key, the name is the one asked for, the body is valid, and it
/// isn't older than the newest one accepted for that name on that channel. <paramref name="only"/>: the one name these keys
/// may sign (null: any); <paramref name="maxBytes"/>: the envelope's cap.</summary>
public sealed class ContentTrust(AppStore store, IReadOnlyDictionary<string, string> keys, string? only = null, int maxBytes = ContentFile.MaxBytes)
{
    /// <summary>The rules public keys (kid -> base64 raw Ed25519 key), generated offline by the maintainer
    /// (tools/release-sign keygen --for rules): rules-a signs, rules-b is the backup. build/publish.ps1 refuses a versioned
    /// build without rules-a.</summary>
    public static readonly IReadOnlyDictionary<string, string> RulesKeys = new Dictionary<string, string>
    {
        ["rules-a"] = "Hm0YVaMre3b7eyz2Y9W6t2QWGFtFkVQXkPgg2968TNk=",
        ["rules-b"] = "PAx76G91Ugb7A2B3CFaGzrYawhMtd5Nsuj1ewy/IZiM=",
    };

    /// <summary>The compat feed's public keys (kid -> base64 raw Ed25519 key): "feed-a" signs, "feed-b" is the next. The server
    /// signs compat.json online with them, so they sign nothing else (<see cref="CompatFeed"/>). Empty: no compat.json is accepted.</summary>
    public static readonly IReadOnlyDictionary<string, string> CompatKeys = new Dictionary<string, string>();

    static readonly SignatureAlgorithm Ed = SignatureAlgorithm.Ed25519;
    const string TimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    sealed record Envelope(int V, string Name, [property: JsonPropertyName("signed_at")] string SignedAt, string Sha256, string Kid, string Sig, string Body);

    /// <summary>Why the last envelope was refused (for the log); null after one was accepted.</summary>
    public string? Rejected { get; private set; }

    public static byte[] Message(string name, string signedAt, string sha256) =>
        Encoding.UTF8.GetBytes($"scskiller-content-v1\n{name}\n{signedAt}\n{sha256}");

    /// <summary>The envelope of <paramref name="body"/> (tools/release-sign; tests). <paramref name="seed"/>: the 32-byte private key.</summary>
    public static string Sign(ReadOnlySpan<byte> seed, string kid, string name, string body, DateTimeOffset signedAt)
    {
        using var key = Key.Import(Ed, seed, KeyBlobFormat.RawPrivateKey);
        var at = signedAt.UtcDateTime.ToString(TimeFormat, CultureInfo.InvariantCulture);
        var sha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        return System.Text.Json.JsonSerializer.Serialize(new { v = 1, name, signed_at = at, sha256 = sha, kid, sig = Convert.ToBase64String(Ed.Sign(key, Message(name, at, sha))), body });
    }

    /// <summary>The body of the envelope <paramref name="json"/> for <paramref name="name"/>, parsed; null when refused
    /// (<see cref="Rejected"/>). Its signed_at is remembered under <paramref name="key"/> (default the name; a channel's copy:
    /// <see cref="ChannelKey"/>) only once <paramref name="parse"/> took the body, so a valid but unusable file never locks out the
    /// last good one.</summary>
    public T? Accept<T>(string name, string json, Func<string, T?> parse, string? key = null) where T : class
    {
        if ((Rejected = Check(name, json, out var body, out var at)) != null) return null;
        if (parse(body) is not { } doc)
        {
            Rejected = "the body isn't valid";
            return null;
        }
        key ??= name;
        lock (store)
        {
            var newest = store.LoadContentTimes();
            if (newest.TryGetValue(key, out var seen) && at < seen)
            {
                Rejected = $"older than one already seen ({seen:u})";
                return null;
            }
            newest[key] = at;
            store.SaveContentTimes(newest);
        }
        return doc;
    }

    /// <summary>The channels besides stable that have their own copy of a channelled file.</summary>
    public static readonly string[] Channels = [UpdateChannels.Alpha, UpdateChannels.Beta];

    /// <summary>What a channel's copy is remembered under: the name for stable, "name@channel" for the others, so each channel
    /// only ever goes forward and stopping to read one never refuses the other's older copy.</summary>
    public static string ChannelKey(string name, string? channel) => channel == null ? name : $"{name}@{channel}";

    /// <summary>Where a channel's copy is kept: <paramref name="cacheFile"/> for stable, "&lt;stem&gt;@&lt;channel&gt;.json" beside it.</summary>
    public static string CacheFile(string cacheFile, string? channel) => channel == null ? cacheFile
        : Path.Combine(Path.GetDirectoryName(cacheFile) ?? "", $"{Path.GetFileNameWithoutExtension(cacheFile)}@{channel}.json");

    /// <summary>Game verdicts and Maximum mode: a channel's copy wins over a newer stable one, so an entry only in stable's
    /// reaches only stable, and a channel that answers 404 has no copy. Rules and news: the newest wins, so stable's reaches
    /// every channel.</summary>
    static bool ChannelFirst(string name) => name is Games.GameVerdicts.Name or Games.MaximumMode.Name;

    /// <summary>The kept copy that verifies among stable's (<paramref name="cacheFile"/>) and those of
    /// <paramref name="channels"/>: the newest, or for <see cref="ChannelFirst"/> names the newest channel's, else stable's;
    /// null when none does. The copies of the other channels are deleted. Null <paramref name="channels"/>: who reads which
    /// isn't known yet (no answer from the account), so every copy kept counts, which is what was read when it was last known.</summary>
    public T? Cached<T>(string name, Func<string, T?> parse, string cacheFile, IReadOnlyCollection<string>? channels) where T : class
    {
        T? stable = null;
        (T Doc, string At)? newest = null;
        foreach (var c in Channels.Prepend(null))
        {
            var file = CacheFile(cacheFile, c);
            if (c != null && channels?.Contains(c) == false)
            {
                try { File.Delete(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // left out anyway; the next check deletes it
                continue;
            }
            string json;
            try { json = File.ReadAllText(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (Accept(name, json, parse, ChannelKey(name, c)) is not { } doc) continue;
            if (c == null && ChannelFirst(name)) stable = doc;
            // the fixed-width UTC format sorts as text
            else if (ContentFile.Read<Envelope>(json, maxBytes)!.SignedAt is var at && (newest == null || string.CompareOrdinal(at, newest.Value.At) > 0))
                newest = (doc, at);
        }
        return newest?.Doc ?? stable;
    }

    /// <summary>Stable's copy of <paramref name="name"/>.json (/v1/content/) and, with an access <paramref name="token"/>, those
    /// of <paramref name="channels"/> (/v1/updates/&lt;channel&gt;/), each kept beside <paramref name="cacheFile"/> when it verifies
    /// and isn't older than that channel's last; then <see cref="Cached"/>. Each copy is a whole file. A channel that fails
    /// or refuses (401, 403) keeps its last copy, and so does one that answers 404 except for <see cref="ChannelFirst"/>
    /// names, whose kept copy is then deleted.</summary>
    public async Task<T?> FetchAsync<T>(string name, Func<string, T?> parse, string cacheFile, IReadOnlyCollection<string>? channels, string? token,
        RouteFailover? routes, TimeSpan? budget = null) where T : class
    {
        routes ??= RouteFailover.Default;
        var wait = budget ?? TimeSpan.FromSeconds(3);
        var from = new string?[] { null }.Concat(token == null ? [] : (channels ?? []).Where(Channels.Contains)).ToArray();
        var served = await Task.WhenAll(from.Select(c =>
            routes.GetContentStatusAsync(c == null ? $"v1/content/{name}.json" : UpdateFeeds.EdgePath(c, name + ".json"), wait, ContentFile.MaxBytes, c == null ? null : token)));
        for (var i = 0; i < from.Length; i++)
            if (served[i].Body is { } json && Accept(name, json, parse, ChannelKey(name, from[i])) != null)
                ContentFile.Keep(CacheFile(cacheFile, from[i]), json);
        if (!ChannelFirst(name)) return Cached(name, parse, cacheFile, channels);
        var none = from.Where((_, i) => served[i].Status == System.Net.HttpStatusCode.NotFound).OfType<string>();
        return Cached(name, parse, cacheFile, channels?.Except(none).ToArray());
    }

    /// <summary>Why the envelope is refused, or null with its body and signed_at.</summary>
    string? Check(string name, string json, out string body, out DateTimeOffset at)
    {
        (body, at) = ("", default);
        if (only != null && name != only) return $"these keys don't sign '{name}'";
        if (ContentFile.Read<Envelope>(json, maxBytes) is not { } e) return "malformed envelope";
        if (e.V != 1) return "unknown envelope version";
        if (e.Name != name) return $"signed for '{e.Name}'";
        if (!keys.TryGetValue(e.Kid, out var pub)) return $"unknown key '{e.Kid}'";
        byte[] sig;
        try { sig = Convert.FromBase64String(e.Sig); }
        catch (FormatException) { return "malformed signature"; }
        if (!PublicKey.TryImport(Ed, Convert.FromBase64String(pub), KeyBlobFormat.RawPublicKey, out var key) || !Ed.Verify(key!, Message(name, e.SignedAt, e.Sha256), sig))
            return "bad signature";
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(e.Body))), e.Sha256, StringComparison.Ordinal))
            return "the body doesn't match its signature";
        if (!DateTimeOffset.TryParseExact(e.SignedAt, TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out at))
            return "malformed signed_at";
        body = e.Body;
        return null;
    }
}
