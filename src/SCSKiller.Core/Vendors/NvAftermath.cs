using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Enumeration;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.Vendors;

/// <summary>How a game starts NVIDIA Aftermath, as it passes the values: <c>GFSDK_Aftermath_EnableGpuCrashDumps(Version,
/// Apis, DumpFlags, ...)</c> (Apis 0: no crash dumps), then <c>GFSDK_Aftermath_DX12_Initialize(Version, Features, device)</c>.</summary>
public sealed record AftermathInit(uint Version, uint Features, uint Apis = 0, uint DumpFlags = 0)
{
    /// <summary>Crash dumps on with GenerateShaderDebugInfo (0x8): the driver compiles every pipeline under another cache
    /// key. Measured (selftest amwarm): only that flag, resource tracking (0x2) and DRED's device flags don't change it.</summary>
    public bool ChangesKey => (Features & 8) != 0 && Apis != 0;

    /// <summary>scskiller_warm's --aftermath-init.</summary>
    public string Arg => $"{Version:x},{Features:x},{Apis:x},{DumpFlags:x}";
}

/// <summary>NVIDIA Aftermath as a game starts it, so a warm can start it the same way (scskiller_warm --aftermath).</summary>
public static class NvAftermath
{
    public const string DllName = "GFSDK_Aftermath_Lib.x64.dll";

