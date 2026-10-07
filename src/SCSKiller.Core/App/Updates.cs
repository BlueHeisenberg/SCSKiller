using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NSec.Cryptography;

namespace SCSKiller.Core.App;

// The update system's pure parts (docs/patreon-and-updates.md §4); Velopack itself lives in the App project.

/// <summary>A SemVer 2 version, X.Y.Z[-pre][+build] (a leading "v" is accepted: tags). Compares by SemVer precedence:
/// 1.5.0-alpha.3 &lt; 1.5.0-beta.1 &lt; 1.5.0-internal.1 &lt; 1.5.0 (internal sorts after beta by ASCII, which is why the
/// internal channel allows downgrades, release-process.md §4.1). Build metadata is ignored.</summary>
public sealed partial record AppVersion(int Major, int Minor, int Patch, string Pre) : IComparable<AppVersion>
{
    [GeneratedRegex(@"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Form();

    public static AppVersion? Parse(string? s) => s != null && Form().Match(s.Trim()) is { Success: true } m
        && int.TryParse(m.Groups[1].Value, out var a) && int.TryParse(m.Groups[2].Value, out var b) && int.TryParse(m.Groups[3].Value, out var c)
        ? new(a, b, c, m.Groups[4].Value) : null;

    /// <summary>This build's version (AssemblyInformationalVersion, set from the tag by build/publish.ps1).</summary>
    public static AppVersion Current { get; } = Parse((Assembly.GetEntryAssembly() ?? typeof(AppVersion).Assembly)
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion) ?? new(0, 0, 0, "internal.0");

    /// <summary>The channel this version belongs to: its pre-release label; stable without one. An unknown label (and a dev
    /// build's 0.0.0-internal.0) is internal: it never goes to anyone else.</summary>
    public string Channel => Pre.Length == 0 ? UpdateChannels.Stable : Pre.Split('.')[0] is var l && UpdateChannels.All.Contains(l) ? l : UpdateChannels.Internal;

    public override string ToString() => Pre.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";

    public int CompareTo(AppVersion? o)
    {
        if (o is null) return 1;
        var c = (Major, Minor, Patch).CompareTo((o.Major, o.Minor, o.Patch));
        if (c != 0) return c;
        if (Pre.Length == 0 || o.Pre.Length == 0) return (Pre.Length == 0).CompareTo(o.Pre.Length == 0);   // a release sorts after its pre-releases
        string[] x = Pre.Split('.'), y = o.Pre.Split('.');
        for (var i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            bool xn = ulong.TryParse(x[i], out var xv), yn = ulong.TryParse(y[i], out var yv);
            c = xn && yn ? xv.CompareTo(yv) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return Math.Sign(c);
        }
        return x.Length.CompareTo(y.Length);
    }
}

/// <summary>Which update feed to follow (§4.1, §4.5 item 5).</summary>
public static class UpdateChannels
{
    public const string Stable = "stable", Beta = "beta", Alpha = "alpha", Internal = "internal";
    /// <summary>From the public end to the earliest builds; each channel's entitlement flag has the same name.</summary>
    public static readonly string[] All = [Stable, Beta, Alpha, Internal];

    /// <summary>The channels the account may pick: stable, plus each one its token's <c>ent</c> flags grant. Settings shows
    /// the picker only when there is more than stable.</summary>
    public static IReadOnlyList<string> Offered(IReadOnlyCollection<string>? ent) => All.Where(c => c == Stable || ent?.Contains(c) == true).ToList();

    /// <summary>The feed to check: the chosen channel (null: the running build's own) capped by the entitlement, stepping
    /// back towards stable (a lapsed alpha supporter with beta gets beta). Nothing is downgraded by this: Velopack only
    /// takes a newer version, so the installed beta stays until stable passes it.</summary>
    public static string Effective(string? chosen, string buildChannel, IReadOnlyCollection<string>? ent)
    {
        var at = Array.IndexOf(All, chosen ?? buildChannel);
        while (at > 0 && ent?.Contains(All[at]) != true) at--;
        return All[Math.Max(at, 0)];
    }

    /// <summary>The internal channel installs whatever its feed names, even an older version (§4.1).</summary>
    public static bool AllowsDowngrade(string channel) => channel == Internal;

    /// <summary>A download from the <paramref name="staged"/> channel's feed still installs: it is the feed <see cref="Effective"/>
    /// picks. With the entitlements unknown (null: signed in, but no token read since the start, as at a logon without
    /// network) any channel from stable up to the chosen one does: it was downloaded while that channel was allowed.</summary>
    public static bool Installs(string staged, string? chosen, string buildChannel, IReadOnlyCollection<string>? ent) =>
        ent != null ? staged == Effective(chosen, buildChannel, ent)
            : Array.IndexOf(All, staged) is >= 0 and var at && at <= Array.IndexOf(All, chosen ?? buildChannel);
}

/// <summary>A signed feed exactly as fetched: releases.&lt;channel&gt;.json and its .sig.</summary>
public sealed record SignedFeed(byte[] Feed, byte[] Sig);

/// <summary>A downloaded update, kept across runs so the next start installs it without the network. The record is no
/// authority: <see cref="Refused"/> checks it against the signed feeds its check fetched, kept beside it, and the
/// package's SHA-256 is checked again before it is applied.</summary>
public sealed record StagedUpdate(string Channel, string Version, string FileName, long Size, string Sha256, IReadOnlyDictionary<string, SignedFeed> Feeds)
{
    static string FileIn(string dataDir) => Path.Combine(dataDir, "update-staged.json");

    public static StagedUpdate? Load(string dataDir)
    {
        try
        {
            return JsonSerializer.Deserialize<StagedUpdate>(File.ReadAllText(FileIn(dataDir))) is { Channel: not null, Version: not null, FileName: not null, Sha256: not null, Feeds: not null } s
                && AppVersion.Parse(s.Version) != null ? s : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    public void Save(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(FileIn(dataDir), JsonSerializer.Serialize(this));
    }

    public static void Forget(string dataDir)
    {
        try { File.Delete(FileIn(dataDir)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Why this record isn't one its signed feeds back; null: they do. Each feed must verify with a pinned key for
    /// its own channel, be the record's channel or one whose feed that channel's check reads (§4.1: the channels before it),
    /// and one of them must list this full package with this version, size and SHA-256. No replay check: the version must
    /// still be newer than the running one (<see cref="AtStart"/>), so an old signed feed installs nothing older.</summary>
    public string? Refused(IReadOnlyDictionary<string, string> keys)
    {
        var at = Array.IndexOf(UpdateChannels.All, Channel);
        if (at < 0) return $"unknown channel '{Channel}'";
        if (Feeds.Count == 0) return "no signed feed";
        var listed = false;
        foreach (var (channel, f) in Feeds)
        {
            if (Array.IndexOf(UpdateChannels.All, channel) is var c && (c < 0 || c > at)) return $"the {channel} feed isn't read on the {Channel} channel";
            try { FeedTrust.Verify(keys, channel, f.Feed, f.Sig); }
            catch (FeedRejectedException e) { return $"the {channel} feed: {e.Message}"; }
            listed |= Lists(f.Feed);
        }
        return listed ? null : $"no signed feed lists {FileName} as recorded";
    }

    bool Lists(byte[] feed)
    {
        try
        {
            using var j = JsonDocument.Parse(feed);
            return j.RootElement.GetProperty("Assets").EnumerateArray().Any(a =>
                a.GetProperty("FileName").GetString() == FileName
                && AppVersion.Parse(a.GetProperty("Version").GetString()) is { } v && v == AppVersion.Parse(Version)
                && string.Equals(a.GetProperty("SHA256").GetString(), Sha256, StringComparison.OrdinalIgnoreCase)
                && a.GetProperty("Size").GetInt64() == Size
                && a.GetProperty("Type") is var t && (t.ValueKind == JsonValueKind.String ? t.GetString() == "Full" : t.GetInt32() == 1));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return false; }
    }

    /// <summary>At the app's start, running <paramref name="current"/>. <paramref name="handedOver"/>: <see cref="Busy"/>'s
    /// marker is still there, so the last handover's new version never started.</summary>
    public StartStep AtStart(AppVersion current, bool handedOver)
    {
        var order = AppVersion.Parse(Version)!.CompareTo(current);
        return order == 0 ? StartStep.Installed
            : handedOver ? StartStep.Failed
            : order > 0 || UpdateChannels.AllowsDowngrade(Channel) ? StartStep.Apply : StartStep.Older;
    }
}

/// <summary><see cref="StagedUpdate.AtStart"/>: install it; it is the running version (the record and a marker go); its
/// handover failed (it waits for "Restart to update" or a quit, never a restart loop); older than what runs (it goes).</summary>
public enum StartStep { Apply, Installed, Failed, Older }

/// <summary>"Install updates automatically" (<see cref="Settings.InstallUpdatesAutomatically"/>): a downloaded update installs
/// at the app's next start, before anything there has begun, or at the tray's Quit. Never during Windows' shutdown, which
/// may cut the swap off halfway. "Restart to update" offers it whenever it is downloaded, with the setting on or off.</summary>
public static class AutoInstall
{
    public const string Off = "Automatic install is off.";
    public const string Compiling = "A compile is running.";
    public const string Offline = "An offline session's game or cleanup is running.";
    public const string Playing = "A game is running, or the games aren't known yet.";

    /// <summary>Why a downloaded update doesn't install by itself now; null: it does.</summary>
    public static string? HeldBack(Settings s, bool compiling, bool offline, bool playing) =>
        !s.InstallUpdatesAutomatically ? Off : compiling ? Compiling : offline ? Offline : playing ? Playing : null;

    /// <summary>"Restart to update" shows for every downloaded update that still installs, whatever the setting.</summary>
    public static bool OffersRestart(StagedUpdate? staged, string? chosen, string buildChannel, IReadOnlyCollection<string>? ent) =>
        staged != null && UpdateChannels.Installs(staged.Channel, chosen, buildChannel, ent);

    public static string ReadyNote(string version, Settings s) => s.InstallUpdatesAutomatically
        ? Loc.Format($"SCSKiller {version} is ready: it installs the next time SCSKiller starts or quits, or use Restart to update at the top.")
        : Loc.Format($"SCSKiller {version} is ready: use Restart to update at the top.");
}

/// <summary>Where updates and their source come from. <see cref="GhRepo"/> is compiled in: renaming the public repo
/// strands every installed build's stable updates.</summary>
public static class UpdateFeeds
{
    public const string GhRepo = "BlueHeisenberg/SCSKiller";
    public static readonly Uri Packages = new("https://dl.scskiller.io/");   // alpha/beta/internal packages: VPS route only (hosting.md §3)
    /// <summary>The app's background check; the Library's refresh and About's "Check for updates" check in between.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    /// <summary>Stable: the feed on GitHub Releases (§4.4). Other channels: the edge, behind the access token, through
    /// <see cref="RouteFailover"/> (a URL on its primary route).</summary>
    public static Uri Feed(string channel, string file) => channel == UpdateChannels.Stable
        ? new($"https://github.com/{GhRepo}/releases/latest/download/{file}")
        : new(RouteFailover.Default.Primary, $"v1/updates/{channel}/{file}");

    /// <summary>A package named by a signed feed: the version's own channel says where it lives (a beta feed also lists
    /// stable releases, §4.1). Its SHA-256 comes from the signed feed, so the host doesn't have to be trusted.</summary>
    public static Uri Package(AppVersion v, string fileName) => v.Channel == UpdateChannels.Stable
        ? new($"https://github.com/{GhRepo}/releases/download/v{v}/{Uri.EscapeDataString(fileName)}")
        : new(PackageHost, $"v1/updates/{v.Channel}/{Uri.EscapeDataString(fileName)}");

    /// <summary>A package body into <paramref name="to"/>: at most the signed feed's <paramref name="size"/> (its SHA-256 is
    /// only checked once the download ends), and refused when no byte arrives for <paramref name="stall"/>.</summary>
    public static async Task Download(Stream from, Stream to, long size, TimeSpan stall, CancellationToken ct)
    {
        var buf = new byte[81920];
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        for (long total = 0; ;)
        {
            idle.CancelAfter(stall);
            int n;
            try { n = await from.ReadAsync(buf, idle.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException($"no package bytes for {stall.TotalSeconds:0} s"); }
            if (n == 0) return;
            if ((total += n) > size) throw new FeedRejectedException($"a package over the feed's {size} bytes");
            await to.WriteAsync(buf.AsMemory(0, n), ct);
        }
    }

    // SCSKILLER_API (a local backend) serves the packages too
    static Uri PackageHost => Environment.GetEnvironmentVariable("SCSKILLER_API") is { Length: > 0 } ? RouteFailover.Default.Primary : Packages;

    /// <summary>The About page's "Download source code" (§4.5 item 7): the public repo's tag for stable; for alpha and beta
    /// the source zip next to the package on the edge (with the access token); none for internal and dev builds.</summary>
    public static Uri? Source(AppVersion v) => v.Channel switch
    {
        UpdateChannels.Stable => new($"https://github.com/{GhRepo}/tree/v{v}"),
        UpdateChannels.Alpha or UpdateChannels.Beta => new(RouteFailover.Default.Primary, $"v1/updates/{v.Channel}/source-{v}.zip"),
        _ => null,
    };
}

/// <summary>The signed feeds an update check reads (§4.1, §4.3) and the packages they name. A 429's Retry-After holds back
/// only the host that sent it: a request to it fails at once with 429 until then.</summary>
public sealed class FeedClient(HttpMessageHandler handler, FeedTrust trust, Func<Task<string?>> token, Action<string>? log = null)
{
    readonly HttpClient http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    // keyed by the host as written: GitHub (stable), the edge's feeds and the package host each have their own limits
    readonly ConcurrentDictionary<string, long> retryAt = new();

    /// <summary>The channel's feed, then those of the channels after it (§4.1), so a release published after this
    /// channel's last feed still reaches it. A later feed that can't be fetched is left out (Partial); a 404 is a channel
    /// with no feed, nothing left out; a feed that fails its signature fails the read.</summary>
    public async Task<(List<(string Channel, SignedFeed Feed)> Feeds, bool Partial)> ReadAsync(string channel)
    {
        List<(string, SignedFeed)> feeds = [];
        var partial = false;
        try { feeds.Add((channel, await SignedAsync(channel))); }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound && channel != UpdateChannels.Stable) { }   // none yet
        foreach (var later in UpdateChannels.All.TakeWhile(c => c != channel))
            try { feeds.Add((later, await SignedAsync(later))); }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound) { }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or InvalidOperationException or AccountException) { partial = true; }
        return (feeds, partial);
    }

    async Task<SignedFeed> SignedAsync(string channel)
    {
        var name = $"releases.{channel}.json";
        var feed = await GetAsync(UpdateFeeds.Feed(channel, name), channel);
        var sig = await GetAsync(UpdateFeeds.Feed(channel, name + ".sig"), channel);
        trust.Accept(channel, feed, sig);
        return new(feed, sig);
    }

    async Task<byte[]> GetAsync(Uri url, string channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var r = await SendAsync(url, channel, HttpCompletionOption.ResponseContentRead, cts.Token);
        return await r.Content.ReadAsByteArrayAsync(cts.Token);
    }

    /// <summary>A package's response once its headers are in; the caller reads the body.</summary>
    public Task<HttpResponseMessage> PackageAsync(AppVersion v, string fileName, CancellationToken ct) =>
        SendAsync(UpdateFeeds.Package(v, fileName), v.Channel, HttpCompletionOption.ResponseHeadersRead, ct);

    // The edge's channels need the access token; GitHub (stable) gets none.
    async Task<HttpResponseMessage> SendAsync(Uri url, string channel, HttpCompletionOption completion, CancellationToken ct)
    {
        if (retryAt.TryGetValue(url.Host, out var at) && Environment.TickCount64 < at)
            throw new HttpRequestException($"{url.Host} asked to wait", null, HttpStatusCode.TooManyRequests);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (channel != UpdateChannels.Stable)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await token() ?? throw new InvalidOperationException($"sign in for the {channel} channel"));
        var r = await http.SendAsync(request, completion, ct);
        if (r.IsSuccessStatusCode) return r;
        if (r.StatusCode == HttpStatusCode.TooManyRequests) Limited(url, r);
        using (r) r.EnsureSuccessStatusCode();
        return r;
    }

    void Limited(Uri url, HttpResponseMessage r)
    {
        var wait = r.Headers.RetryAfter is { Delta: { } d } ? d : r.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow ?? TimeSpan.FromHours(1);
        // the edge's longest window is a day: a larger or negative value is not believed
        wait = wait < TimeSpan.Zero ? TimeSpan.Zero : wait > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : wait;
        retryAt[url.Host] = Environment.TickCount64 + (long)wait.TotalMilliseconds;
        log?.Invoke($"429 from {url.Host} for {url.AbsolutePath}: no request to it for {wait:c}");
    }
}

public sealed class FeedRejectedException(string message) : Exception(message);

/// <summary>The signed feed (§4.3): releases.&lt;channel&gt;.json.sig next to Velopack's feed,
/// {"v":1,"channel","signed_at","feed_sha256","kid","sig"}, sig = Ed25519 over
/// "scskiller-feed-v1\n" + channel + "\n" + signed_at + "\n" + feed_sha256. A feed is used only once its signature
/// verifies with a pinned key, its hash matches, and it isn't older than the newest one accepted for that channel.</summary>
public sealed class FeedTrust(AppStore store, IReadOnlyDictionary<string, string> keys)
{
    /// <summary>The release public keys (kid -> base64 raw Ed25519 key), generated offline by the maintainer
    /// (tools/release-sign keygen): rel-a signs, rel-b is the backup.</summary>
    public static readonly IReadOnlyDictionary<string, string> ReleaseKeys = new Dictionary<string, string>
    {
        ["rel-a"] = "sitj5K2fcZku26c/EvUo793SeSuVIb0YcN+6/mi0CwM=",
        ["rel-b"] = "OBpxPkqkWZJIx6p89z8R4cEp52aMRcmeZQVHZQEZ0ns=",
    };

    static readonly SignatureAlgorithm Ed = SignatureAlgorithm.Ed25519;

    public static byte[] Message(string channel, string signedAt, string feedSha256) =>
        Encoding.UTF8.GetBytes($"scskiller-feed-v1\n{channel}\n{signedAt}\n{feedSha256}");

    /// <summary>The .sig file for <paramref name="feed"/> (tools/release-sign; tests). <paramref name="seed"/>: the 32-byte private key.</summary>
    public static string Sign(ReadOnlySpan<byte> seed, string kid, string channel, byte[] feed, DateTimeOffset signedAt)
    {
        using var key = Key.Import(Ed, seed, KeyBlobFormat.RawPrivateKey);
        var at = signedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var sha = Convert.ToHexStringLower(SHA256.HashData(feed));
        return JsonSerializer.Serialize(new { v = 1, channel, signed_at = at, feed_sha256 = sha, kid, sig = Convert.ToBase64String(Ed.Sign(key, Message(channel, at, sha))) });
    }

    /// <summary>A new key pair: (base64 seed, base64 public key).</summary>
    public static (string Seed, string Public) NewKey()
    {
        using var key = Key.Create(Ed, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (Convert.ToBase64String(key.Export(KeyBlobFormat.RawPrivateKey)), Convert.ToBase64String(key.PublicKey.Export(KeyBlobFormat.RawPublicKey)));
    }

    /// <summary>Checks <paramref name="feed"/> against its <paramref name="sig"/> file for <paramref name="channel"/>, and
    /// remembers its signed_at (per channel: switching back to stable isn't a replay). Throws <see cref="FeedRejectedException"/>.</summary>
    public void Accept(string channel, byte[] feed, byte[] sig)
    {
        var at = Verify(keys, channel, feed, sig);
        lock (store)
        {
            var newest = store.LoadFeedTimes();
            if (newest.TryGetValue(channel, out var seen) && at < seen) throw new FeedRejectedException($"an older feed than one already seen ({seen:u})");
            newest[channel] = at;
            store.SaveFeedTimes(newest);
        }
    }

    /// <summary>The signature and hash checks of <see cref="Accept"/>, without its replay check: the feed's signed_at.</summary>
    public static DateTimeOffset Verify(IReadOnlyDictionary<string, string> keys, string channel, byte[] feed, byte[] sig)
    {
        string signedAt, sha, kid;
        byte[] signature;
        try
        {
            var j = JsonDocument.Parse(sig).RootElement;
            if (j.GetProperty("v").GetInt32() != 1) throw new FeedRejectedException("unknown signature version");
            if (j.GetProperty("channel").GetString() != channel) throw new FeedRejectedException("the signature is for another channel");
            (signedAt, sha, kid) = (j.GetProperty("signed_at").GetString()!, j.GetProperty("feed_sha256").GetString()!, j.GetProperty("kid").GetString()!);
            signature = Convert.FromBase64String(j.GetProperty("sig").GetString()!);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or NullReferenceException)
        {
            throw new FeedRejectedException("malformed signature file");
        }
        if (!keys.TryGetValue(kid, out var pub)) throw new FeedRejectedException($"unknown key '{kid}'");
        if (!PublicKey.TryImport(Ed, Convert.FromBase64String(pub), KeyBlobFormat.RawPublicKey, out var key) || !Ed.Verify(key!, Message(channel, signedAt, sha), signature))
            throw new FeedRejectedException("bad signature");
        if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(feed)), sha, StringComparison.OrdinalIgnoreCase))
            throw new FeedRejectedException("the feed doesn't match its signature");
        return DateTimeOffset.TryParse(signedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? at : throw new FeedRejectedException("malformed signed_at");
    }
}

/// <summary>Never apply an update under a running warm (§4.5 item 3). Every process running a queue item holds
/// <see cref="Name"/>; the app applies only while nobody does. The CLI never applies, and exits at once while the app
/// hands over to Update.exe (<see cref="MarkApplying"/>): a marker file, since a mutex dies with the app before the swap.</summary>
public static class Busy
{
    public const string Name = @"Local\SCSKiller.Busy";
    public static readonly TimeSpan ApplyingFor = TimeSpan.FromMinutes(2);   // an older marker is a crashed apply

    /// <summary>Held until disposed. The named object exists while any process has it open, so ownership (and its thread
    /// affinity, awkward across awaits) isn't needed.</summary>
    public static IDisposable Hold(string name = Name) => new Mutex(false, name);

    /// <summary>A compile worker's hold, taken before it reads the marker (the updater writes the marker before it reads
    /// <see cref="IsHeld"/>), so an update and a compile never both go ahead; null, holding nothing, while an update is
    /// being handed over.</summary>
    public static IDisposable? TryHold(string dataDir, DateTimeOffset now, string name = Name)
    {
        var hold = Hold(name);
        if (!Applying(dataDir, now)) return hold;
        hold.Dispose();
        return null;
    }

    public static bool IsHeld(string name = Name)
    {
        if (!Mutex.TryOpenExisting(name, out var m)) return false;
        m.Dispose();
        return true;
    }

    static string Marker(string dataDir) => Path.Combine(dataDir, "applying");

    public static void MarkApplying(string dataDir, DateTimeOffset now)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(Marker(dataDir), now.ToString("O", CultureInfo.InvariantCulture));
    }

    public static void ClearApplying(string dataDir) => File.Delete(Marker(dataDir));

    /// <summary>A marker of any age: a handover whose new version never started.</summary>
    public static bool Marked(string dataDir) => File.Exists(Marker(dataDir));

    public static bool Applying(string dataDir, DateTimeOffset now)
    {
        try
        {
            // a reader polls it: the updater's ClearApplying must be able to delete it meanwhile
            using var f = new FileStream(Marker(dataDir), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return DateTimeOffset.TryParse(new StreamReader(f).ReadToEnd(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
                && now - at < ApplyingFor && at - now < ApplyingFor;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
