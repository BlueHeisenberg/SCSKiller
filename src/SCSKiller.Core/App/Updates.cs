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

// The update system's pure parts. Velopack itself lives in the App project.

/// <summary>A SemVer 2 version, X.Y.Z[-pre][+build] (a leading "v" is accepted: tags). Compares by SemVer precedence:
/// 1.5.0-alpha.3 &lt; 1.5.0-beta.1 &lt; 1.5.0-internal.1 &lt; 1.5.0 (internal sorts after beta by ASCII). Build metadata
/// is ignored.</summary>
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
            bool xn = x[i].All(char.IsAsciiDigit), yn = y[i].All(char.IsAsciiDigit);
            c = xn && yn ? Numeric(x[i], y[i]) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x[i], y[i]);
            if (c != 0) return Math.Sign(c);
        }
        return x.Length.CompareTo(y.Length);

        // any length: an identifier past ulong must not turn textual
        static int Numeric(string a, string b)
        {
            (a, b) = (a.TrimStart('0'), b.TrimStart('0'));
            return a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
        }
    }
}

/// <summary>Velopack's feed entries have no OS field, so each OS has feeds and packages of its own (<see cref="UpdateChannels.Signed"/>).</summary>
public enum FeedOs { Windows, Linux }

/// <summary>Which update feed to follow (§4.1, §4.5 item 5).</summary>
public static partial class UpdateChannels
{
    public const string Stable = "stable", Beta = "beta", Alpha = "alpha", Internal = "internal";
    /// <summary>From the public end to the earliest builds; each channel's entitlement flag has the same name.</summary>
    public static readonly string[] All = [Stable, Beta, Alpha, Internal];

    /// <summary>The OS whose feeds and packages this build takes.</summary>
    public static partial FeedOs Os { get; }

    /// <summary>The name <paramref name="channel"/>'s feed is signed for on <paramref name="os"/>: the feed file
    /// (releases.&lt;signed&gt;.json, still under the edge's v1/updates/&lt;channel&gt;/), the signature's channel, the
    /// replay record and Velopack's channel.</summary>
    public static string Signed(string channel, FeedOs os) => os == FeedOs.Linux ? channel + "-linux" : channel;

    /// <summary>The channel whose feed is signed as <paramref name="signed"/> on <paramref name="os"/>; null: none.</summary>
    public static string? Of(string signed, FeedOs os) => All.FirstOrDefault(c => Signed(c, os) == signed);

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

    /// <summary>The channel <see cref="Effective"/> checks in place of the wanted one the account doesn't get; null: the wanted
    /// one, or not known yet (signed in, no token read).</summary>
    public static string? SteppedDown(string? chosen, string buildChannel, IReadOnlyCollection<string>? ent, bool signedIn)
    {
        var wanted = chosen ?? buildChannel;
        if (signedIn && ent == null || !All.Contains(wanted)) return null;
        var e = Effective(chosen, buildChannel, ent);
        return e == wanted ? null : e;
    }

    /// <summary>About's channel: the running build's, or the one checked and why.</summary>
    public static string Shown(string? chosen, string buildChannel, IReadOnlyCollection<string>? ent, bool signedIn) =>
        SteppedDown(chosen, buildChannel, ent, signedIn) is { } c
            ? $"{Shown(c)} ({(signedIn ? "your Patreon membership doesn't include" : "sign in with Patreon for")} {chosen ?? buildChannel} builds)"
            : Shown(buildChannel);

    public static string Shown(string channel) => char.ToUpperInvariant(channel[0]) + channel[1..] + " channel";

    /// <summary>The note after a check of <paramref name="checkedChannel"/> found nothing newer: it names that channel when it
    /// isn't the wanted one.</summary>
    public static string UpToDate(string checkedChannel, string? chosen, string buildChannel) =>
        checkedChannel == (chosen ?? buildChannel) ? "SCSKiller is up to date." : $"SCSKiller is up to date on the {checkedChannel} channel.";

    /// <summary>A download from the <paramref name="staged"/> channel's feed still installs: it is the feed <see cref="Effective"/>
    /// picks. With the entitlements unknown (null: signed in, but no token read since the start, as at a logon without
    /// network) any channel from stable up to the chosen one does: it was downloaded while that channel was allowed.</summary>
    public static bool Installs(string staged, string? chosen, string buildChannel, IReadOnlyCollection<string>? ent) =>
        ent != null ? staged == Effective(chosen, buildChannel, ent)
            : Array.IndexOf(All, staged) is >= 0 and var at && at <= Array.IndexOf(All, chosen ?? buildChannel);
}

