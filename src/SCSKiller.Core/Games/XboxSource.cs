using System.Runtime.InteropServices;
using System.Xml.Linq;

namespace SCSKiller.Core.Games;

/// <summary>Installed Xbox app / Game Pass PC games: &lt;library&gt;\&lt;title&gt;\Content, each with its
/// own MicrosoftGame.config (the GDK package manifest) naming the game's real executable(s) and version. No
/// package-manager query is needed to enumerate installs: the manifest sits right there in Content, readable like
/// any other file since the 2022 Xbox app. It's only used, via the native PackageFamilyNameFromId (below), to turn
/// the manifest's Identity name+publisher into the same PackageFamilyName Get-AppxPackage would show, for the id.</summary>
public sealed class XboxSource(IEnumerable<string>? driveRoots = null) : IGameSource
{
    public Store Store => Store.Xbox;

    /// <summary>Games listed before, by id: the exe found below Content for a game whose config and Content folder name none
    /// is reused while the game's version and folder are the same and the exe is there.</summary>
    public IReadOnlyDictionary<string, Game>? Known { get; set; }

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        var roots = driveRoots ?? DriveInfo.GetDrives().Where(d => d.DriveType is DriveType.Fixed or DriveType.Removable).Select(d => d.RootDirectory.FullName);
        foreach (var root in roots.SelectMany(LibraryFolders).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> titleDirs;
            try { titleDirs = Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var titleDir in titleDirs)
            {
                var content = Path.Combine(titleDir, "Content");
                var configPath = Path.Combine(content, "MicrosoftGame.config");
                if (!File.Exists(configPath)) continue;
                try
                {
                    if (ParseGame(content, configPath, Known) is { } game) games.Add(game);
                }
                catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("Xbox", configPath, e); }
            }
        }
        return games;
    }

    /// <summary>The folders the Xbox app installs to on this drive, from its .GamingRoot, and &lt;drive&gt;:\XboxGames.</summary>
    static IEnumerable<string> LibraryFolders(string driveRoot)
    {
        List<string>? listed = null;
        try
        {
            var file = new FileInfo(Path.Combine(driveRoot, ".GamingRoot"));
            if (file.Exists && file.Length <= MaxGamingRoot) listed = ParseGamingRoot(File.ReadAllBytes(file.FullName));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        // GetFullPath with a base resolves a relative entry, a "\"-rooted one and an absolute one alike. XboxGames always:
        // games installed before the install folder was changed stay there.
        return [.. (listed ?? []).Select(p => Path.GetFullPath(p, driveRoot)), Path.Combine(driveRoot, "XboxGames")];
    }

    const int MaxGamingRoot = 64 * 1024;

    /// <summary>.GamingRoot: "RGBX", a uint32 count, then that many null-terminated UTF-16LE paths, relative to the drive
    /// root. Null when the bytes aren't that.</summary>
    internal static List<string>? ParseGamingRoot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8 || bytes.Length > MaxGamingRoot || !bytes[..4].SequenceEqual("RGBX"u8)) return null;
        var count = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes[4..]);
        if (count is 0 or > 64) return null;
        var chars = MemoryMarshal.Cast<byte, char>(bytes[8..][..((bytes.Length - 8) & ~1)]);
        var paths = new List<string>();
        while (paths.Count < count)
        {
            var end = chars.IndexOf('\0');
            if (end <= 0) return null;
            paths.Add(new string(chars[..end]));
            chars = chars[(end + 1)..];
        }
        return paths;
    }

    static Game? ParseGame(string content, string configPath, IReadOnlyDictionary<string, Game>? known = null)
    {
        var doc = XDocument.Load(configPath);
        var ns = doc.Root!.GetDefaultNamespace();
        var identity = doc.Root.Element(ns + "Identity");
        var name = identity?.Attribute("Name")?.Value;
        var publisher = identity?.Attribute("Publisher")?.Value;
        var version = identity?.Attribute("Version")?.Value;
        if (name == null) return null;
        // An add-on (DLC, the MCC per-game content packs, Game Pass trackers) names the products it belongs to.
        if (doc.Root.Element(ns + "AllowedProducts") != null) return null;
        var display = doc.Root.Element(ns + "ShellVisuals")?.Attribute("DefaultDisplayName")?.Value ?? name;

        var familyName = publisher != null ? PackageFamilyName(name, publisher) : null;
        var exeRel = doc.Root.Element(ns + "ExecutableList")?.Elements(ns + "Executable")
            .Where(IsGameExecutable)
            .Select(e => e.Attribute("Name")?.Value)
            .FirstOrDefault(n => n != null)
            ?? RootExe(content)
            ?? (known?.GetValueOrDefault($"xbox:{familyName ?? name}") is { Version: { } was } k && was == version && k.InstallDir.Equals(content, StringComparison.OrdinalIgnoreCase)
                && File.Exists(k.ExePath) && Pickable(k.ExePath) ? k.ExePath : FindExeFallback(content));
        if (exeRel == null) return null;
        var exe = Path.GetFullPath(Path.Combine(content, exeRel));

        // e.g. Minecraft Launcher. Name-based because nothing else says it: neither MicrosoftGame.config nor
        // appxmanifest.xml has an app type or category (checked on 37 installed titles: the launcher's elements and
        // attributes are the same set the games use), and the GamingServices registry only mirrors the config.
        if (display.EndsWith(" Launcher", StringComparison.OrdinalIgnoreCase)) return null;
        return new Game($"xbox:{familyName ?? name}", display, Store.Xbox, content, exe, version);
    }

    // Names that mark an ExecutableList entry (or a fallback-scan candidate) as a launcher/utility rather than the
    // game itself. "codShip"-style IW bootstrap.exe is the one real exception this misses: it's the only entry in
    // its config, so it's picked anyway (nothing else to prefer over it).
    static readonly string[] HelperHints =
        ["launch", "bootstrap", "helper", "crash", "webview", "uploader", "gpudetection", "hashdatabase", "setup", "redist", "unins", "reporter", "cache_builder"];

    /// <summary>The exes an Xbox app game's MicrosoftGame.config lists as not the game (dev-only, another device family, a
    /// helper), full paths; none without a config that can be read.</summary>
    internal static HashSet<string> NotTheGame(string content)
    {
        var config = Path.Combine(content, "MicrosoftGame.config");
        try
        {
            if (!File.Exists(config)) return [];
            var doc = XDocument.Load(config);
            var ns = doc.Root!.GetDefaultNamespace();
            return (doc.Root.Element(ns + "ExecutableList")?.Elements(ns + "Executable") ?? []).Where(e => !IsGameExecutable(e))
                .Select(e => e.Attribute("Name")?.Value).OfType<string>().Select(n => Path.GetFullPath(GameFiles.Under(content, n)))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException or ArgumentException or NotSupportedException) { return []; }
    }

    static bool IsGameExecutable(XElement e)
    {
        var n = e.Attribute("Name")?.Value;
        if (n == null || LauncherOrUtility(n)) return false;
        var family = e.Attribute("TargetDeviceFamily")?.Value;
        if (family != null && !family.Contains("PC", StringComparison.OrdinalIgnoreCase)) return false;
        return !string.Equals(e.Attribute("IsDevOnly")?.Value, "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>No config entry survived the helper filter: the largest non-helper exe in Content itself, as the game it is
    /// (an Unreal stub's Shipping exe, an EasyAntiCheat bootstrap's target). DOOM Eternal's config names only its launcher,
    /// and its mod sandbox in a subfolder is larger than the game in the root.</summary>
    static string? RootExe(string content)
    {
        var excluded = NotTheGame(content);
        var exe = Directory.EnumerateFiles(content, "*.exe", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = 0 })
            .Where(f => Pickable(f) && !excluded.Contains(Path.GetFullPath(f)))
            .Select(f => new FileInfo(f)).MaxBy(f => f.Length)?.FullName;
        return exe == null ? null : GameFiles.GameExe(content, exe);
    }

    /// <summary>Nothing in Content itself either (e.g. Darktide's config lists only its launcher): the largest non-helper
    /// exe under Content.</summary>
    static string? FindExeFallback(string content)
    {
        var opts = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true, AttributesToSkip = 0 };
        return Directory.EnumerateFiles(content, "*.exe", opts)
            .Where(Pickable)
            .Select(f => new FileInfo(f)).MaxBy(f => f.Length)?.FullName;
    }

    static bool Pickable(string exe) => !LauncherOrUtility(Path.GetFileName(exe)) && !GameFiles.IsHelper(exe);

    /// <summary>A launcher's or utility's exe by its name (<see cref="HelperHints"/>).</summary>
    internal static bool LauncherOrUtility(string name) => HelperHints.Any(h => name.Contains(h, StringComparison.OrdinalIgnoreCase));

    /// <summary>"&lt;package family&gt;!&lt;app id&gt;" of an Xbox game (its Content\appxmanifest.xml), null for any other game.</summary>
    public static string? AppUserModelId(Game g)
    {
        if (g.Store != Store.Xbox || !g.Id.StartsWith("xbox:", StringComparison.Ordinal)) return null;
        try
        {
            var doc = XDocument.Load(Path.Combine(g.InstallDir, "appxmanifest.xml"));
            var app = doc.Root?.Element(doc.Root.GetDefaultNamespace() + "Applications")?.Elements().FirstOrDefault()?.Attribute("Id")?.Value;
            return app == null ? null : $"{g.Id["xbox:".Length..]}!{app}";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return null; }
    }

    /// <summary>The path an Xbox game's process runs from: its exe under the package's install folder
    /// (<c>C:\Program Files\WindowsApps\&lt;full name&gt;</c>), not under Content. Null when no installed package holds it.</summary>
    public static string? LaunchPath(Game g)
    {
        if (g.Store != Store.Xbox || !g.Id.StartsWith("xbox:", StringComparison.Ordinal)) return null;
        var family = g.Id["xbox:".Length..];
        var rel = Path.GetRelativePath(g.InstallDir, g.ExePath);
        uint count = 0, len = 0;
        GetPackagesByPackageFamily(family, ref count, null, ref len, 0);
        if (count == 0 || len == 0) return null;
        var names = new nint[count];
        var buf = Marshal.AllocHGlobal((int)len * sizeof(char));
        try
        {
            if (GetPackagesByPackageFamily(family, ref count, names, ref len, buf) != 0) return null;
            foreach (var full in names.Take((int)count).Select(p => Marshal.PtrToStringUni(p)))
            {
                uint plen = 0;
                GetPackagePathByFullName(full!, ref plen, null);
                if (plen == 0) continue;
                var path = new char[plen];
                if (GetPackagePathByFullName(full!, ref plen, path) == 0 && Path.Combine(new string(path, 0, (int)plen - 1), rel) is var exe && File.Exists(exe))
                    return exe;
            }
            return null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagesByPackageFamily(string packageFamilyName, ref uint count, [Out] nint[]? packageFullNames, ref uint bufferLength, nint buffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetPackagePathByFullName(string packageFullName, ref uint pathLength, [Out] char[]? path);

    // PackageFamilyNameFromId (appmodel.h)
    [StructLayout(LayoutKind.Sequential)]
    struct PACKAGE_VERSION { public ulong Version; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct PACKAGE_ID
    {
        public uint Reserved, ProcessorArchitecture;
        public PACKAGE_VERSION Version;
        public string? Name, Publisher, ResourceId, PublisherId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int PackageFamilyNameFromId(ref PACKAGE_ID packageId, ref uint packageFamilyNameLength, char[]? packageFamilyName);

    public static string? PackageFamilyName(string name, string publisher)
    {
        var id = new PACKAGE_ID { Name = name, Publisher = publisher };
        uint len = 0;
        PackageFamilyNameFromId(ref id, ref len, null);
        if (len == 0) return null;
        var buf = new char[len];
        return PackageFamilyNameFromId(ref id, ref len, buf) == 0 ? new string(buf, 0, (int)len - 1) : null;
    }
}
