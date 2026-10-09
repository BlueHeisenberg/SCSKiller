using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Games;

/// <summary>What a ReShade add-on does to the game's pipelines.</summary>
public enum AddonKind
{
    NotPipeline,       // leaves them alone (renodx-dlss5, dlssfix, an effect add-on)
    ReplacesShaders,   // replaces some shaders: only their pipelines change
    LayoutInjecting,   // adds a constant to every root signature the game creates: every pipeline changes
}

/// <param name="Mod">"RenoDX" or "Luma"; the file's name for one that couldn't be read; null = another add-on</param>
/// <param name="Disabled">listed in ReShade.ini's DisabledAddons: never loaded</param>
public sealed record ReShadeAddon(string Path, string? Mod, AddonKind Kind, bool Disabled = false);

/// <summary>Why a copy of ReShade's layer wouldn't reproduce what the game loads (<see cref="ReShadeInstall.Block"/>).</summary>
public enum LayerBlock
{
    None,
    Luma,               // a Luma add-on, whose shader files a copy leaves out
    NotBesideExe,       // only in the install root above the exe: the game may not load it
    OptiScalerOff,      // ReShade64.dll beside an OptiScaler whose OptiScaler.ini doesn't set LoadReshade=true
    OptiScalerIni,      // ...that does, in an OptiScaler.ini with a section header not whole on its line: SimpleIni may read
                        // it across lines, so a copy can't be sure its paths and update check are out
    UnloadedName,       // an .asi, ReShade64.dll or a renamed file another loader may or may not pick up
    SpecialK,           // Special K under a name the game loads: it loads ReShade as a plug-in, from its own folders or this one
}

