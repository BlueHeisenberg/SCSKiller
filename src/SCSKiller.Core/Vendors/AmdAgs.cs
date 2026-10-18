using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;

namespace SCSKiller.Core.Vendors;

/// <summary>The names an Unreal game registers with the AMD driver when it creates its device through AGS
/// (<c>agsDriverExtensionsDX12_CreateDevice</c>, UE 4.25-5.6 D3D12Adapter.cpp): the project name as the app,
/// "UnrealEngine&lt;major&gt;.&lt;minor&gt;" as the engine, both versions unspecified. AMD keys the DxcCache on the app name,
/// not on the exe name, unless a driver profile matches (<see cref="AmdAppCache.AgsKey"/>). <paramref name="ExeUnread"/>: the
/// exe can't be read (an Xbox app game's), so AGS is assumed; only the keys the game is seen holding prove it.</summary>
public sealed record AgsRegistration(string App, string Engine, bool ExeUnread = false);

public static class AmdAgs
{
    public const string DllName = "amd_ags_x64.dll";
    const string CreateDevice = "agsDriverExtensionsDX12_CreateDevice";

    /// <summary>Null unless the game is Unreal 4.25 or later (earlier versions create the D3D12 device without AGS, though
    /// they link it: 4.20-4.24 call only agsInit), its project name is known and it links AGS or its exe can't be read.</summary>
    public static AgsRegistration? Of(Game g, EngineInfo? engine)
    {
        if (engine?.Family != "Unreal" || Regex.Match(engine.Version, @"^(\d+)\.(\d+)") is not { Success: true } v) return null;
        var (major, minor) = (int.Parse(v.Groups[1].Value), int.Parse(v.Groups[2].Value));
        if (major < 4 || major == 4 && minor < 25 || ProjectName(g) is not { } project) return null;
        var uses = UsesAgs(g.ExePath);
        return uses == false ? null : new(project, $"UnrealEngine{major}.{minor}", ExeUnread: uses == null);
    }

