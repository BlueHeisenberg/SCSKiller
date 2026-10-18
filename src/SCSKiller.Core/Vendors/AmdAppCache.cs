namespace SCSKiller.Core.Vendors;

/// <summary>AMD's shader caches are split per application (measured on driver 32.0.31041.1004 =
/// Adrenalin 26.8.1, see ARCHITECTURE.md). Files are <c>&lt;app&gt;.&lt;f2&gt;.&lt;kind&gt;.&lt;build&gt;.&lt;slot&gt;.parc</c>,
/// hex without leading zeros:
///   D3D12, <c>%LOCALAPPDATA%\AMD\DxcCache</c>: app = FNV-1a-32 of the exe file name's UTF-16LE bytes, case-sensitive,
///     path-independent (<see cref="DxcAppHash"/>) for most exes, BUT the driver's built-in app profiles override it: any
///     exe whose name contains "ff7rebirth" uses 6b2fcd83 whatever its case, Cyberpunk2077.exe 81de6978, eldenring.exe
///     b73b6179. So the name hash is only a hint: a game's key is what a process named like it holds open.
///   D3D11, <c>%LOCALAPPDATA%\AMD\DxCache</c>: app = a 64-bit hash of the exe file name (not a known hash of it: observed,
///     not derived); one kind.
/// slot: 0, and 1, 2... for a second process (or D3D11 device) of the same name at the same time; later processes open
/// every slot. Keys are "dxc:xxxxxxxx" and "dx:xxxxxxxxxxxxxxxx" (zero-padded), covering all slots, kinds and driver
/// builds of that app. Like NVIDIA, a key is attributed to a game by the files a process named like it holds open
/// (<see cref="KeysOpenBy"/>, during a warm or while the game runs): a D3D12 device keeps its DxcCache files open while it
/// lives, D3D11 only some of the time (a new name's first run was never seen holding its DxCache file), so a game's D3D11
/// share may be attributed only by a later warm. The driver writes the files through a memory map: their size grows,
/// their LastWriteTime doesn't, so mtimes say nothing. <c>%LOCALAPPDATA%\D3DSCache</c> (the Windows runtime's DXBC->DXIL
/// conversions) is not this cache: App.D3DSCache finds a game's folders there.</summary>
public sealed class AmdAppCache(string dxcDir, string dxDir) : IAppCache
{
    public const string D3D12Prefix = "dxc:", D3D11Prefix = "dx:";

    public string DxcDir { get; } = dxcDir;   // D3D12
    public string DxDir { get; } = dxDir;     // D3D11