/// <summary>How this copy was installed, which says who updates it (<see cref="Installation"/>).</summary>
public enum InstallKind
{
    /// <summary>A dev build, or files nobody installed: nothing updates it.</summary>
    NotInstalled,
    /// <summary>Velopack's install on Windows, its portable zip too: the updater.</summary>
    Velopack,
    /// <summary>A Linux AppImage that Velopack's locator found through $APPIMAGE: the updater.</summary>
    AppImage,
    /// <summary>A Flatpak: its remote.</summary>
    Flatpak,
    /// <summary>A Linux distribution's package: its package manager.</summary>
    Package,
}

/// <summary>This copy's <see cref="InstallKind"/>. <paramref name="Manager"/>: a package's manager, as its marker names it.</summary>
public sealed record Installation(InstallKind Kind, string? Manager = null)
{
    public const string FlatpakInfo = "/.flatpak-info";
    // a package installs it beside the app, or beside the AppImage it ships, with the package manager's name as its first line
    public const string PackageMarker = ".scskiller-package";

    /// <summary>Velopack updates it: the updater checks, downloads and applies.</summary>
    public bool UpdatesItself => Kind is InstallKind.Velopack or InstallKind.AppImage;

    /// <summary>A Flatpak remote or a package manager updates it: the updater never runs.</summary>
    public bool UpdatedElsewhere => Kind is InstallKind.Flatpak or InstallKind.Package;

    /// <summary>The line the app shows in place of its update check; null where the updater runs, or nothing updates.</summary>
    public string? UpdatesFrom => Kind switch
    {
        InstallKind.Flatpak => "Updates come from your software store.",
        InstallKind.Package => "Updates come from your package manager.",
        _ => null,
    };

    /// <summary>A Flatpak or a package, found without Velopack; null otherwise, and always on Windows. <paramref name="appDir"/>:
    /// the app's own folder. <paramref name="env"/>, <paramref name="exists"/> and <paramref name="read"/> (a file's first
    /// line) are the process's environment and files.</summary>
    public static Installation? Elsewhere(bool windows, string appDir, Func<string, string?> env, Func<string, bool> exists, Func<string, string?> read)
    {
        if (windows) return null;
        // all three only in our own sandbox: another app's (a Flatpak Steam, say) that starts our AppImage has the first two
        if (exists(FlatpakInfo) && env("FLATPAK_ID") is { Length: > 0 } && appDir.StartsWith("/app/", StringComparison.Ordinal)) return new(InstallKind.Flatpak);
        foreach (var dir in new[] { appDir, env("APPIMAGE") is { Length: > 0 } image ? Path.GetDirectoryName(image) : null })
            if (dir != null && Path.Combine(dir, PackageMarker) is var marker && exists(marker))
                return new(InstallKind.Package, read(marker)?.Trim() is { Length: > 0 } name ? name : null);
        return null;
    }

    /// <summary><paramref name="elsewhere"/> (<see cref="Elsewhere"/>), else what Velopack's locator found
    /// (<paramref name="velopack"/>, asked only then): its install on Windows, an AppImage on Linux, or nothing.</summary>
    public static Installation Detect(Installation? elsewhere, bool windows, Func<bool> velopack) =>
        elsewhere ?? (velopack() ? new(windows ? InstallKind.Velopack : InstallKind.AppImage) : new(InstallKind.NotInstalled));

    /// <summary>This process's <see cref="Elsewhere"/>: read before Velopack starts, which it then never does.</summary>
    public static Installation? ThisElsewhere => thisElsewhere.Value;
    static readonly Lazy<Installation?> thisElsewhere = new(() =>
        Elsewhere(OperatingSystem.IsWindows(), AppContext.BaseDirectory, Environment.GetEnvironmentVariable, File.Exists, FirstLine));