    /// <summary>A non-Unreal game's registration, recovered from the D3D12 keys its own process held (<paramref name="gameKeys"/>)
    /// when none is its exe name's (<paramref name="exeName"/>, as it runs): the one string in the exe whose
    /// <see cref="AmdAppCache.AppNameKey"/> is one of them, in an exe that names <c>agsDriverExtensionsDX12_CreateDevice</c>.
    /// The engine name isn't in the key; the family is passed. Null for Unreal (<see cref="Of"/>), before the game is seen,
    /// an unreadable exe, or no single match (an app profile's key).</summary>
    public static AgsRegistration? Recovered(Game g, EngineInfo? engine, string exeName, IEnumerable<string> gameKeys)
    {
        if (engine == null || engine.Family == "Unreal") return null;
        var dxc = gameKeys.Where(k => k.StartsWith(AmdAppCache.D3D12Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        if (dxc.Count == 0 || dxc.Contains(AmdAppCache.HintKey(exeName))) return null;
        var fi = new FileInfo(g.ExePath);
        if (!fi.Exists) return null;
        // ponytail: in-memory per exe stamp and keys, so a restart scans again; persist on the GameRecord if that shows
        var stamp = (fi.FullName.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc, string.Join(',', dxc));
        if (!names.TryGetValue(stamp, out var app))
        {
            var hashes = dxc.Select(k => uint.TryParse(k.AsSpan(AmdAppCache.D3D12Prefix.Length), System.Globalization.NumberStyles.HexNumber, null, out var h) ? h : 0).ToHashSet();
            try
            {
                using var s = new FileStream(fi.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
                names[stamp] = app = StringsHashing(s, hashes) is [var one] ? one : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }   // not cached: a later read may succeed
        }
        return app == null || app.Equals(exeName, StringComparison.OrdinalIgnoreCase) ? null : new(app, engine.Family);
    }

    /// <summary>The registration the game's own process was seen making (the recorder hooks
    /// <c>agsDriverExtensionsDX12_CreateDevice</c> in a loaded <see cref="DllName"/>), only when the key it gives
    /// (<see cref="AmdAppCache.AgsKey"/>) is one the game's process held (<paramref name="gameKeys"/>): a name the driver
    /// keyed otherwise (an app profile, no app registration) is not proof.</summary>
    public static AgsRegistration? Learned(string? app, string? engine, string exeName, IEnumerable<string> gameKeys) =>
        app is { Length: > 0 } && gameKeys.Contains(AmdAppCache.AgsKey(exeName, app)) ? new(app, engine ?? "") : null;

    static readonly ConcurrentDictionary<(string, long, DateTime, string), string?> names = new();
    const int MaxName = 64;
    static readonly uint CreateDeviceHash = AmdAppCache.Fnv1a(CreateDevice);

    /// <summary>Up to two distinct NUL-terminated strings of 2-64 printable ASCII characters, plain or UTF-16LE at either byte parity
    /// (AGS takes the name wide; an exe may widen it at run time), whose <see cref="AmdAppCache.Fnv1a"/> is one of
    /// <paramref name="hashes"/>; empty unless the stream also holds <c>agsDriverExtensionsDX12_CreateDevice</c> (an import,
    /// an export or a GetProcAddress name). One pass, fixed memory.</summary>
    public static List<string> StringsHashing(Stream s, IReadOnlySet<uint> hashes)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var creates = false;
        Run ascii = new(), even = new(), odd = new();
        void End(Run r)
        {
            if (!r.Ends(hashes, out var str)) return;
            if (str == CreateDevice) creates = true;
            else if (found.Count < 2) found.Add(str);   // two already mean no single app name
        }
        var buf = new byte[1 << 20];
        long pos = 0;
        var prev = -1;
        for (int n; (n = s.Read(buf)) > 0;)
            for (var i = 0; i < n; i++, pos++)
            {
                var b = buf[i];
                if (b is >= 0x20 and <= 0x7E) ascii.Add(b); else if (b == 0) End(ascii); else ascii.Reset();
                if (prev >= 0)
                {
                    var r = (pos & 1) == 1 ? even : odd;   // the UTF-16 unit starting at pos - 1
                    if (b == 0 && prev is >= 0x20 and <= 0x7E) r.Add((byte)prev); else if (b == 0 && prev == 0) End(r); else r.Reset();
                }
                prev = b;
            }
        return creates ? [.. found.Order(StringComparer.Ordinal)] : [];
    }

    sealed class Run
    {
        readonly char[] chars = new char[MaxName];
        int length;
        bool over;
        uint hash = 0x811C9DC5;

        public void Add(byte c)
        {
            if (length == MaxName) { over = true; return; }
            chars[length++] = (char)c;
            hash = (hash ^ c) * 0x01000193 * 0x01000193;   // FNV-1a over the char's UTF-16LE bytes: c, then 0
        }

        public bool Ends(IReadOnlySet<uint> hashes, out string s)
        {
            var hit = !over && length >= 2 && (hashes.Contains(hash) || hash == CreateDeviceHash);
            s = hit ? new string(chars, 0, length) : "";
            Reset();
            return hit;
        }

        public void Reset() => (length, over, hash) = (0, false, 0x811C9DC5);
    }

    /// <summary>UE's FApp::GetProjectName as packaged: the folder holding <c>Binaries\&lt;platform&gt;\</c> the exe is in,
    /// else the only <c>&lt;install&gt;\&lt;Project&gt;\Content\Paks</c>. The case is the driver's: the key hashes it as is.</summary>
    public static string? ProjectName(Game g)
    {
        if (UnrealUserCache.Project(g.ExePath) is { } p) return p;
        if (!Directory.Exists(g.InstallDir)) return null;
        var projects = Directory.EnumerateDirectories(g.InstallDir)
            .Where(d => !Path.GetFileName(d).Equals("Engine", StringComparison.OrdinalIgnoreCase) && Directory.Exists(Path.Combine(d, "Content", "Paks")))
            .Select(Path.GetFileName).Take(2).ToList();
        return projects.Count == 1 ? projects[0] : null;
    }

    /// <summary>The AGS DLL a warm creates its device through: the game's own next to its exe when it is AGS 6 (the ABI
    /// scskiller_warm speaks; loaded read-only), else <paramref name="bundled"/>. UE links AGS statically, so most get
    /// the bundled one: the key depends on the app name only, not on the AGS build.</summary>
    public static string? DllFor(Game g, string? bundled)
    {
        var own = Path.Combine(Path.GetDirectoryName(g.ExePath) ?? "", DllName);
        try { return File.Exists(own) && FileVersionInfo.GetVersionInfo(own).FileMajorPart == 6 ? own : bundled; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return bundled; }
    }

    static readonly ConcurrentDictionary<(string, long, DateTime), bool?> exports = new();

    /// <summary>The game creates its device through AGS: an <see cref="DllName"/> next to the exe, or AGS linked into the exe
    /// (its static library declares every function dllexport, so a monolithic UE exe exports them). Null: the exe can't be
    /// read (access denied, as an Xbox app game's Shipping exe is), so it isn't known.</summary>
    public static bool? UsesAgs(string exePath)
    {
        if (File.Exists(Path.Combine(Path.GetDirectoryName(exePath) ?? "", DllName))) return true;
        var fi = new FileInfo(exePath);
        if (!fi.Exists) return false;
        return exports.GetOrAdd((fi.FullName.ToLowerInvariant(), fi.Length, fi.LastWriteTimeUtc), _ =>
        {
            try
            {
                using var pe = PeFile.Open(fi.FullName);
                return PeFile.ExportNames(pe).Contains(CreateDevice);
            }
            catch (UnauthorizedAccessException) { return null; }
            catch (Exception e) when (e is IOException or BadImageFormatException or InvalidOperationException) { return false; }
        });
    }
}
