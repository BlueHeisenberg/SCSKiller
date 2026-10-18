using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
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

    /// <summary>The game's Aftermath DLL: next to its exe, else where Unreal loads it from (Engine\Binaries\ThirdParty\NVIDIA\NVaftermath,
    /// in a folder below it). Null when neither has it.
    /// ponytail: those two places only; search the whole install if a game keeps it elsewhere.</summary>
    public static string? DllFor(Game g)
    {
        try
        {
            var beside = Path.Combine(Path.GetDirectoryName(g.ExePath)!, DllName);
            if (File.Exists(beside)) return beside;
            var nv = Path.Combine(g.InstallDir, "Engine", "Binaries", "ThirdParty", "NVIDIA", "NVaftermath");
            return Directory.Exists(nv) ? Directory.EnumerateFiles(nv, DllName, SearchOption.AllDirectories).FirstOrDefault() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>The init a warm of the game mirrors, only one that changes the driver's key (<see cref="AftermathInit.ChangesKey"/>),
    /// with its <paramref name="dll"/> present: what the recorder logged the game doing (<see cref="FromLog"/>), else the plan
    /// rules' entry for the game (nv.aftermath.v1), else, for an Unreal config with shader debug info
    /// (<see cref="EngineInfo.AftermathShaderDebug"/>), Unreal's call: crash dumps for D3D and Vulkan with deferred debug
    /// info callbacks, shader debug info (RHICoreNvidiaAftermath.cpp). The version is the DLL's own where its file version
    /// gives it (<see cref="ApiVersion"/>).</summary>
    public static AftermathInit? Of(Game g, EngineInfo? e, string? recorderLog, string? dll)
    {
        if (dll == null) return null;
        var init = FromLog(recorderLog) ?? Rule(g.Id)
            ?? (e?.AftermathShaderDebug == true && ApiVersion(dll) is { } v ? new AftermathInit(v, 8, 3, 1) : null);
        // the DLL takes only its own API version (2.27 refused 0x217 and 0x21c): a game update may have brought another
        return init is { ChangesKey: true } ? init with { Version = ApiVersion(dll) ?? init.Version } : null;
    }

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
    static readonly ConcurrentDictionary<string, (DateTime At, long Length, AftermathInit? Init)> Logs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The recorder log's last successful EnableGpuCrashDumps and DX12_Initialize; null without both.</summary>
    public static AftermathInit? FromLog(string? path)
    {
        try
        {
            var f = new FileInfo(path ?? "");
            if (path == null || !f.Exists) return null;
            if (Logs.TryGetValue(path, out var c) && c.At == f.LastWriteTimeUtc && c.Length == f.Length) return c.Init;
            Match? dumps = null, init = null;
            using (var r = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)))
                while (r.ReadLine() is { } line)
                    if (line.Contains("aftermath: ", StringComparison.Ordinal))
                    {
                        if (Dumps.Match(line) is { Success: true } d) dumps = d;
                        else if (Init.Match(line) is { Success: true } i) init = i;
                    }
            uint Hex(Match m, int g) => uint.Parse(m.Groups[g].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
            var found = dumps != null && init != null ? new AftermathInit(Hex(init, 1), Hex(init, 2), Hex(dumps, 2), Hex(dumps, 3)) : null;
            Logs[path] = (f.LastWriteTimeUtc, f.Length, found);
            return found;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or OverflowException) { return null; }
    }
}