    static string? FirstLine(string file)
    {
        try { return File.ReadLines(file).FirstOrDefault(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

/// <summary>A signed feed exactly as fetched: releases.&lt;signed channel&gt;.json and its .sig (<see cref="UpdateChannels.Signed"/>).</summary>
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
    /// its own channel on <paramref name="os"/>, be the record's channel or one whose feed that channel's check reads (§4.1:
    /// the channels before it), and one of them must list this full package, one for <paramref name="os"/>, with this
    /// version, size and SHA-256. No replay check: the version must still be newer than the running one
    /// (<see cref="AtStart"/>), so an old signed feed installs nothing older.</summary>
    public string? Refused(IReadOnlyDictionary<string, string> keys, FeedOs os)
    {
        var at = Array.IndexOf(UpdateChannels.All, Channel);
        if (at < 0) return $"unknown channel '{Channel}'";
        if (Feeds.Count == 0) return "no signed feed";
        if (!UpdateFeeds.PackageFor(os, FileName)) return $"{FileName} isn't a {os} package";
        var listed = false;
        foreach (var (channel, f) in Feeds)
        {
            if (Array.IndexOf(UpdateChannels.All, channel) is var c && (c < 0 || c > at)) return $"the {channel} feed isn't read on the {Channel} channel";
            try { FeedTrust.Verify(keys, UpdateChannels.Signed(channel, os), f.Feed, f.Sig); }
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
            : order > 0 ? StartStep.Apply : StartStep.Older;
    }
}

/// <summary><see cref="StagedUpdate.AtStart"/>: install it; it is the running version (the record and a marker go); its
/// handover failed (it waits for "Restart to update" or a quit, never a restart loop); older than what runs (it goes).</summary>
public enum StartStep { Apply, Installed, Failed, Older }

/// <summary>"Install updates automatically" (<see cref="Settings.InstallUpdatesAutomatically"/>): a downloaded update installs
/// at the app's next start, before anything there has begun, or at the tray's Quit. Never during Windows' shutdown, which
/// may cut the swap off halfway. "Restart to update" offers it whenever it is downloaded, with the setting on or off.
/// A download from the internal channel's feed never installs by itself.</summary>
public static class AutoInstall
{
    public const string Off = "Automatic install is off.";
    public const string Internal = "Updates on the internal channel install only through Restart to update.";
    public const string Compiling = "A compile is running.";
    public const string Offline = "A recording session's game or cleanup is running. The update installs once it has ended.";
    public const string Playing = "A game is running, or the games aren't known yet.";

    /// <summary>Whether a download from <paramref name="channel"/>'s feed installs by itself at a start or quit.</summary>
    public static bool On(Settings s, string channel) => s.InstallUpdatesAutomatically && channel != UpdateChannels.Internal;

    /// <summary>Why a download from <paramref name="channel"/>'s feed doesn't install by itself now; null: it does.</summary>
    public static string? HeldBack(Settings s, string channel, bool compiling, bool offline, bool playing) =>
        channel == UpdateChannels.Internal ? Internal : !s.InstallUpdatesAutomatically ? Off : compiling ? Compiling : offline ? Offline : playing ? Playing : null;

    /// <summary>"Restart to update" shows for every downloaded update that still installs, whatever the setting: its
    /// channel still the chosen one, and newer than <paramref name="current"/> whatever a feed lists. The one older one is
    /// <paramref name="backToStable"/>: "Go back to stable now"'s download, in the run that asked for it.</summary>
    public static bool OffersRestart(StagedUpdate? staged, string? chosen, AppVersion current, IReadOnlyCollection<string>? ent, bool backToStable = false) =>
        staged != null && UpdateChannels.Installs(staged.Channel, chosen, current.Channel, ent)
        && (AppVersion.Parse(staged.Version) is { } v && v.CompareTo(current) > 0 || backToStable && staged.Channel == UpdateChannels.Stable);

    public static string ReadyNote(string version, bool auto) => auto
        ? $"SCSKiller {version} is ready: it installs the next time SCSKiller starts or quits, or use Restart to update at the top."
        : $"SCSKiller {version} is ready: use Restart to update at the top.";
}

/// <summary>Where updates and their source come from. <see cref="GhRepo"/> is compiled in: renaming the public repo
/// strands every installed build's stable updates.</summary>
public static class UpdateFeeds
{
    public const string GhRepo = "BlueHeisenberg/SCSKiller";
    public static readonly Uri Packages = new("https://dl.scskiller.io/");   // alpha/beta/internal packages: VPS route only
    /// <summary>The app's background check; the Library's refresh and About's "Check for updates" check in between.</summary>
    public static readonly TimeSpan CheckEvery = TimeSpan.FromHours(1);

    /// <summary>Stable: the feed on GitHub Releases (§4.4). Other channels: the edge, behind the access token, through
    /// <see cref="RouteFailover"/> (a URL on its primary route).</summary>
    public static Uri Feed(string channel, string file) => channel == UpdateChannels.Stable
        ? new($"https://github.com/{GhRepo}/releases/latest/download/{file}")
        : new(RouteFailover.Default.Primary, EdgePath(channel, file));

    /// <summary>A file of an alpha, beta or internal channel on the edge, relative to a route.</summary>
    public static string EdgePath(string channel, string file) => $"v1/updates/{channel}/{file}";

    /// <summary>A package named by a signed feed: the version's own channel says where it lives (a beta feed also lists
    /// stable releases, §4.1). Its SHA-256 comes from the signed feed, so the host doesn't have to be trusted.</summary>
    public static Uri Package(AppVersion v, string fileName) => v.Channel == UpdateChannels.Stable
        ? new($"https://github.com/{GhRepo}/releases/download/v{v}/{Uri.EscapeDataString(fileName)}")
        : new(PackageHost, $"v1/updates/{v.Channel}/{Uri.EscapeDataString(fileName)}");

    /// <summary>Whether a feed's <paramref name="fileName"/> is a package for <paramref name="os"/>. vpk names one
    /// &lt;id&gt;-&lt;version&gt;-&lt;vpk channel&gt;-full.nupkg (or -delta): every Linux channel ends in -linux, no Windows one does.</summary>
    public static bool PackageFor(FeedOs os, string fileName) =>
        (fileName.EndsWith("-linux-full.nupkg", StringComparison.Ordinal) || fileName.EndsWith("-linux-delta.nupkg", StringComparison.Ordinal)) == (os == FeedOs.Linux);

    /// <summary>The first package a signed <paramref name="feed"/> lists that isn't for <paramref name="os"/>; null: none.</summary>
    public static string? Foreign(FeedOs os, byte[] feed)
    {
        try
        {
            using var j = JsonDocument.Parse(feed);
            foreach (var a in j.RootElement.GetProperty("Assets").EnumerateArray())
                if ((a.GetProperty("FileName").GetString() ?? "") is var f && !PackageFor(os, f)) return f;
            return null;
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new FeedRejectedException("a malformed feed");
        }
    }

    /// <summary>Why the offline key must not sign <paramref name="feed"/> as the feed of the signed channel
    /// <paramref name="signed"/> (tools/release-sign); null: it may. The client refuses a feed that lists another OS's
    /// package (<see cref="Foreign"/>), so a Windows feed never names a Linux package and a Linux feed names only Linux ones.</summary>
    public static string? Unsignable(string signed, byte[] feed)
    {
        if (Enum.GetValues<FeedOs>().Where(o => UpdateChannels.Of(signed, o) != null).ToList() is not [var os])
            return $"'{signed}' is no channel's feed: {string.Join(", ", UpdateChannels.All)}, with -linux for the Linux one";
        try { return Foreign(os, feed) is { } f ? $"it lists {f}, not a {os} package: the {signed} feed lists only {os} packages" : null; }
        catch (FeedRejectedException e) { return e.Message; }
    }

    /// <summary>The edge allows 10 full packages a day per device; 8 leaves a margin for requests this count misses.</summary>
    public const int PackagesPerDay = 8;
    static readonly TimeSpan[] Backoff = [TimeSpan.FromHours(1), TimeSpan.FromHours(2), TimeSpan.FromHours(4), TimeSpan.FromHours(12)];

    /// <summary>When a package may be requested again, or null now: after consecutive failed downloads the wait doubles from
    /// the last failure (1 h, 2 h, 4 h, then 12 h; <paramref name="asked"/>, a click, skips it), and an <paramref name="edge"/>
    /// package (not GitHub's) never goes over <see cref="PackagesPerDay"/> full packages in any 24 h, whoever asks. Times after
    /// <paramref name="now"/> (a clock set back) don't count: the edge's own limit still holds.</summary>
    public static DateTimeOffset? PackageHold(PackageTries t, DateTimeOffset now, bool asked, bool edge)
    {
        var recent = t.At.Where(a => Recent(a, now)).Order().ToList();
        DateTimeOffset? hold = edge && recent.Count >= PackagesPerDay ? recent[^PackagesPerDay] + TimeSpan.FromDays(1) : null;
        if (!asked && t.Failures > 0 && t.FailedAt is { } failed && failed <= now && failed + Backoff[Math.Min(t.Failures, Backoff.Length) - 1] is var wait && wait > (hold ?? DateTimeOffset.MinValue)) hold = wait;
        return hold > now ? hold : null;
    }

    internal static bool Recent(DateTimeOffset at, DateTimeOffset now) => at <= now && now - at < TimeSpan.FromDays(1);

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

/// <summary>A package as the signed feed names it.</summary>
public sealed record PackageFile(string FileName, long Size, string Sha256);

/// <summary>A package download that picks up where an interrupted one stopped. Its bytes go to update.part in the packages
/// folder, and update.part.json names the package and the server's validator (a strong ETag, else Last-Modified). A resume
/// asks for the rest with Range and If-Range, and keeps the bytes only on a 206 whose Content-Range continues the file to
/// the package's size and whose validator is the stored one: anything else starts over from byte 0. The finished file
/// reaches its place only with the feed's SHA-256, else it is deleted and the next download starts over.</summary>
public static class PackageResume
{
    sealed record Meta(string FileName, long Size, string Sha256, string Validator);

    static string Part(string dir) => Path.Combine(dir, "update.part");
    static string MetaOf(string dir) => Part(dir) + ".json";

    /// <summary>The partial download and its record, whichever package they are for.</summary>
    public static void Forget(string dir)
    {
        File.Delete(MetaOf(dir));   // first: a part without its record is never resumed
        File.Delete(Part(dir));
    }

    /// <summary>The stored validator when the partial file is <paramref name="p"/>'s and shorter than it.</summary>
    static string? Resumable(string dir, PackageFile p)
    {
        try
        {
            var m = JsonSerializer.Deserialize<Meta>(File.ReadAllBytes(MetaOf(dir)));
            var part = new FileInfo(Part(dir));
            return m is not null && m.FileName == p.FileName && m.Size == p.Size && string.Equals(m.Sha256, p.Sha256, StringComparison.OrdinalIgnoreCase)
                && RangeConditionHeaderValue.TryParse(m.Validator, out _) && part.Exists && part.Length > 0 && part.Length < p.Size ? m.Validator : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>The validator If-Range can carry: a strong ETag, else Last-Modified.</summary>
    static string? Validator(HttpResponseMessage r) =>
        r.Headers.ETag is { IsWeak: false } e ? e.Tag : r.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Downloads <paramref name="p"/> into <paramref name="to"/>, resuming the partial file in <paramref name="dir"/>
    /// (another package's is deleted). <paramref name="get"/>(from, If-Range validator, ct) sends the request, from 0 without
    /// Range. Its headers must arrive within <paramref name="headers"/>, and the body may stall for at most <paramref name="stall"/>.</summary>
    public static async Task DownloadAsync(string dir, PackageFile p, string to, Func<long, string?, CancellationToken, Task<HttpResponseMessage>> get,
        TimeSpan headers, TimeSpan stall, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var validator = Resumable(dir, p);
        if (validator is null) Forget(dir);
        var keep = true;   // false: these bytes are no start for the package
        var file = new FileStream(Part(dir), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
        try
        {
            var from = validator is null ? 0 : file.Length;
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(headers);
            using var r = await get(from, validator, wait.Token);
            wait.CancelAfter(Timeout.InfiniteTimeSpan);   // the body has its own stall limit
            if (from > 0 && r.StatusCode == HttpStatusCode.PartialContent)
            {
                if (r.Content.Headers.ContentRange is not { Unit: "bytes", From: var a, To: var b, Length: var n } || a != from || b != p.Size - 1 || n != p.Size || Validator(r) != validator)
                {
                    keep = false;
                    throw new IOException("the server's partial answer doesn't continue the download");
                }
                file.Seek(0, SeekOrigin.End);
            }
            else if (r.StatusCode == HttpStatusCode.OK)
            {
                File.Delete(MetaOf(dir));
                file.SetLength(0);
                if (Validator(r) is { } v) File.WriteAllBytes(MetaOf(dir), JsonSerializer.SerializeToUtf8Bytes(new Meta(p.FileName, p.Size, p.Sha256, v)));
            }
            else
            {
                keep = false;
                throw new IOException($"the package was answered with {(int)r.StatusCode}");
            }
            await using var body = await r.Content.ReadAsStreamAsync(ct);
            await UpdateFeeds.Download(body, file, p.Size - file.Position, stall, ct);
            if (file.Length != p.Size) throw new IOException($"the package ended after {file.Length} of {p.Size} bytes");
        }
        // a 4xx is no hiccup: a host or proxy that refuses the Range (416, 400) would refuse it at every try
        catch (Exception e) when (e is FeedRejectedException or HttpRequestException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError and not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests) })
        {
            keep = false;
            throw;
        }
        finally
        {
            await file.DisposeAsync();
            if (!keep) Forget(dir);
        }
        // the caller checks it too: here a resume that went wrong is dropped before it reaches the package's place
        await using (var done = File.OpenRead(Part(dir)))
            if (!string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(done, ct)), p.Sha256, StringComparison.OrdinalIgnoreCase))
                keep = false;
        if (!keep)
        {
            Forget(dir);
            throw new IOException("the downloaded package doesn't match the feed's SHA-256");
        }
        File.Move(Part(dir), to, overwrite: true);
        File.Delete(MetaOf(dir));
    }
}

/// <summary>When package requests were made in the last day, and the downloads that failed in a row (<see cref="UpdateFeeds.PackageHold"/>).</summary>
public sealed class PackageTries
{
    public List<DateTimeOffset> At { get; set; } = [];
    public int Failures { get; set; }
    public DateTimeOffset? FailedAt { get; set; }

    public void Requested(DateTimeOffset now) => At = [.. At.Where(a => UpdateFeeds.Recent(a, now)), now];
    public void Failed(DateTimeOffset now) => (Failures, FailedAt) = (Failures + 1, now);
    public void Succeeded() => (Failures, FailedAt) = (0, null);
}

/// <summary>The signed feeds an update check reads (§4.1, §4.3) and the packages they name. A 429's Retry-After holds back
/// only the host that sent it: a request to it fails at once with 429 until then.</summary>
public sealed class FeedClient(HttpMessageHandler handler, FeedTrust trust, Func<Task<string?>> token, Action<string>? log = null)
{
    readonly HttpClient http = new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    // keyed by the host as written: GitHub (stable), the edge's feeds and the package host each have their own limits
    readonly ConcurrentDictionary<string, long> retryAt = new();

    /// <summary>The OS whose signed feeds it reads (<see cref="UpdateChannels.Signed"/>).</summary>
    public FeedOs Os { get; init; } = UpdateChannels.Os;

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
        var signed = UpdateChannels.Signed(channel, Os);
        var name = $"releases.{signed}.json";
        var feed = await GetAsync(UpdateFeeds.Feed(channel, name), channel);
        var sig = await GetAsync(UpdateFeeds.Feed(channel, name + ".sig"), channel);
        trust.Accept(signed, feed, sig);
        if (UpdateFeeds.Foreign(Os, feed) is { } other) throw new FeedRejectedException($"the {signed} feed lists '{other}', not a {Os} package");
        return new(feed, sig);
    }

    async Task<byte[]> GetAsync(Uri url, string channel)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(1));
        using var r = await SendAsync(url, channel, HttpCompletionOption.ResponseContentRead, cts.Token);
        return await r.Content.ReadAsByteArrayAsync(cts.Token);
    }

    /// <summary>A package's response once its headers are in; the caller reads the body. <paramref name="from"/> &gt; 0: the
    /// rest from there, only while the package is still <paramref name="ifRange"/> (<see cref="PackageResume"/>).</summary>
    public Task<HttpResponseMessage> PackageAsync(AppVersion v, string fileName, CancellationToken ct, long from = 0, string? ifRange = null) =>
        SendAsync(UpdateFeeds.Package(v, fileName), v.Channel, HttpCompletionOption.ResponseHeadersRead, ct, from, ifRange);

    // The edge's channels need the access token; GitHub (stable) gets none.
    async Task<HttpResponseMessage> SendAsync(Uri url, string channel, HttpCompletionOption completion, CancellationToken ct, long from = 0, string? ifRange = null)
    {
        if (retryAt.TryGetValue(url.Host, out var at) && Environment.TickCount64 < at)
            throw new HttpRequestException($"{url.Host} asked to wait", null, HttpStatusCode.TooManyRequests);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (from > 0)
        {
            request.Headers.Range = new(from, null);
            request.Headers.IfRange = RangeConditionHeaderValue.Parse(ifRange!);
        }
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

/// <summary>The steps of one Velopack hook, each on its own: one that throws is logged and the ones after it still run, so
/// an uninstall that can't remove the scheduled task still takes the recorders out of the games.</summary>
public static class HookSteps
{
    public static void Run(string hook, Action<string> log, params Action[] steps)
    {
        log(hook);
        for (var i = 0; i < steps.Length; i++)
            try { steps[i](); }
            catch (Exception e) { log($"{hook}: step {i + 1} of {steps.Length} failed, the others ran: {e}"); }
    }

    /// <summary>The uninstall's steps: the driver-update task, the sign-in entry and the toast registration go, then the
    /// recorders come out of the games whatever happened to those. <paramref name="running"/>: the running processes' names, tests only.</summary>
    public static void Uninstall(AppStore store, Action<string> log, Action unregisterTask, Action removeStartup, Action removeToasts, IReadOnlySet<string>? running = null) =>
        Run("Uninstalling", log, unregisterTask, removeStartup, removeToasts, () => ScsKiller.RemoveAllRecorders(store, running));
}

/// <summary>Never apply an update under a running warm (§4.5 item 3). Every process running a queue item holds
/// <see cref="Name"/>; the app applies only while nobody does. The CLI never applies, and exits at once while the app
/// hands over to Update.exe (<see cref="MarkApplying"/>): a marker file, since a mutex dies with the app before the swap.</summary>
public static class Busy
{
    // SCSKILLER_TEST_SCOPE: a test run's suffix, set only by the tests and inherited by the processes they start
    internal static string Scope = ScopeFrom(Environment.GetEnvironmentVariable("SCSKILLER_TEST_SCOPE"));

    // anything but .test-<guid> keeps the production names: a stray value (a backslash, say) must not make them invalid
    internal static string ScopeFrom(string? value) =>
        value is { Length: 38 } && value.StartsWith(".test-", StringComparison.Ordinal) && Guid.TryParseExact(value[6..], "N", out _) ? value : "";

    public static string Name => @"Local\SCSKiller.Busy" + Scope;
    public static readonly TimeSpan ApplyingFor = TimeSpan.FromMinutes(2);   // an older marker is a crashed apply

    /// <summary>Held until disposed. The named object exists while any process has it open, so ownership (and its thread
    /// affinity, awkward across awaits) isn't needed.</summary>
    public static IDisposable Hold(string? name = null) => NamedMutex.Open(name ?? Name);

    /// <summary>A compile worker's hold, taken before it reads the marker (the updater writes the marker before it reads
    /// <see cref="IsHeld"/>), so an update and a compile never both go ahead; null, holding nothing, while an update is
    /// being handed over.</summary>
    public static IDisposable? TryHold(string dataDir, DateTimeOffset now, string? name = null)
    {
        var hold = Hold(name);
        if (!Applying(dataDir, now)) return hold;
        hold.Dispose();
        return null;
    }

    /// <summary>A compile holds <see cref="Name"/> in some SCSKiller process. On Linux also while the names can't be
    /// trusted (<see cref="NamedMutex.Held"/>): nothing waiting on it goes ahead.</summary>
    public static bool IsHeld(string? name = null) => NamedMutex.Held(name ?? Name);

    public const string CompileStarted = "A compile started meanwhile. The update installs when SCSKiller quits after it has finished.";

    /// <summary>The updater's side of the handshake, the last step before it hands an update over: the marker, then
    /// <see cref="IsHeld"/>. A compile worker holds Busy first, then reads the marker (<see cref="TryHold"/>), so one of the
    /// two always sees the other. Null, the marker left, while no compile can run anywhere. Otherwise why not, the marker
    /// taken back: the names can't be trusted (<see cref="NamedMutex.Unsafe"/>), or a compile holds Busy.
    /// <paramref name="lockRoot"/>: the folder <see cref="NamedMutex.Unsafe"/> checks, tests only.</summary>
    public static string? Handover(string dataDir, DateTimeOffset now, string? name = null, string? lockRoot = null)
    {
        MarkApplying(dataDir, now);
        var why = NamedMutex.Unsafe(lockRoot) is { } locks ? $"{locks}. No update installs until that is fixed."
            : IsHeld(name) ? CompileStarted
            : null;
        if (why != null) ClearApplying(dataDir);
        return why;
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