/// <summary>ReShade next to the game: its DLL (whatever its name), ReShade.ini and ReShade.log when present, and the
/// add-ons in the folder it loads them from (its own, or ReShade.ini's AddonPath).</summary>
/// <param name="LoadsAddons">the build with full add-on support; the standard one loads no add-on files</param>
/// <param name="BesideExe">where the game loads it: the exe's own folder, or an Xbox app game's package root; else in the
/// install root above the exe</param>
/// <param name="OptiScalerLoads">ReShade64.dll beside an OptiScaler the game loads: true when its OptiScaler.ini sets
/// LoadReshade=true (OptiScaler then loads it from the exe's folder); null = no OptiScaler there</param>
/// <param name="OptiScaler">that OptiScaler's DLL; null = none</param>
/// <param name="OptiScalerIniOdd">its OptiScaler.ini has a section header that isn't a whole [name] on its line</param>
/// <param name="SpecialK">Special K beside it under a name the game loads; <paramref name="Dll"/> is Special K's when no ReShade is there</param>
public sealed record ReShadeInstall(string Dll, bool LoadsAddons, string? Ini, string? Log, IReadOnlyList<ReShadeAddon> Addons, bool BesideExe = true,
    bool? OptiScalerLoads = null, string? OptiScaler = null, bool OptiScalerIniOdd = false, bool SpecialK = false)
{
    /// <summary>The OptiScaler that loads ReShade64.dll: a copy of the layer runs through it too, as the game does.</summary>
    public string? Loader => OptiScalerLoads == true ? OptiScaler : null;

    /// <summary>The loaded add-on that changes the most of the game's pipelines; null = none changes any.</summary>
    public ReShadeAddon? ShaderMod => LoadsAddons ? Addons.Where(a => a is { Disabled: false, Kind: not AddonKind.NotPipeline }).MaxBy(a => a.Kind) : null;

    // the names the game itself loads ReShade under from its exe's folder or package root (system DLLs it imports, or the recorder's chain)
    internal static readonly string[] Loaded = ["dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "opengl32.dll", "dinput8.dll", ScsKiller.ChainName];

    /// <summary>What keeps a copy of the layer next to the warm's exe from reproducing what the game loads; None = ReShade
    /// beside the exe under a name the game loads by itself, or ReShade64.dll that OptiScaler loads.</summary>
    public LayerBlock Block =>
        Addons.Any(a => a is { Disabled: false, Mod: "Luma" }) ? LayerBlock.Luma
        : !BesideExe ? LayerBlock.NotBesideExe
        : SpecialK ? LayerBlock.SpecialK
        : OptiScalerLoads == true ? OptiScalerIniOdd ? LayerBlock.OptiScalerIni : LayerBlock.None
        : Loaded.Contains(Path.GetFileName(Dll), StringComparer.OrdinalIgnoreCase) ? LayerBlock.None
        : OptiScalerLoads == false ? LayerBlock.OptiScalerOff
        : LayerBlock.UnloadedName;

    public bool Copyable => Block == LayerBlock.None;

    /// <summary>A shader mod whose layer a copy reproduces (<see cref="Copyable"/>): compiles run through it.</summary>
    public bool Layered => ShaderMod != null && Copyable;

    /// <summary>An add-on that changes every pipeline in a layer a copy can't reproduce: a compile without it matches
    /// nothing if the game loads it, and one through it nothing if the game doesn't.</summary>
    public bool Blocks => !Copyable && ShaderMod is { Kind: AddonKind.LayoutInjecting };

    /// <summary>ReShade under the recorder's own name (d3d12.dll, or the name a chained mod gets): the recorder records
    /// under it only when the user chains it ("Record alongside").</summary>
    public bool AsD3D12 => Path.GetFileName(Dll) is var n && (n.Equals("d3d12.dll", StringComparison.OrdinalIgnoreCase) || n.Equals(ScsKiller.ChainName, StringComparison.OrdinalIgnoreCase));

    /// <summary>What a warm through this layer depends on: ReShade's DLL, the add-ons that change pipelines and the
    /// <see cref="Loader"/> with its OptiScaler.ini (its sampler overrides change root signatures), each by name, size and
    /// write time (not ReShade.ini: ReShade rewrites it as it runs); null = nothing to copy (not <see cref="Layered"/>).</summary>
    public string? Fingerprint => !Layered ? null : string.Join('|', new[] { Dll }.Concat(Addons.Where(a => a is { Disabled: false, Kind: not AddonKind.NotPipeline }).Select(a => a.Path))
        .Concat(Loader is { } o ? [o, Path.Combine(Path.GetDirectoryName(o)!, "OptiScaler.ini")] : [])
        .Select(p => new FileInfo(p)).Select(f => $"{f.Name}:{f.Length}:{f.LastWriteTimeUtc.Ticks}"));
}

public static class ReShade
{
    static readonly EnumerationOptions Flat = new() { IgnoreInaccessible = true };

    // Raw bytes for a DLL without a readable version resource: its FileDescription. The export add-ons register through is
    // read from the export table: Special K and add-on hosts hold the name too, to look it up.
    static readonly byte[][] Identity = [Encoding.Unicode.GetBytes("ReShade post-processing injector")];
    // only in the standard build, which loads no add-on files
    static readonly byte[][] Limited = ["only limited add-on functionality"u8.ToArray()];
    // the names ReShade loads under next to the exe, read whole when the version resource doesn't name it
    static readonly string[] Names = ["dxgi.dll", "d3d12.dll", "d3d11.dll", "d3d10.dll", "d3d9.dll", "opengl32.dll", "dinput8.dll", "ReShade64.dll", ScsKiller.ChainName];
    // the names OptiScaler works under as a DLL the game loads by itself (OptiScaler.asi and OptiScaler.dll need another
    // loader): every D3D12 game loads the first two; the rest only when the exe imports them
    static readonly string[] OptiScalerNames = ["dxgi.dll", "d3d12.dll", "winmm.dll", "version.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll"];

    // Logged unconditionally where release builds register their pipeline hooks: RenoDX's shader-replacement util (every
    // HDR add-on and the devkit, in both RenoDX repos; not dlssfix, fpslimiter or DLSS-only add-ons) and Luma's core.
    static readonly (string Mod, byte[] Marker)[] Mods =
        [("RenoDX", "utils::shader attached."u8.ToArray()), ("Luma", "Luma: trying to load a config from a newer version of the mod"u8.ToArray())];
    static readonly byte[][] ModMarkers = [.. Mods.Select(m => m.Marker)];

    // ReShade.log: which file each add-on name was registered from, then RenoDX (prefixed with that name) adding its
    // constants to a game root signature or cloning the layout instead. Both paths are in every build and a runtime flag
    // picks one, so the binary can't tell them apart.
    static readonly Regex Loading = new(@"Loading add-on from '(.+)' \.\.\.$"), Registered = new(@"Registered add-on ""(.+?)""");
    static readonly Regex Pipeline = new(@"\| \[(.+?)\] mods::shader::(OnCreatePipelineLayout\(will insert|OnInitPipelineLayout\(Cloning)");

    /// <summary>A file larger than this is never read (the largest add-on seen is 65 MB, its strings at the end).</summary>
    const long MaxBytes = 128L << 20;
    const int NotRead = -2;
    static readonly ConcurrentDictionary<string, (long Length, DateTime Written, string Identity, object? Value)> Probed = new(StringComparer.OrdinalIgnoreCase);
    static long probesVersion;
    static readonly Dictionary<string, long> probesSaved = new(StringComparer.OrdinalIgnoreCase);   // file -> the version it holds
    static readonly Lock probeGate = new();

    sealed record KeptProbe(long Length, DateTime Written, string Identity, bool? Flag, int? Number, string? Text);

    /// <summary>Adds the probes <see cref="SaveProbes"/> wrote to <paramref name="file"/>: a start then reads no DLL's version
    /// resource or bytes again while the file stays the same. One that can't be read adds none.</summary>
    public static void LoadProbes(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, KeptProbe>>(File.ReadAllBytes(file)) is { } kept)
                foreach (var (key, p) in kept)
                    if (p is { Identity.Length: > 1 }) Probed.TryAdd(key, (p.Length, p.Written, p.Identity, (object?)p.Flag ?? (object?)p.Number ?? p.Text));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
    }

    /// <summary>Writes the probes that are a flag, a number or a text to <paramref name="file"/> when one was made since it
    /// was last written.</summary>
    public static void SaveProbes(string file)
    {
        lock (probeGate)
        {
            var version = Interlocked.Read(ref probesVersion);
            if (probesSaved.GetValueOrDefault(file) == version) return;
            var kept = Probed.Where(p => p.Value.Value is bool or int or string).ToDictionary(p => p.Key,
                p => new KeptProbe(p.Value.Length, p.Value.Written, p.Value.Identity, p.Value.Value as bool?, p.Value.Value as int?, p.Value.Value as string));
            AppStore.WriteAtomic(file, JsonSerializer.SerializeToUtf8Bytes(kept));
            probesSaved[file] = version;
        }
    }

    /// <summary>Every probe forgotten, the saved ones too: each file is read again (a refresh the user asked for).</summary>
    public static void ForgetProbes() => Probed.Clear();

    /// <summary>RenoDX's build folders by what their add-on does (shader-mods.json).</summary>
    public static IReadOnlyDictionary<string, AddonKind> RenoDxTable { get => field ??= ParseTable(ContentFile.Embedded("SCSKiller.Core.Games.shader-mods.json")); }

    static Dictionary<string, AddonKind> ParseTable(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var table = new Dictionary<string, AddonKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, kind) in new[] { ("layout_injecting", AddonKind.LayoutInjecting), ("replaces_shaders", AddonKind.ReplacesShaders) })
            foreach (var e in doc.RootElement.GetProperty(key).EnumerateArray()) table[e.GetString()!] = kind;
        return table;
    }

    /// <summary>The RenoDX build folder an add-on file name ("renodx-ff7rebirth.addon64") names; null = not RenoDX's.</summary>
    public static string? RenoDxFolder(string fileName) =>
        fileName.StartsWith("renodx-", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(fileName)["renodx-".Length..] : null;

    /// <summary>ReShade where the game loads it: an Xbox app game's package root (<see cref="PackageRoot"/>) under a name
    /// the game loads, else the exe's folder; else the install root under any name. Known by its version resource (any
    /// DLL there) or, for one of its usual names, its own bytes; null = none. Its add-ons are the ones in ReShade.ini's
    /// AddonPath, else its folder; DisabledAddons are marked. A RenoDX add-on is LayoutInjecting or ReplacesShaders by ReShade.log's lines for
    /// its file, when the log was written after the file; else by its build folder (<see cref="RenoDxTable"/>, from the file
    /// name or the version resource's OriginalFilename); else ReplacesShaders, as is an add-on that couldn't be read: not
    /// known to change every pipeline. Each file is read once while its size and write time stay the same.</summary>
    public static ReShadeInstall? Detect(Game game)
    {
        var exeDir = GameFiles.DirKey(Path.GetDirectoryName(game.ExePath)!);
        var root = PackageRoot(game);
        // a packaged app's DLL search looks in its package root before the exe's folder (Microsoft's "Dynamic-link library
        // search order"), for a name the game loads by itself
        if (root != null && At(root, game, exeDir, root) is { } packaged && ReShadeInstall.Loaded.Contains(Path.GetFileName(packaged.Dll), StringComparer.OrdinalIgnoreCase))
            return packaged;
        foreach (var dir in new[] { exeDir, GameFiles.DirKey(game.InstallDir) }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (At(dir, game, exeDir, root) is { } r) return r;
        return null;
    }

    /// <summary>An Xbox app game's package root: its install folder (Content, with MicrosoftGame.config), searched for
    /// DLLs before the exe's folder; null = another store's game.</summary>
    public static string? PackageRoot(Game game) => game.Store == Store.Xbox ? GameFiles.DirKey(game.InstallDir) : null;

    static ReShadeInstall? At(string dir, Game game, string exeDir, string? root)
    {
        var files = List(dir);
        var besideExe = dir.Equals(exeDir, StringComparison.OrdinalIgnoreCase);
        var dll = files?.FirstOrDefault(f => (Ext(f, ".dll") || Ext(f, ".asi")) && IsReShade(f));
        var sk = besideExe || dir.Equals(root, StringComparison.OrdinalIgnoreCase)
            ? files?.FirstOrDefault(f => f != dll && Array.FindIndex(ReShadeInstall.Loaded, n => n.Equals(f.Name, StringComparison.OrdinalIgnoreCase)) is var i and >= 0
                && (i < 2 || Imports(game.ExePath).Contains(f.Name, StringComparer.OrdinalIgnoreCase)) && IsSpecialK(f)) : null;
        // every D3D12 game loads Loaded's first two names; Special K loads the add-ons in the exe's folder itself, whatever
        // its own folder and ReShade's AddonPath, and ReShade from there or its own folders
        var hostAddons = sk == null ? [] : (besideExe ? files! : List(exeDir) ?? []).Where(f => Ext(f, ".addon") || Ext(f, ".addon64")).ToList();
        dll ??= hostAddons.Count > 0 ? sk : null;
        if (dll == null) return null;
        string? Named(string name) => files!.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.FullName;
        // ReShade reads its ini and resolves AddonPath in its own folder
        var (ini, log) = (Named("ReShade.ini"), Named("ReShade.log"));
        var (addonPath, disabled) = ini != null ? Config(new FileInfo(ini)) : (null, []);
        var addonDir = addonPath != null ? GameFiles.DirKey(Path.Combine(dir, addonPath)) : dir;
        var verdicts = log != null ? LogVerdicts(new FileInfo(log)) : null;
        var addons = (addonDir == dir ? files! : List(addonDir) ?? []).Where(f => Ext(f, ".addon") || Ext(f, ".addon64")).Concat(addonDir == exeDir ? [] : hostAddons)
            .Select(f =>
            {
                var a = Classify(f, verdicts, log);
                return a with { Disabled = disabled.Any(d => Disables(d, a, f)) };
            }).ToList();
        // OptiScaler loads ReShade from the exe's folder under this one name only
        var (loads, opti, odd) = besideExe && dll.Name.Equals("ReShade64.dll", StringComparison.OrdinalIgnoreCase) ? OptiScalerLoads(files!, game.ExePath) : (null, null, false);
        return new(dll.FullName, Find(dll, "limited", Limited) != 0, ini, log, addons, besideExe || dir.Equals(root, StringComparison.OrdinalIgnoreCase), loads, opti?.FullName, odd, sk != null);
    }

    /// <summary>Whether an OptiScaler the game loads loads ReShade64.dll: its OptiScaler.ini, next to it, sets [Plugins]
    /// LoadReshade=true (absent or "auto" is false), its DLL, and whether the ini has a header that isn't a whole [name] on
    /// its line; null = no OptiScaler the game loads.</summary>
    static (bool? Loads, FileInfo? Dll, bool Odd) OptiScalerLoads(List<FileInfo> files, string exe)
    {
        var opti = files.FirstOrDefault(f => Array.FindIndex(OptiScalerNames, n => n.Equals(f.Name, StringComparison.OrdinalIgnoreCase)) is var i and >= 0
            && (i < 2 || Imports(exe).Contains(f.Name, StringComparer.OrdinalIgnoreCase)) && IsOptiScaler(f));
        if (opti == null) return (null, null, false);
        var ini = files.FirstOrDefault(f => f.Name.Equals("OptiScaler.ini", StringComparison.OrdinalIgnoreCase));
        return (ini != null && OptiScalerValue(ini, "Plugins", "LoadReshade")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true, opti,
            ini != null && IniAll(ini).Any(e => !e.Closed));
    }

    /// <summary>A key's value as OptiScaler reads it (the last one, under a header SimpleIni names); null = absent or "auto".</summary>
    static string? OptiScalerValue(FileInfo ini, string section, string key) =>
        IniEntries(ini).LastOrDefault(e => e is { Closed: true } && e.Section.Equals(section, StringComparison.OrdinalIgnoreCase)
            && e.Key!.Equals(key, StringComparison.OrdinalIgnoreCase)).Value is { Length: > 0 } v && !v.Equals("auto", StringComparison.OrdinalIgnoreCase) ? v : null;

    // what OptiScaler loads as it starts, from its OptiDllPath folder (default the exe folder's OptiScaler\), else the
    // exe's folder: the FidelityFX, XeSS and NGX libraries its proxies look for (nvapi64.dll only from System32)
    static readonly string[] OptiScalerLibraries = ["amd_fidelityfx_loader_dx12.dll", "amd_fidelityfx_dx12.dll", "amd_fidelityfx_upscaler_dx12.dll",
        "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_denoiser_dx12.dll", "amd_fidelityfx_radiancecache_dx12.dll",
        "libxess.dll", "libxess_dx11.dll", "libxess_fg.dll", "libxell.dll", "_nvngx.dll", "nvngx.dll"];

    /// <summary>The <see cref="OptiScalerLibraries"/> OptiScaler finds beside <paramref name="opti"/>, as it looks for them.
    /// OptiDllPath counts only as a relative path that stays inside the exe's folder; else the default OptiScaler\.</summary>
    static IEnumerable<string> LibrariesOf(string opti)
    {
        var exeDir = Path.GetFullPath(Path.GetDirectoryName(opti)!);
        var main = Inside(exeDir, OptiScalerValue(new FileInfo(Path.Combine(exeDir, "OptiScaler.ini")), "Libraries", "OptiDllPath")) ?? Path.Combine(exeDir, "OptiScaler");
        if (!Directory.Exists(main)) main = exeDir;
        return OptiScalerLibraries.Select(n => new[] { Path.Combine(main, n), Path.Combine(exeDir, n) }.FirstOrDefault(File.Exists)).OfType<string>();
    }

    /// <summary><paramref name="relative"/> under <paramref name="dir"/>, canonical; null = rooted (a drive, UNC), escaping
    /// it, or not a path.</summary>
    static string? Inside(string dir, string? relative)
    {
        if (relative == null || Path.IsPathRooted(relative)) return null;
        try
        {
            var full = Path.GetFullPath(Path.Combine(dir, relative));
            return full.StartsWith(Path.TrimEndingDirectorySeparator(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    // a library is staged only as a PE file within both caps (real ones are tens of MB); one left out, OptiScaler looks
    // elsewhere as it would without it
    const long MaxLibraryBytes = 128L << 20, LibraryBudget = 512L << 20;

    static bool IsPe(string path, long length)
    {
        if (length < 0x40) return false;
        using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> head = stackalloc byte[0x40];
        f.ReadExactly(head);
        if (head[0] != 'M' || head[1] != 'Z') return false;
        var at = BitConverter.ToInt32(head[0x3C..]);
        if (at < 0x40 || at > length - 4) return false;
        Span<byte> pe = stackalloc byte[4];
        f.Position = at;
        f.ReadExactly(pe);
        return pe.SequenceEqual("PE\0\0"u8);
    }

    /// <summary>OptiScaler.ini for a warm: no key that points OptiScaler at another folder or file ([Libraries], [Plugins]
    /// Path, [Log] LogFileName), so it loads and writes only in the stage, and no update check (its only network access):
    /// CheckForUpdate=false under the first [Hotfix], or in a [Hotfix] added at the end.</summary>
    static List<string> ForStage(IEnumerable<string> ini)
    {
        var kept = IniLines(ini).Where(e => e.Key == null || !(e.Section.Equals("Libraries", StringComparison.OrdinalIgnoreCase)
            || e.Section.Equals("Plugins", StringComparison.OrdinalIgnoreCase) && e.Key.Equals("Path", StringComparison.OrdinalIgnoreCase)
            || e.Section.Equals("Log", StringComparison.OrdinalIgnoreCase) && e.Key.Equals("LogFileName", StringComparison.OrdinalIgnoreCase)
            || e.Section.Equals("Hotfix", StringComparison.OrdinalIgnoreCase) && e.Key.Equals("CheckForUpdate", StringComparison.OrdinalIgnoreCase))).ToList();
        var lines = kept.Select(e => e.Line).ToList();
        var hotfix = kept.FindIndex(e => e.Key == null && e.Section.Equals("Hotfix", StringComparison.OrdinalIgnoreCase) && e.Line.TrimStart().StartsWith('['));
        if (hotfix < 0) lines.AddRange(["[Hotfix]", "CheckForUpdate=false"]);
        else lines.Insert(hotfix + 1, "CheckForUpdate=false");
        return lines;
    }

    static IReadOnlyList<string> Imports(string exe) => Cached(new FileInfo(exe), "imports", () =>
    {
        try { return CarvedReader.PeImports(exe, out _); }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException) { return new List<string>(); }
    }) as IReadOnlyList<string> ?? [];

    static bool IsOptiScaler(FileInfo f) => Cached(f, "optiscaler", () => new[] { Middleware.ExportName(f.FullName), FileVersionInfo.GetVersionInfo(f.FullName).ProductName }
        .Any(n => n?.StartsWith("OptiScaler", StringComparison.OrdinalIgnoreCase) == true)) is true;

    static bool IsSpecialK(FileInfo f) => Cached(f, "specialk", () => new[] { Middleware.ExportName(f.FullName), FileVersionInfo.GetVersionInfo(f.FullName).ProductName }
        .Any(n => n != null && (n.StartsWith("SpecialK", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Special K", StringComparison.OrdinalIgnoreCase)))) is true;

    static bool ExportsAddonApi(FileInfo f) => Cached(f, "addonexport", () =>
    {
        try
        {
            using var pe = PeFile.Open(f.FullName);
            return PeFile.ExportNames(pe).Contains("ReShadeRegisterAddon");
        }
        catch (Exception e) when (e is BadImageFormatException or InvalidOperationException) { return false; }
    }) is true;

    static List<FileInfo>? List(string dir)
    {
        try { return new DirectoryInfo(dir).EnumerateFiles("*", Flat).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>A copy of the layer for a warm (scskiller_warm --layer) in <paramref name="dest"/>, emptied first: as dxgi.dll
    /// (the warm's exe loads that name, whatever the game's is) ReShade's DLL, or the <see cref="ReShadeInstall.Loader"/>
    /// with ReShade64.dll, its OptiScaler.ini (<see cref="ForStage"/>) and the libraries it loads; the add-ons that change
    /// pipelines, and ReShade.ini without the keys that point ReShade at another folder ([ADDON] AddonPath, [INSTALL] BasePath).</summary>
    public static string Stage(ReShadeInstall r, string dest, long maxFile = MaxLibraryBytes, long budget = LibraryBudget)
    {
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.CreateDirectory(dest);
        File.Copy(r.Loader ?? r.Dll, Path.Combine(dest, "dxgi.dll"));
        if (r.Loader is { } opti)
        {
            File.Copy(r.Dll, Path.Combine(dest, "ReShade64.dll"));
            File.WriteAllLines(Path.Combine(dest, "OptiScaler.ini"), ForStage(File.ReadAllLines(Path.Combine(Path.GetDirectoryName(opti)!, "OptiScaler.ini"))));
            // ponytail: copied per warm process (FidelityFX and XeSS are tens of MB each); hard links if staging gets slow
            foreach (var lib in LibrariesOf(opti))
                if (new FileInfo(lib).Length is var n && n <= maxFile && n <= budget && IsPe(lib, n))
                {
                    File.Copy(lib, Path.Combine(dest, Path.GetFileName(lib)));
                    budget -= n;
                }
        }
        foreach (var a in r.Addons.Where(a => a is { Disabled: false, Kind: not AddonKind.NotPipeline })) File.Copy(a.Path, Path.Combine(dest, Path.GetFileName(a.Path)));
        if (r.Ini != null) File.WriteAllLines(Path.Combine(dest, "ReShade.ini"), WithoutPaths(File.ReadAllLines(r.Ini)));
        return dest;
    }

    static IEnumerable<string> WithoutPaths(IEnumerable<string> ini) => IniLines(ini)
        .Where(e => !(e.Section.Equals("ADDON", StringComparison.OrdinalIgnoreCase) && e.Key?.Equals("AddonPath", StringComparison.OrdinalIgnoreCase) == true
            || e.Section.Equals("INSTALL", StringComparison.OrdinalIgnoreCase) && e.Key?.Equals("BasePath", StringComparison.OrdinalIgnoreCase) == true))
        .Select(e => e.Line);

    static bool Ext(FileInfo f, string ext) => f.Extension.Equals(ext, StringComparison.OrdinalIgnoreCase);

    static bool IsReShade(FileInfo f) =>
        Cached(f, "product", () => FileVersionInfo.GetVersionInfo(f.FullName).ProductName == "ReShade") is true
        || Names.Contains(f.Name, StringComparer.OrdinalIgnoreCase) && (Find(f, "description", Identity) >= 0 || ExportsAddonApi(f));

    static ReShadeAddon Classify(FileInfo f, IReadOnlyDictionary<string, AddonKind>? verdicts, string? log)
    {
        var mod = Find(f, "mods", ModMarkers);
        if (mod == -1) return new(f.FullName, null, AddonKind.NotPipeline);
        if (mod >= 0 && Mods[mod].Mod != "RenoDX") return new(f.FullName, Mods[mod].Mod, AddonKind.ReplacesShaders);
        // a log from before the file was replaced says nothing about it
        var logged = verdicts != null && verdicts.TryGetValue(f.FullName, out var k) && File.GetLastWriteTimeUtc(log!) > f.LastWriteTimeUtc ? k : (AddonKind?)null;
        var kind = logged ?? Listed(f.Name) ?? Listed(OriginalName(f.FullName));
        return new(f.FullName, mod >= 0 || kind != null ? "RenoDX" : Path.GetFileNameWithoutExtension(f.Name), kind ?? AddonKind.ReplacesShaders);
    }

    /// <summary>A DisabledAddons entry as ReShade reads it: "name" for the add-on registered under that name, "@file" or
    /// "name@file" for its file (the name isn't checked before loading). The name is RenoDX's for its builds, else the
    /// version resource's ProductName or the file's stem, as ReShade takes it unless the add-on exports another.</summary>
    static bool Disables(string entry, ReShadeAddon a, FileInfo f) =>
        entry.IndexOf('@') is var at and >= 0 ? entry[(at + 1)..] == f.Name
        : entry == (a.Mod == "RenoDX" ? "RenoDX" : Cached(f, "productname", () => FileVersionInfo.GetVersionInfo(f.FullName).ProductName ?? "") as string is { Length: > 0 } product
            ? product : Path.GetFileNameWithoutExtension(f.Name));

    static AddonKind? Listed(string fileName) => RenoDxFolder(fileName) is { } folder && RenoDxTable.TryGetValue(folder, out var k) ? k : null;

    static string OriginalName(string path) => Cached(new FileInfo(path), "original", () => FileVersionInfo.GetVersionInfo(path).OriginalFilename ?? "") as string ?? "";

    /// <summary>ReShade.ini's [ADDON] AddonPath (relative to ReShade's folder) and DisabledAddons' entries.</summary>
    static (string? AddonPath, List<string> Disabled) Config(FileInfo ini)
    {
        string? addonPath = null;
        var disabled = new List<string>();
        foreach (var (section, _, key, value, _) in IniEntries(ini))
        {
            if (!section.Equals("ADDON", StringComparison.OrdinalIgnoreCase)) continue;
            if (key.Equals("AddonPath", StringComparison.OrdinalIgnoreCase) && value.Trim('"') is { Length: > 0 } p) addonPath = p;
            else if (key.Equals("DisabledAddons", StringComparison.OrdinalIgnoreCase))
                disabled.AddRange(value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        return (addonPath, disabled);
    }

    /// <summary>An ini file's key=value lines (<see cref="IniLines"/>).</summary>
    static IEnumerable<(string Section, bool Closed, string? Key, string Value, string Line)> IniEntries(FileInfo ini) => IniAll(ini).Where(e => e.Key != null);

    static IEnumerable<(string Section, bool Closed, string? Key, string Value, string Line)> IniAll(FileInfo ini) =>
        IniLines((Cached(ini, "ini", () => File.ReadAllText(ini.FullName)) as string ?? "").Split('\n'));

    /// <summary>Each line of an ini file with the section it is in, as ReShade's ini_file.cpp and SimpleIni (OptiScaler) read
    /// it. A line starting with '[' starts a section: its text up to ']', the rest of the line ignored; without a ']',
    /// ReShade names it by the rest of the line and SimpleIni by no usable name (<c>Closed</c> false). Key and value
    /// (null key: no '=') are trimmed.</summary>
    static IEnumerable<(string Section, bool Closed, string? Key, string Value, string Line)> IniLines(IEnumerable<string> lines)
    {
        var (section, closed) = ("", true);
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                var close = line.IndexOf(']');
                (section, closed) = (line[1..(close > 0 ? close : line.Length)].Trim(), close > 0);
                yield return (section, closed, null, "", raw);
            }
            else if (line.IndexOf('=') is > 0 and var eq) yield return (section, closed, line[..eq].Trim(), line[(eq + 1)..].Trim(), raw);
            else yield return (section, closed, null, "", raw);
        }
    }

    /// <summary>The last verdict ReShade.log gives each add-on file: LayoutInjecting or ReplacesShaders.</summary>
    static IReadOnlyDictionary<string, AddonKind> LogVerdicts(FileInfo log) =>
        Cached(log, "log", () =>
        {
            var (files, verdicts) = (new Dictionary<string, string?>(), new Dictionary<string, AddonKind>(StringComparer.OrdinalIgnoreCase));
            string? loading = null;
            using var r = new StreamReader(new FileStream(log.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));   // a running game writes it
            for (string? line; (line = r.ReadLine()) != null;)
                if (Loading.Match(line) is { Success: true } l) loading = l.Groups[1].Value;
                else if (Registered.Match(line) is { Success: true } g && loading != null) (files[g.Groups[1].Value], loading) = (FullPath(loading), null);
                else if (Pipeline.Match(line) is { Success: true } p && files.TryGetValue(p.Groups[1].Value, out var file) && file != null)
                    verdicts[file] = p.Groups[2].Value.StartsWith("OnCreate", StringComparison.Ordinal) ? AddonKind.LayoutInjecting : AddonKind.ReplacesShaders;
            return verdicts;
        }) as IReadOnlyDictionary<string, AddonKind> ?? new Dictionary<string, AddonKind>();

    // as FileInfo.FullName has it: ReShade logs its search folder joined with AddonPath ("C:\Game\.\addons\x.addon64")
    static string? FullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The first of <paramref name="markers"/> the file holds; -1 = none; <see cref="NotRead"/>.</summary>
    static int Find(FileInfo f, string what, byte[][] markers) => Cached(f, what, () =>
    {
        using var s = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[s.Length];
        s.ReadExactly(bytes);
        return Array.FindIndex(markers, m => bytes.AsSpan().IndexOf(m) >= 0);
    }) as int? ?? NotRead;

    /// <summary><paramref name="read"/>'s result (<paramref name="what"/>) for the file while its size, write time, change time and
    /// file id stay the same; null = it
    /// couldn't be read or is over <see cref="MaxBytes"/>.</summary>
    static object? Cached(FileInfo f, string what, Func<object> read)
    {
        f.Refresh();
        if (!f.Exists || f.Length > MaxBytes) return null;
        var key = f.FullName + "|" + what;
        var identity = KeyFiles.Identity(f.FullName);
        if (Probed.TryGetValue(key, out var c) && (c.Length, c.Written, c.Identity) == (f.Length, f.LastWriteTimeUtc, identity)) return c.Value;
        try
        {
            var value = read();
            Probed[key] = (f.Length, f.LastWriteTimeUtc, identity, value);
            if (value is bool or int or string) Interlocked.Increment(ref probesVersion);
            return value;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