    /// <summary>"207c35a9.dfac411e.71efbc0e.2b1a674a.0.parc" in DxcCache -> "dxc:207c35a9"; null for anything else.</summary>
    public static string? Key(string fileName, bool d3d12)
    {
        var parts = fileName.Split('.');
        if (parts.Length != 6 || !parts[5].Equals("parc", StringComparison.OrdinalIgnoreCase) || !int.TryParse(parts[4], out _)) return null;
        for (int i = 0; i < 4; i++)
            if (parts[i].Length is 0 or > 16 || !ulong.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out _)) return null;
        var app = ulong.Parse(parts[0], System.Globalization.NumberStyles.HexNumber);
        return d3d12 ? (app <= uint.MaxValue ? $"{D3D12Prefix}{app:x8}" : null) : $"{D3D11Prefix}{app:x16}";
    }

    /// <summary>HINT ONLY: the DxcCache app hash the driver uses for an exe without an app profile: FNV-1a-32 over the exe
    /// file name's UTF-16LE bytes, exactly as the process was launched (case matters: "AMDTESTA.EXE" and "amdtestA.exe" are
    /// two caches). Verified on selftest.exe, chrome.exe, steamwebhelper.exe, Discord.exe, testhost.exe, bg3.exe,
    /// ff7remake_.exe and the study's fake names. Wrong for profiled exes (<see cref="ProfileKey"/>): a game's real key is
    /// learned with <see cref="KeysOpenBy"/>. Clear cache deletes by it only while the game has no D3D12 key learned.</summary>
    public static uint DxcAppHash(string exeFileName) => Fnv1a(Path.GetFileName(exeFileName));

    /// <summary>FNV-1a-32 over the string's UTF-16LE bytes.</summary>
    public static uint Fnv1a(string s)
    {
        uint h = 0x811C9DC5;
        foreach (var c in s)
        {
            h = (h ^ (byte)c) * 0x01000193;
            h = (h ^ (byte)(c >> 8)) * 0x01000193;
        }
        return h;
    }

    /// <summary>HINT ONLY: "dxc:" + <see cref="DxcAppHash"/>, the key of an exe without an app profile.</summary>
    public static string DxcKey(string exeFileName) => $"{D3D12Prefix}{DxcAppHash(exeFileName):x8}";

    /// <summary>HINT ONLY: the key of a device created through AGS with this non-empty app name (<see cref="AgsRegistration"/>):
    /// FNV-1a-32 of the name as registered, case-sensitive, whatever the exe is called or the engine name and versions.</summary>
    public static string AppNameKey(string appName) => $"{D3D12Prefix}{Fnv1a(appName):x8}";

    /// <summary>The plan rules' AGS app names (<see cref="Planning.AgsRule"/>, embedded: Phoenix d32786a7, OakGame f2f80824, proven
    /// Townfall), the embedded ones while the rules in use have none. App-name profiles were measured on the exact case only
    /// and win over exe-name profiles: Wonderlands.exe registering "OakGame" gets f2f80824, not its own 85c2b2e5. A proven app
    /// name's game was seen holding the key it gives (SILENT HILL: Townfall, dc72f790): a warm registers it before the game
    /// has been seen, any other only once the game is seen holding its key.</summary>
    static IEnumerable<Planning.AgsRule> AgsRules() =>
        (Planning.RulesFile.Current.Rules.Any(r => r.Ags != null) ? Planning.RulesFile.Current : Planning.RulesFile.Embedded).Rules.Select(r => r.Ags).OfType<Planning.AgsRule>();

    /// <summary>HINT ONLY: the key of an exe that registers <paramref name="appName"/> through AGS: an app-name profile's,
    /// then an exe-name profile's (it wins over an unprofiled app name: ff7rebirth_.exe registering any other name keeps
    /// 6b2fcd83), else <see cref="AppNameKey"/>.</summary>
    public static string AgsKey(string exeFileName, string appName) =>
        AgsRules().Select(r => r.Profiles?.GetValueOrDefault(appName)).OfType<string>().Select(k => $"{D3D12Prefix}{k.ToLowerInvariant()}").FirstOrDefault()
        ?? ProfileKey(exeFileName) ?? AppNameKey(appName);

    public static bool ProvenAgsApp(string appName) => AgsRules().Any(r => r.Proven?.Contains(appName, StringComparer.Ordinal) == true);

    /// <summary>Driver app profiles measured on 32.0.31041.1004: a name part (case-insensitive) and the fixed DxcCache key it
    /// gets. "ff7rebirth" matches as a substring (measured: any name containing it, any case); the others were measured on
    /// their exact exe file names only. Not exhaustive and driver-dependent: a hint, like the name hash.</summary>
    static readonly (string Part, bool Substring, uint App)[] Profiles =
        [("ff7rebirth", true, 0x6b2fcd83), ("cyberpunk2077.exe", false, 0x81de6978), ("eldenring.exe", false, 0xb73b6179), ("wonderlands.exe", false, 0x85c2b2e5)];

    /// <summary>HINT ONLY: the fixed key of a known driver app profile matching this exe file name, else null.</summary>
    public static string? ProfileKey(string exeFileName)
    {
        var name = Path.GetFileName(exeFileName);
        foreach (var (part, substring, app) in Profiles)
            if (substring ? name.Contains(part, StringComparison.OrdinalIgnoreCase) : name.Equals(part, StringComparison.OrdinalIgnoreCase))
                return $"{D3D12Prefix}{app:x8}";
        return null;
    }

    /// <summary>HINT ONLY: the D3D12 key the driver probably gives this exe file name: a known profile's, else the name
    /// hash. For display, for spotting games that would share a key, and for Clear cache while no D3D12 key is learned;
    /// attribution uses learned keys.</summary>
    public static string HintKey(string exeFileName) => ProfileKey(exeFileName) ?? DxcKey(exeFileName);

    /// <summary>Whether the D3D12 keys learned for a game (what its warms or its running process held open) include the name
    /// hash of <paramref name="exeFileName"/>: true = name-hashed (the exact case matters), false = only other keys, an app
    /// profile's (case irrelevant), null = no D3D12 key learned yet (unknown).</summary>
    public static bool? IsNameHashed(IEnumerable<string> learnedKeys, string exeFileName)
    {
        var dxc = learnedKeys.Where(k => k.StartsWith(D3D12Prefix, StringComparison.OrdinalIgnoreCase)).ToList();
        return dxc.Count == 0 ? null : dxc.Contains(DxcKey(exeFileName), StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<FileInfo> FilesOf(IEnumerable<string> keys)
    {
        var set = keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 ? [] : All().Where(f => set.Contains(f.Key)).Select(f => new FileInfo(f.Path)).ToList();
    }

    /// <summary>Bytes of these keys' files. Sizes are current even while the driver has a file mapped (it grows the file;
    /// only the write time stays put).</summary>
    public long SizeOf(IEnumerable<string> keys) => FilesOf(keys).Sum(f => f.Exists ? f.Length : 0);   // the driver trims files at any time

    /// <summary>Keys of the DxcCache and DxCache files a running process with this exe file name has open now: the
    /// authoritative way to learn a game's key (profiles included).</summary>
    public IReadOnlySet<string> KeysOpenBy(string exeFileName) => AppCacheFiles.KeysOpenBy(exeFileName, All());

    public int Delete(IEnumerable<string> keys) => AppCacheFiles.DeleteAll(FilesOf(keys));

    public IReadOnlySet<string> AllKeys() => All().Select(f => f.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>The driver's cap on the whole DxcCache folder, all builds and apps (see ARCHITECTURE.md): fixed, not a setting.</summary>
    public const long DxcCacheCap = 16L << 30;

    /// <summary>Names of the DxcCache (D3D12) files of these keys.</summary>
    public HashSet<string> D3D12FileNames(IEnumerable<string> keys)
    {
        var set = keys.Where(k => k.StartsWith(D3D12Prefix, StringComparison.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return set.Count == 0 || !AppCacheFiles.Readable(DxcDir) ? []
            : Directory.EnumerateFiles(DxcDir, "*.parc").Select(f => Path.GetFileName(f)).Where(n => Key(n, true) is { } k && set.Contains(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Of these DxcCache file names, the ones no longer there: the driver trims least recently used files when the
    /// folder passes <see cref="DxcCacheCap"/>, one file at a time, so a key can lose one of its files.</summary>
    public IReadOnlyList<string> Missing(IEnumerable<string> fileNames) => fileNames.Where(n => !File.Exists(Path.Combine(DxcDir, n))).ToList();

    /// <summary>Bytes of every file in DxcCache: what <see cref="DxcCacheCap"/> is compared with.</summary>
    public long DxcBytes() => AmdBackend.Bytes(DxcDir);

    /// <summary>DxcCache within a tenth of <see cref="DxcCacheCap"/>: an eviction leaves it just under the cap, and later
    /// compiles and launches only add to it, so a missing file there is the driver making room.</summary>
    public bool Full() => DxcBytes() >= DxcCacheCap - DxcCacheCap / 10;

    /// <summary>A warning when the queued games' estimated cache growth is more than the room left under the cap, else null.</summary>
    public static string? QueueWarning(long used, IReadOnlyList<(string Game, long Bytes)> queue, long cap = DxcCacheCap)
    {
        var growing = queue.Where(q => q.Bytes > 0).ToList();
        long planned = growing.Sum(q => q.Bytes), free = Math.Max(0, cap - used);
        if (planned <= free) return null;
        return $"This queue adds about {App.Format.Bytes(planned)} to the shader cache ({string.Join(", ", growing.Select(q => $"{q.Game} {App.Format.Bytes(q.Bytes)}"))}), "
            + $"{App.Format.Bytes(planned - free)} more than the {App.Format.Bytes(free)} free under the AMD driver's fixed {App.Format.Bytes(cap)} limit. "
            + "The driver then trims the least recently used caches, older games' included, and those games stutter until compiled again.";
    }

    IEnumerable<(string Path, string Key)> All()
    {
        foreach (var (dir, d3d12) in new[] { (DxcDir, true), (DxDir, false) })
        {
            if (!AppCacheFiles.Readable(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir, "*.parc"))
                if (Key(Path.GetFileName(f), d3d12) is { } k) yield return (f, k);
        }
    }
}