    /// <summary>The game's Aftermath DLL: next to its exe, else anywhere in the install (<see cref="Find"/>; Unreal loads it
    /// from Engine\Binaries\ThirdParty\NVIDIA\NVaftermath, some games from a folder below it). Null when none. Kept per exe
    /// while its size and write time stay and the DLL found is still there.</summary>
    public static string? DllFor(Game g)
    {
        try
        {
            var exe = new FileInfo(g.ExePath);
            var stamp = $"{g.InstallDir}|{(exe.Exists ? $"{exe.Length}:{exe.LastWriteTimeUtc.Ticks}" : "-")}";
            if (Dlls.TryGetValue(g.ExePath, out var c) && c.Stamp == stamp && (c.Dll == null || File.Exists(c.Dll))) return c.Dll;
            var beside = Path.Combine(Path.GetDirectoryName(g.ExePath)!, DllName);
            var dll = File.Exists(beside) ? beside : Find(g.InstallDir);
            Dlls[g.ExePath] = (stamp, dll);
            return dll;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    static readonly ConcurrentDictionary<string, (string Stamp, string? Dll)> Dlls = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Deep enough for Content\Engine\Binaries\ThirdParty\NVIDIA\NVaftermath\2.23\Win64. Nothing skipped here: <see cref="Find"/>
    /// counts junctions and links toward its budget and passes them by.</summary>
    static readonly EnumerationOptions Search = new() { RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true, AttributesToSkip = 0 };

    /// <summary>A walk stops after this many files and folders.</summary>
    internal const int MaxEntries = 200_000;

    /// <summary><see cref="DllName"/> (the x64 build: the warm loads it) under <paramref name="dir"/>, the fewest folders
    /// down; null when none within <see cref="MaxEntries"/>.</summary>
    internal static string? Find(string dir)
    {
        if (!Directory.Exists(dir)) return null;
        var entries = 0;
        // breadth first (the enumerator queues each folder's subfolders): the first one found is the fewest folders down
        foreach (var path in new FileSystemEnumerable<string?>(dir, (ref FileSystemEntry e) => entries > MaxEntries ? null : e.ToFullPath(), Search)
                 {
                     ShouldIncludePredicate = (ref FileSystemEntry e) => ++entries > MaxEntries
                         || !e.IsDirectory && (e.Attributes & FileAttributes.ReparsePoint) == 0 && e.FileName.Equals(DllName, StringComparison.OrdinalIgnoreCase),
                     ShouldRecursePredicate = (ref FileSystemEntry e) => (e.Attributes & FileAttributes.ReparsePoint) == 0,
                 })
            return path;
        return null;
    }

    /// <summary>The init a warm of the game mirrors, only one that changes the driver's key (<see cref="AftermathInit.ChangesKey"/>),
    /// with its <paramref name="dll"/> present: what the recorder logged the game doing (<see cref="FromLog"/>), else the plan
    /// rules' entry for the game (nv.aftermath.v1), else an Unreal game's logged DX12_Initialize (<see cref="Inferred"/>), else
    /// an Unreal game's Engine config (<see cref="ConfigGuess"/>); the last two with Inferred true, judged by the compile's first
    /// launch. The DLL being there alone never counts: Unreal games load it without initializing it. The version is the DLL's
    /// own where its file version gives it (<see cref="ApiVersion"/>). None when the newest session shows the game running
    /// without it (<see cref="LogFacts.Without"/>), whatever an older session or the config says.</summary>
    public static (AftermathInit? Init, bool Inferred) Resolve(Game g, EngineInfo? e, string? recorderLog, string? dll) => Resolve(g, e, Facts(recorderLog), dll);

    /// <param name="shared">the community recordings in use say the game starts it so (<see cref="Shared(EngineInfo?, LogFacts, bool)"/>)</param>
    public static (AftermathInit? Init, bool Inferred) Resolve(Game g, EngineInfo? e, LogFacts facts, string? dll, bool shared = false)
    {
        if (dll == null) return (null, false);
        var init = facts.Without ? null : facts.Init ?? Rule(g.Id);
        // a config guess needs the DLL's own API version: an old one (1.x) has no crash dumps call to make
        var inferred = init == null && (Inferred(e, facts) || (ConfigGuess(e, facts) || Shared(e, facts, shared)) && ApiVersion(dll) != null);
        if (inferred) init = new AftermathInit(0x217, facts.Features ?? 0xa, 3, 1);
        // the DLL takes only its own API version (2.27 refused 0x217 and 0x21c): a game update may have brought another
        return init is { ChangesKey: true } ? (init with { Version = ApiVersion(dll) ?? init.Version }, inferred) : (null, false);
    }

    public static AftermathInit? Of(Game g, EngineInfo? e, string? recorderLog, string? dll) => Resolve(g, e, recorderLog, dll).Init;

    /// <summary>No session says otherwise and the Unreal game's Engine config turns shader debug info on with Aftermath not off
    /// (<see cref="EngineInfo.AftermathShaderDebug"/>): Unreal starts it so (Galactic Racer 0xa, Gears of War: E-Day 0x8) and
    /// games whose config is silent load the DLL without initializing it (Darwin's Paradox, FF7 Rebirth).</summary>
    /// <summary>The last word, when nothing on this PC has one (no session, no rule, no Unreal config turning shader debug
    /// info on or Aftermath off): the community recordings in use say their uploaders' games started it with shader debug
    /// info. Tried as a config guess is, with its flags (never the uploaders' values), and judged by the compile's first launch.</summary>
    public static bool Shared(EngineInfo? e, LogFacts f, bool shared) => shared && !f.Known && e is not { AftermathOff: true };

    public static bool ConfigGuess(EngineInfo? e, LogFacts f) => e is { Family: "Unreal", AftermathShaderDebug: true, AftermathOff: false } && f is { Without: false, Init: null };

    /// <summary>An Unreal game's newest session logged DX12_Initialize with shader debug info but not its crash dumps call,
    /// made before the hook went in (on device creation): Unreal's crash dumps are taken as on, so the flags change the key.</summary>
    public static bool Inferred(EngineInfo? e, LogFacts f) => e is { Family: "Unreal" } && f is { Without: false, Init: null, Features: { } x } && (x & 8) != 0;

    /// <summary><see cref="Of"/> may give an init once the DLL is known: the install's walk (<see cref="DllFor"/>) waits for it.</summary>
    public static bool MayStart(Game g, EngineInfo? e, string? recorderLog) => MayStart(g, e, Facts(recorderLog));

    public static bool MayStart(Game g, EngineInfo? e, LogFacts facts, bool shared = false) => facts switch
    {
        { Without: true } => false,
        { Init: { } init } => init.ChangesKey,
        var f => Rule(g.Id) is { } rule ? rule.ChangesKey : Inferred(e, f) || ConfigGuess(e, f) || Shared(e, f, shared),
    };

    /// <summary>The 'N' record a recording carries <paramref name="f"/> in (<see cref="Planning.HashOnly.AftermathTarget"/>): all
    /// zero when the session ran without an init that changes the key, the init when it ran with one; null when it showed
    /// neither or passed values <see cref="Planning.HashOnly.AftermathOn"/> doesn't take.</summary>
    public static Planning.PsoDb.Rec? Shared(LogFacts f)
    {
        if (!f.Without && f.Init is not { ChangesKey: true }) return null;
        var p = new byte[Planning.PsoDb.NvState.Size];
        Convert.FromHexString(Planning.HashOnly.AftermathTarget).CopyTo(p, 0);
        if (!f.Without && f.Init is { } i)
            foreach (var (v, at) in new[] { i.Version, i.Features, i.Apis, i.DumpFlags }.Select((v, k) => (v, 20 + 4 * k))) System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(p.AsSpan(at), v);
        var r = new Planning.PsoDb.Rec('N', p);
        return Planning.HashOnly.AftermathOn(r) == null ? null : r;
    }

    /// <summary>The init the compile after a compile the game's launch didn't use tries (ScsKiller: once per driver and exe),
    /// with its <paramref name="dll"/> present, only when the log's newest session shows the game loading Aftermath
    /// (<see cref="LogFacts.Loaded"/>) and nothing saying it runs without one that changes the key: no init the recorder
    /// logged (<see cref="FromLog"/>), no plan rule, no <see cref="LogFacts.Without"/>. The feature flags are a DX12_Initialize's that session logged without its crash dumps call, else (DX12_Initialize
    /// not hooked) 0xa (resource tracking and shader debug info, as Mafia: The Old Country passes them), with Unreal's crash dumps.</summary>
    public static AftermathInit? Retry(Game g, string? recorderLog, string? dll) => Retry(g, null, Facts(recorderLog), dll);

    /// <param name="e">an Unreal config turning Aftermath off (<see cref="EngineInfo.AftermathOff"/>) rules the retry out</param>
    public static AftermathInit? Retry(Game g, EngineInfo? e, LogFacts facts, string? dll) =>
        dll == null || Rule(g.Id) != null || e is { AftermathOff: true } || facts is not { Without: false, Init: null, Loaded: true } ? null
            : new AftermathInit(ApiVersion(dll) ?? 0x217, facts.Features ?? 0xa, 3, 1);

    public static AftermathInit? Rule(string gameId) =>
        Planning.RulesFile.Current.Rules.Select(r => r.Aftermath?.GetValueOrDefault(gameId)).OfType<AftermathInit>().FirstOrDefault();

    /// <summary>GFSDK_Aftermath_Version_API of a DLL: 0x200 + the minor of its file version 2.x (measured: 2.23 takes 0x217, 2.27 0x21b).</summary>
    public static uint? ApiVersion(string dll)
    {
        try { return FileVersionInfo.GetVersionInfo(dll) is { FileMajorPart: 2, FileMinorPart: var minor and < 256 } ? 0x200u + (uint)minor : null; }
        catch (FileNotFoundException) { return null; }
    }

    static readonly Regex Dumps = new(@"aftermath: EnableGpuCrashDumps\(version 0x(\w+), apis 0x(\w+), flags 0x(\w+), [^)]*\) = 0x1$", RegexOptions.CultureInvariant);
    static readonly Regex Init = new(@"aftermath: DX12_Initialize\(version 0x(\w+), feature flags 0x(\w+)\) = 0x1\b", RegexOptions.CultureInvariant);
    static readonly ConcurrentDictionary<string, (DateTime At, long Length, LogFacts Facts)> Logs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a recorder log tells about the game's Aftermath. <paramref name="Init"/>: its last successful
    /// EnableGpuCrashDumps and DX12_Initialize, null without both. <paramref name="Without"/>: the newest session (from its
    /// last "loaded into" line) shows the game running without an init that changes the key: a DX12_Initialize without
    /// shader debug info, crash dumps with no APIs, devices created and the DLL never loaded, or DX12_Initialize hooked and
    /// never called (the hook goes in on device creation, or as the DLL loads after a device, before the game's init).
    /// <paramref name="Features"/>: that session's DX12_Initialize feature flags. <paramref name="Loaded"/>: the session had
    /// the game's Aftermath DLL loaded.</summary>
    public readonly record struct LogFacts(AftermathInit? Init, bool Without, uint? Features, bool Loaded = false)
    {
        /// <summary>The session showed anything about Aftermath: a newer one without anything doesn't replace a stored one.</summary>
        public bool Known => Init != null || Without || Features != null || Loaded;
    }

    /// <summary>The recorder log's last successful EnableGpuCrashDumps and DX12_Initialize; null without both.</summary>
    public static AftermathInit? FromLog(string? path) => Facts(path).Init;

    /// <summary><see cref="LogFacts"/> of the recorder log at <paramref name="path"/>, kept while its size and write time stay.</summary>
    public static LogFacts Facts(string? path)
    {
        try
        {
            var f = new FileInfo(path ?? "");
            if (path == null || !f.Exists) return default;
            if (Logs.TryGetValue(path, out var c) && c.At == f.LastWriteTimeUtc && c.Length == f.Length) return c.Facts;
            Match? dumps = null, init = null;
            uint? features = null, apis = null;
            var (devices, loaded, hooked, late) = (0, 0, false, false);
            uint Hex(Match m, int g) => uint.Parse(m.Groups[g].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            using (var r = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                while (r.ReadLine() is { } line)
                    if (line.Contains("] loaded into ", StringComparison.Ordinal)) (features, apis, devices, loaded, hooked, late) = (null, null, 0, 0, false, false);
                    else if (line.Contains("] device: ", StringComparison.Ordinal) && line.EndsWith("; NVIDIA Aftermath loaded", StringComparison.Ordinal)) (devices, loaded) = (devices + 1, loaded + 1);
                    else if (line.Contains("] device: ", StringComparison.Ordinal) && line.EndsWith("; NVIDIA Aftermath not loaded", StringComparison.Ordinal)) devices++;
                    // the DLL loaded after the session's devices: hooked as it loaded, or the recorder couldn't
                    else if (line.Contains("dedupe: off, NVIDIA Aftermath loaded", StringComparison.Ordinal)) late = true;
                    else if (line.Contains("aftermath: ", StringComparison.Ordinal))
                    {
                        if (line.Contains("aftermath: at its load, ", StringComparison.Ordinal)) late = true;
                        if (line.EndsWith(", DX12_Initialize hooked", StringComparison.Ordinal)) hooked = true;
                        if (Dumps.Match(line) is { Success: true } d) (dumps, apis) = (d, Hex(d, 2));
                        else if (Init.Match(line) is { Success: true } i) (init, features) = (i, Hex(i, 2));
                    }
            var dll = loaded > 0 || late || features != null || apis != null;   // the session had the game's Aftermath DLL loaded
            var found = new LogFacts(dumps != null && init != null ? new AftermathInit(Hex(init, 1), Hex(init, 2), Hex(dumps, 2), Hex(dumps, 3)) : null,
                features is { } fl && (fl & 8) == 0 || apis == 0 || devices > 0 && !dll || hooked && features == null, features, dll);
            Logs[path] = (f.LastWriteTimeUtc, f.Length, found);
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException) { return default; }
    }
}
