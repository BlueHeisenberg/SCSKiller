using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Microsoft.Win32;

namespace SCSKiller.Core.Vendors;

/// <summary>NVIDIA via NvAPI (nvapi64.dll). Caps measured on driver 610.88, see ARCHITECTURE.md.
///
/// Shader cache size = DRS setting "Shader disk cache maximum size" (id 0x00AC8497 on 610.88, looked up by name) in the
/// base (global) profile, what NVIDIA Control Panel's "Shader Cache Size" and Profile Inspector edit. Value encoding
/// (verified: NVCP "100 GB" reads back 0x00019000 = 102400):
///   setting absent from the base profile = driver default (the driver reports its default value: 0x4000 = 16 GB on 610.88)
///   N (1..0xFFFFFFFE) = N MiB;  0 = cache disabled;  0xFFFFFFFF = unlimited.
/// Setting "default" deletes the override from the base profile. Saving needs admin.</summary>
public sealed unsafe class NvidiaBackend : IGpuVendorBackend, IRefreshableGpu
{
    public const string CacheSizeSettingName = "Shader disk cache maximum size";
    const uint KnownCacheSizeId = 0x00AC8497;
    const uint Unlimited = 0xFFFFFFFF;
    const int SettingNotFound = -160, InvalidUserPrivilege = -137, EndEnumeration = -7;
    const long MiB = 1 << 20;

    public NvidiaBackend(GpuInfo dxgi)
    {
        Check(((delegate* unmanaged<int>)Fn(0x0150E828))(), "NvAPI_Initialize");
        var fromDxgi = DriverVersionOf(dxgi.DriverVersion, Wine.Running);
        (Gpu, _confirmed) = (dxgi with { DriverVersion = fromDxgi ?? ReadDriverVersion() }, fromDxgi != null);
    }

    bool _confirmed;   // the version came from DXGI once: NvAPI's may be older

    internal static string ReadDriverVersion()
    {
        uint ver;
        byte* branch = stackalloc byte[64];
        Check(((delegate* unmanaged<uint*, byte*, int>)Fn(0x2926AAAD))(&ver, branch), "NvAPI_SYS_GetDriverAndBranchVersion");
        return $"{ver / 100}.{ver % 100:00}";
    }

    /// <summary>DXGI's version first: the process keeps the nvapi64.dll it loaded at start, which after a driver update may
    /// fail or still answer the old version. Without one from DXGI (incomplete): the last DXGI one stays, NvAPI's only
    /// when there never was one.</summary>
    public bool Refresh(GpuInfo adapter)
    {
        if (DriverVersionOf(adapter.DriverVersion, Wine.Running) is { } dxgi)
        {
            (Gpu, _confirmed) = (adapter with { DriverVersion = dxgi }, true);
            return true;
        }
        string? nv = null;
        if (!_confirmed)
            try { nv = ReadDriverVersion(); }
            catch (InvalidOperationException) { }
        Gpu = adapter with { DriverVersion = nv ?? Gpu.DriverVersion };
        return false;
    }

    public string FallbackVersion(string umd) => DriverVersionOf(umd, Wine.Running) ?? umd;

    /// <summary>The driver version an adapter's DriverVersion holds: <see cref="FromUserModeVersion"/>, or under Wine the host
    /// driver's as GpuBackends read it ("615.71.09"); null when it holds neither.</summary>
    public static string? DriverVersionOf(string adapterVersion, bool wine) =>
        wine && adapterVersion.Split('.') is [_, _, _] ? adapterVersion : FromUserModeVersion(adapterVersion);

    /// <summary>NVIDIA's version in a DXGI user-mode driver version: its last five digits ("32.0.16.1714" = "617.14");
    /// null when it isn't one.</summary>
    public static string? FromUserModeVersion(string umd) =>
        umd.Split('.') is [_, _, var a, var b] && int.TryParse(a, out var hi) && int.TryParse(b, out var lo) && lo < 10000
        && $"{hi}{lo:0000}" is { Length: >= 5 } d ? $"{d[^5..^2]}.{d[^2..]}" : null;

    public GpuVendor Vendor => GpuVendor.Nvidia;
    public GpuInfo Gpu { get; private set; }
    public VendorCaps Caps { get; } = new("nvidia-1", CacheKeyedByExeName: true, StateIndependentCache: true, CacheSizeConfigurable: true,
        PerStageCache: true,    // measured: a pipeline of stages cached in other pairings costs 0.40 ms, as an exact hit
        RtCacheGranularity: RtCacheGranularity.Collection,   // selftest dxr: collections cached on their own; Jedi's recorded ones 42.7 -> 3.35 ms after synthesized ones
        PackageKeyed: true);

    public static string CacheDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NVIDIA", "DXCache");

    /// <summary>The DXCache's per-application files.</summary>
    public NvidiaAppCache AppCache { get; } = new(CacheDir);
    IAppCache? IGpuVendorBackend.AppCache => AppCache;

    public CacheUsage GetCacheUsage() => new(CacheDir, AppCacheFiles.Readable(CacheDir)
        ? new DirectoryInfo(CacheDir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length)
        : 0, UpperBound: true);

    /// <summary>The setting id plus its raw value in the base profile (null = not set there = driver default).</summary>
    public (uint Id, uint? Raw, uint DriverDefault) ReadCacheSetting()
    {
        var id = CacheSizeSettingId();
        using var drs = new Drs();
        return (id, ReadBase(drs, id), DriverDefault(id));
    }

    /// <summary>A DWORD setting's value in the base profile; null = not set there.</summary>
    static uint? ReadBase(Drs drs, uint id)
    {
        byte* s = stackalloc byte[SettingSize];
        InitSetting(s);
        int rc = ((delegate* unmanaged<nint, nint, uint, byte*, int>)Fn(0x73BF8338))(drs.Session, drs.Profile, id, s);   // NvAPI_DRS_GetSetting
        if (rc != SettingNotFound) Check(rc, "NvAPI_DRS_GetSetting");
        return rc == 0 && *(uint*)(s + OffIsCurrentPredefined) == 0 ? *(uint*)(s + OffCurrentValue) : null;
    }

    /// <summary>NVIDIA App's "Automatic Shader Compilation (Beta)" (driver r610+): a DWORD in the base profile that the public
    /// NvAPI has no name for. Evidence (read-only; 610.88, NVIDIA App 11.0.9): the App's console.log lists "PreCompileShader"
    /// with drsEnumId 0x00EAD189 and values 0 Off, 1 Low, 2 Medium, 3 High ("System Utilization"); the App's NvCplPlugin.dll
    /// (writer) and the driver's NvOSC.exe (reader: "Osc disabled by NvApp setting") both contain the id. Not set = Off.
    /// The setting alone compiles nothing: the App then runs the driver's <c>NvOSC.exe -register</c>, which reads the value
    /// from the DRS db and (re)creates the idle task "\NVIDIA Shader Compiler" (XML priority 10/4/2 for Low/Medium/High;
    /// the task runs <c>NvOSC.exe -runTool</c>). Disassembly of NvOSC.exe (r610.85): <c>-register</c> with 0 stops and
    /// disables the task; <c>-deregister</c> stops, disables and deletes it. r617.14: <c>-register</c> with 1..3 stops,
    /// disables and deletes an existing task, then registers create-only (TASK_CREATE), so it exits 5 when the old one
    /// can't be removed.</summary>
    public const uint AutoShaderCompilationId = 0x00EAD189;
    public const string AutoShaderTaskName = @"\NVIDIA Shader Compiler";

    /// <summary>The NVIDIA App's own DRS calls (NvCplPlugin.dll, via nvapi_QueryInterface like the public ones). The public
    /// NvAPI_DRS_GetSetting/SetSetting answer -160 (not found) for the hidden 0x00EAD189 even when it is set; these see it.
    /// SetSettingEx(session, profile, NVDRS_SETTING_V1*, 0, 0); GetSettingEx(session, profile, id, NVDRS_SETTING_V1*, uint* flags = 0).
    /// Verified in an unsaved session on 610.88: SetSettingEx(0x00EAD189 = 2) = 0, then GetSettingEx reads 2.</summary>
    public const uint GetSettingExId = 0xEA99498D, SetSettingExId = 0x8A2CF5F5, GetSettingId = 0x73BF8338;

    public AutoShaderState? GetAutoShaderCompilation()
    {
        try
        {
            AutoShaderCompilation? level;
            using (var drs = new Drs()) level = ReadAutoShaderLevel(drs.Session, drs.Profile, TryFn(GetSettingExId), Fn(GetSettingId));
            return level is { } l ? TaskState(l, QueryTaskXml(), NvOscPath()) : null;
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or Win32Exception) { return null; }
    }

    /// <summary>GetSettingEx when the driver has it (not found there = not set = Off); else the public GetSetting, whose
    /// "not found" proves nothing for a hidden setting: null (unknown), never a guessed Off.</summary>
    public static AutoShaderCompilation? ReadAutoShaderLevel(nint session, nint profile, nint getEx, nint get)
    {
        byte* s = stackalloc byte[SettingSize];
        if (getEx != 0)
        {
            InitSetting(s);
            uint flags = 0;
            int rc = ((delegate* unmanaged<nint, nint, uint, byte*, uint*, int>)getEx)(session, profile, AutoShaderCompilationId, s, &flags);
            if (rc == SettingNotFound) return AutoShaderCompilation.Off;
            if (rc == 0) return DecodeAutoShaderCompilation(*(uint*)(s + OffCurrentValue));
        }
        InitSetting(s);
        return ((delegate* unmanaged<nint, nint, uint, byte*, int>)get)(session, profile, AutoShaderCompilationId, s) == 0
            ? DecodeAutoShaderCompilation(*(uint*)(s + OffCurrentValue)) : null;
    }

    /// <summary>SetSettingEx of the DWORD 0x00EAD189 in the given profile (in the session only; SaveSettings persists it).</summary>
    public static int WriteAutoShaderLevel(nint session, nint profile, nint setEx, AutoShaderCompilation level)
    {
        byte* s = stackalloc byte[SettingSize];
        InitSetting(s);
        *(uint*)(s + OffSettingId) = AutoShaderCompilationId;
        *(uint*)(s + OffSettingType) = 0;   // NVDRS_DWORD_TYPE
        *(uint*)(s + OffCurrentValue) = (uint)level;
        return ((delegate* unmanaged<nint, nint, byte*, uint, uint, int>)setEx)(session, profile, s, 0, 0);
    }

    static void InitSetting(byte* s)
    {
        new Span<byte>(s, SettingSize).Clear();
        *(uint*)s = SettingVersion1;
    }

    public static AutoShaderCompilation? DecodeAutoShaderCompilation(uint? raw) =>
        raw switch { null => AutoShaderCompilation.Off, <= 3u => (AutoShaderCompilation)raw.Value, _ => null };

    /// <summary>What the NVIDIA App enforces: no automatic compilation while the shader cache is Disabled.</summary>
    public static string? RefuseReason(AutoShaderCompilation level, CacheLimit? cache) =>
        level != AutoShaderCompilation.Off && cache is { Bytes: 0 }
            ? "The driver's shader cache is disabled, so there is nothing to compile into. Set a cache size first." : null;

    /// <summary>Enable: the setting 1..3, saved, then <c>NvOSC.exe -register</c> (as the NVIDIA App does). Disable: 0, saved,
    /// then <c>NvOSC.exe -deregister</c> (deletes the task; the App's -register would leave it disabled). Only the part that
    /// differs from the current state is done (<see cref="AutoShaderSteps"/>): an already set value is never rewritten, and
    /// a state that already matches touches nothing. The setting is written first: without admin rights nothing is
    /// touched. If NvOSC fails after the save, the state reads "on, task not ready" and running this again only retries
    /// NvOSC. Global: callers must have the user's explicit OK. Never run from tests.</summary>
    public string SetAutoShaderCompilation(AutoShaderCompilation level)
    {
        var (write, arg) = AutoShaderSteps(GetAutoShaderCompilation(), level);
        if (arg == null) return AutoShaderOutcome(level, changed: false);
        if (RefuseReason(level, GetCacheLimit()) is { } why) throw new InvalidOperationException(why);
        var osc = NvOscPath() ?? throw new InvalidOperationException("NvOSC.exe was not found in the NVIDIA driver's folder (it needs driver r610 or newer)");
        if (write)
        {
            var setEx = TryFn(SetSettingExId) is var f and not 0 ? f
                : throw new InvalidOperationException("this NVIDIA driver can't switch Auto Shader Compilation from outside the NVIDIA App (no NvAPI_DRS_SetSettingEx)");
            using var drs = new Drs();
            var (rc, call) = (WriteAutoShaderLevel(drs.Session, drs.Profile, setEx, level), "NvAPI_DRS_SetSettingEx");
            if (rc == 0) (rc, call) = (((delegate* unmanaged<nint, int>)Fn(0xFCBC7E14))(drs.Session), "NvAPI_DRS_SaveSettings");
            if (rc != 0) throw AutoShaderDrsFailure(call, rc, ErrorName(rc));
        }
        Process p;
        var output = new System.Collections.Concurrent.ConcurrentQueue<string>();
        try
        {
            p = new Process { StartInfo = new ProcessStartInfo(osc, arg)
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(osc)!, RedirectStandardOutput = true, RedirectStandardError = true } };
            p.OutputDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) output.Enqueue(e.Data); };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
        }
        catch (Win32Exception e) { throw new InvalidOperationException(NvOscFailure(arg, null, write, e.Message)); }
        using (p)
        {
            if (!p.WaitForExit(60_000)) throw new InvalidOperationException(NvOscFailure(arg, null, write, output: string.Join('\n', output)));
            p.WaitForExit();   // the redirected output to its end
            if (p.ExitCode != 0) throw new InvalidOperationException(NvOscFailure(arg, p.ExitCode, write, output: string.Join('\n', output)));
        }
        if (GetAutoShaderCompilation() is { } after && !after.Is(level))
            throw new InvalidOperationException(after.Level != level
                ? $"the driver still reads {after.Level} after NvOSC.exe {arg}"
                : (write ? "the setting is saved, but " : "") + $"NvOSC.exe {arg} finished and the NVIDIA task " +
                  (level == AutoShaderCompilation.Off ? "is still enabled" : "still isn't set up"));
        return AutoShaderOutcome(level, changed: true);
    }

    /// <summary>What switching to <paramref name="level"/> still needs: the setting written and saved, and NvOSC.exe's
    /// argument (null = the state already matches, nothing to do). Unreadable state (null): both, as asked.</summary>
    public static (bool WriteSetting, string? NvOscArg) AutoShaderSteps(AutoShaderState? now, AutoShaderCompilation level) =>
        now is { } s && s.Is(level) ? (false, null)
            : (now?.Level != level, level == AutoShaderCompilation.Off ? "-deregister" : "-register");

    public static string AutoShaderOutcome(AutoShaderCompilation level, bool changed) => (level, changed) switch
    {
        (AutoShaderCompilation.Off, false) => "Already off. Nothing to change.",
        (AutoShaderCompilation.Off, true) => "Turned off.",
        (_, false) => $"Already on ({level}). Nothing to change.",
        _ => $"Turned on ({level}).",
    };

    /// <summary>A failed SetSettingEx or SaveSettings: the session is destroyed unsaved, so nothing changed.</summary>
    public static Exception AutoShaderDrsFailure(string call, int rc, string? name) => rc switch
    {
        InvalidUserPrivilege => new UnauthorizedAccessException("switching NVIDIA Auto Shader Compilation needs administrator rights"),
        SettingNotFound => new InvalidOperationException("this NVIDIA driver doesn't have the Auto Shader Compilation setting"),
        _ => new InvalidOperationException($"the NVIDIA driver didn't take the setting, nothing was changed ({call} answered {rc}{(string.IsNullOrEmpty(name) ? "" : " " + name)})"),
    };

    // NvOSC.exe's own names for its exit codes ("Exiting with code %d (%s)", r617).
    static readonly string[] NvOscExits = ["Success", "General abnormal termination", "Major error", "Library/DLL loading failure",
        "Initialization failure", "Task scheduler operation failure", "Memory allocation/resource failure", "Configuration/command line argument error",
        "Compilation/processing error", "File/disk cache error", "Memory leak detected", "Thread pool initialization failure"];

    /// <summary>NvOSC.exe didn't do its job: it exited with <paramref name="exitCode"/>, didn't start
    /// (<paramref name="startError"/>), or (neither) is still running after a minute, when a retry could overlap it.
    /// Its console <paramref name="output"/>: its reason in the first line, all of it on the lines below.</summary>
    public static string NvOscFailure(string arg, int? exitCode, bool settingSaved, string? startError = null, string? output = null)
    {
        var task = arg == "-deregister" ? "removed" : "set up";
        var saved = settingSaved ? "the setting is saved, but " : "";
        var lines = (output ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        // its last line repeats the exit code: the reason is the last line with an error code, else the one before it
        var reason = lines.LastOrDefault(l => l.Contains("0x", StringComparison.OrdinalIgnoreCase))
                     ?? lines.LastOrDefault(l => !l.StartsWith("Exiting", StringComparison.Ordinal));
        var said = reason != null ? $" NvOSC.exe said: {reason}" : "";
        var full = lines.Count > 0 ? "\nNvOSC.exe output:\n" + string.Join('\n', lines) : "";
        if (exitCode == null && startError == null)
            return saved + $"NvOSC.exe {arg} is still running after a minute, so the NVIDIA task may not be {task} yet.{said} Wait a minute before applying again." + full;
        var why = exitCode is { } c ? $"failed with {(c >= 0 && c < NvOscExits.Length ? NvOscExits[c] : "an unknown error")} (exit code {c})"
            : $"didn't start ({startError})";
        return saved + $"the NVIDIA task couldn't be {task}: NvOSC.exe {arg} {why}.{said} Apply again to retry." + full;
    }

    /// <summary>NvOSC.exe of the active NVIDIA driver; null before r610.</summary>
    public static string? NvOscPath() => DriverFolders().Select(d => Path.Combine(d, "NvOSC.exe")).FirstOrDefault(File.Exists);

    /// <summary>When the active NVIDIA driver was installed: its DriverStore folder's creation time. The driver deletes the
    /// older driver's D3D12 caches at install, and files it last wrote before then come from an older driver.</summary>
    public static DateTime? InstalledAt() => DriverFolders().Select(Directory.GetCreationTimeUtc).Cast<DateTime?>().FirstOrDefault();

    /// <summary>The active NVIDIA driver's folders: the display class key's UserModeDriverName folder (the DriverStore holds
    /// a folder per driver ever installed, so never search it). Only inside the DriverStore.</summary>
    static List<string> DriverFolders()
    {
        var store = Path.Combine(Environment.SystemDirectory, "DriverStore", "FileRepository") + Path.DirectorySeparatorChar;
        var found = new List<string>();
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            foreach (var name in cls?.GetSubKeyNames() ?? [])
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;   // 0000, 0001, ... ("Properties" is locked)
                try
                {
                    using var k = cls!.OpenSubKey(name);
                    if (k == null || (k.GetValue("MatchingDeviceId") as string)?.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) != true) continue;
                    if (k.GetValue("UserModeDriverName") is not string[] { Length: > 0 } umd || Path.GetDirectoryName(umd[0]) is not { } dir) continue;
                    var full = Path.GetFullPath(dir);
                    if (full.StartsWith(store, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full)) found.Add(full);
                }
                catch (System.Security.SecurityException) { }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return found;
    }

    /// <summary>The task's XML (schtasks /XML: not localized, unlike its status column), or null when it isn't registered.
    /// NvOSC registers it in the user's own task folder (\Users\&lt;user&gt;\, seen on driver 610.88), older builds at the root.</summary>
    static string? QueryTaskXml() =>
        QueryTaskXml($@"\Users\{Environment.UserName}{AutoShaderTaskName}") ?? QueryTaskXml(AutoShaderTaskName);

    static string? QueryTaskXml(string name)
    {
        var psi = new ProcessStartInfo("schtasks.exe", ["/Query", "/TN", name, "/XML"])
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        var xml = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        _ = err.Result;
        return p.ExitCode == 0 ? xml : null;
    }

    // The task's XML priority for Low, Medium, High, as NvOSC.exe -register writes it.
    static readonly int[] TaskPriorities = [10, 4, 2];

    /// <summary>The setting plus its task's XML (null = not registered). Enabled: Settings/Enabled absent or true (the NVIDIA
    /// App disables rather than deletes it). Ready: enabled, runs <paramref name="nvosc"/>, the active driver's (a task left
    /// from an earlier driver runs that driver's NvOSC.exe, null = none found: never ready), and has the level's priority
    /// (a save whose NvOSC run failed leaves the old level's task). A priority NvOSC doesn't write isn't judged.
    /// Unparsable XML: enabled, not ready. An enabled task with the setting Off compiles nothing: NvOSC -runTool checks it
    /// ("Osc disabled by NvApp setting").</summary>
    public static AutoShaderState TaskState(AutoShaderCompilation level, string? taskXml, string? nvosc)
    {
        if (string.IsNullOrWhiteSpace(taskXml)) return new(level, false, false);
        try
        {
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var task = XDocument.Parse(taskXml.Trim()).Root;
            var settings = task?.Element(ns + "Settings");
            var enabled = settings?.Element(ns + "Enabled")?.Value is not { } e || bool.Parse(e.Trim());
            var command = task?.Element(ns + "Actions")?.Element(ns + "Exec")?.Element(ns + "Command")?.Value.Trim().Trim('"');
            var priority = int.TryParse(settings?.Element(ns + "Priority")?.Value, out var p) ? p : -1;
            var ready = enabled && nvosc != null && string.Equals(command, nvosc, StringComparison.OrdinalIgnoreCase)
                && (level == AutoShaderCompilation.Off || !TaskPriorities.Contains(priority) || priority == TaskPriorities[(int)level - 1]);
            return new(level, ready, enabled);
        }
        catch (Exception e) when (e is System.Xml.XmlException or FormatException) { return new(level, false, true); }
    }

    /// <summary>Every setting stored in the base profile (read-only; for before/after diffs of NVIDIA App toggles).
    /// Value is the DWORD value, meaningless for string/binary types.</summary>
    public static List<(uint Id, string? Name, uint Type, uint Value)> BaseProfileSettings()
    {
        const int max = 1024;
        using var drs = new Drs();
        var a = (byte*)NativeMemory.AllocZeroed((nuint)SettingSize * max);
        try
        {
            for (int i = 0; i < max; i++) *(uint*)(a + SettingSize * i) = SettingSize | 1 << 16;
            uint n = max;
            int rc = ((delegate* unmanaged<nint, nint, uint, uint*, byte*, int>)Fn(0xAE3039DA))(drs.Session, drs.Profile, 0, &n, a);   // NvAPI_DRS_EnumSettings
            if (rc == EndEnumeration) n = 0;   // the profile is empty
            else Check(rc, "NvAPI_DRS_EnumSettings");
            var list = new List<(uint, string?, uint, uint)>();
            for (int i = 0; i < n; i++)
            {
                var e = a + SettingSize * i;
                uint id = *(uint*)(e + OffSettingId);
                list.Add((id, SettingName(id), *(uint*)(e + OffSettingType), *(uint*)(e + OffCurrentValue)));
            }
            return list;
        }
        finally { NativeMemory.Free(a); }
    }

    public CacheLimit? GetCacheLimit()
    {
        try
        {
            var (_, raw, def) = ReadCacheSetting();
            return raw is { } v ? Decode(v, false) : Decode(def, true);
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException) { return null; }
    }

    public static CacheLimit Decode(uint raw, bool isDefault) => new(raw == Unlimited ? null : raw * MiB, isDefault);

    public static uint Encode(CacheLimit limit)
    {
        if (limit.Bytes is not { } b) return Unlimited;
        if (b < 0 || b / MiB >= Unlimited) throw new ArgumentOutOfRangeException(nameof(limit), "cache size out of range");
        return (uint)((b + MiB - 1) / MiB);
    }

    // Global driver setting: callers must have the user's explicit OK. Never run from tests.
    public void SetCacheLimit(CacheLimit limit)
    {
        var id = CacheSizeSettingId();
        using var drs = new Drs();
        if (limit.IsDriverDefault)
        {
            int rc = ((delegate* unmanaged<nint, nint, uint, int>)Fn(0xE4A26362))(drs.Session, drs.Profile, id);   // NvAPI_DRS_DeleteProfileSetting
            if (rc != SettingNotFound) Check(rc, "NvAPI_DRS_DeleteProfileSetting");
        }
        else WriteBase(drs, id, Encode(limit));
        Save(drs, "changing the NVIDIA shader cache size needs administrator rights");
    }

    static void WriteBase(Drs drs, uint id, uint value)
    {
        byte* s = stackalloc byte[SettingSize];
        InitSetting(s);
        *(uint*)(s + OffSettingId) = id;
        *(uint*)(s + OffSettingType) = 0;   // NVDRS_DWORD_TYPE
        *(uint*)(s + OffCurrentValue) = value;
        Check(((delegate* unmanaged<nint, nint, byte*, int>)Fn(0x577DD202))(drs.Session, drs.Profile, s), "NvAPI_DRS_SetSetting");
    }

    static void Save(Drs drs, string needsAdmin)
    {
        int save = ((delegate* unmanaged<nint, int>)Fn(0xFCBC7E14))(drs.Session);   // NvAPI_DRS_SaveSettings
        if (save == InvalidUserPrivilege) throw new UnauthorizedAccessException(needsAdmin);
        Check(save, "NvAPI_DRS_SaveSettings");
    }

    /// <summary>The known id if the driver still names it so, else a search of every available setting by name.</summary>
    public static uint CacheSizeSettingId()
    {
        if (SettingName(KnownCacheSizeId) == CacheSizeSettingName) return KnownCacheSizeId;
        var enumIds = ((delegate* unmanaged<uint*, uint*, int>)Fn(0xF020614A));   // NvAPI_DRS_EnumAvailableSettingIds
        var ids = new uint[8192];
        uint n = (uint)ids.Length;
        fixed (uint* p = ids) Check(enumIds(p, &n), "NvAPI_DRS_EnumAvailableSettingIds");
        foreach (var id in ids.AsSpan(0, (int)n))
            if (string.Equals(SettingName(id), CacheSizeSettingName, StringComparison.OrdinalIgnoreCase)) return id;
        throw new InvalidOperationException($"NVIDIA driver has no '{CacheSizeSettingName}' setting");
    }

    static string? SettingName(uint id)
    {
        char* name = stackalloc char[2048];
        name[0] = '\0';
        return ((delegate* unmanaged<uint, char*, int>)Fn(0xD61CBE6E))(id, name) == 0 ? new string(name) : null;   // NvAPI_DRS_GetSettingNameFromId
    }

    static uint DriverDefault(uint id)
    {
        const int size = 12 + 4100 * 101;   // NVDRS_SETTING_VALUES: version, count, type, default + 100 values (unions of 4100 bytes)
        var v = (byte*)NativeMemory.AllocZeroed(size);
        try
        {
            *(uint*)v = size | 1 << 16;
            uint max = 100;
            Check(((delegate* unmanaged<uint, uint*, byte*, int>)Fn(0x2EC39F90))(id, &max, v), "NvAPI_DRS_EnumAvailableSettingValues");
            return *(uint*)(v + 12);
        }
        finally { NativeMemory.Free(v); }
    }

    // NVDRS_SETTING_V1 (12320 bytes): version, wchar name[2048], id, type, location, isCurrentPredefined, isPredefinedValid,
    // predefined value union (4100), current value union (4100).
    const int SettingSize = 12320, OffSettingId = 4100, OffSettingType = 4104, OffIsCurrentPredefined = 4112, OffCurrentValue = 8220;
    public const uint SettingVersion1 = SettingSize | 1u << 16;   // NVDRS_SETTING_VER1


    sealed class Drs : IDisposable
    {
        public readonly nint Session, Profile;
        public Drs()
        {
            nint s, p;
            Check(((delegate* unmanaged<nint*, int>)Fn(0x0694D52E))(&s), "NvAPI_DRS_CreateSession");
            Session = s;
            try
            {
                Check(((delegate* unmanaged<nint, int>)Fn(0x375DBD6B))(s), "NvAPI_DRS_LoadSettings");
                Check(((delegate* unmanaged<nint, nint*, int>)Fn(0xDA8466A0))(s, &p), "NvAPI_DRS_GetBaseProfile");
            }
            catch { Dispose(); throw; }   // the caller's using never gets the object
            Profile = p;
        }
        public void Dispose() => ((delegate* unmanaged<nint, int>)Fn(0xDAD9CFF8))(Session);   // NvAPI_DRS_DestroySession
    }

    static nint _queryInterface;

    static nint Fn(uint id) => TryFn(id) is var p and not 0 ? p : throw new InvalidOperationException($"NvAPI function 0x{id:X8} not available");

    /// <summary>0 when this driver's nvapi64.dll doesn't have the function.</summary>
    static nint TryFn(uint id)
    {
        if (_queryInterface == 0) _queryInterface = NativeLibrary.GetExport(NativeLibrary.Load("nvapi64.dll"), "nvapi_QueryInterface");
        return ((delegate* unmanaged<uint, nint>)_queryInterface)(id);
    }

    static void Check(int rc, string what)
    {
        if (rc != 0) throw new InvalidOperationException($"{what} failed: {rc} {ErrorName(rc)}");
    }

    static string? ErrorName(int rc)
    {
        byte* msg = stackalloc byte[64];
        msg[0] = 0;
        ((delegate* unmanaged<int, byte*, int>)Fn(0x6C2D048C))(rc, msg);   // NvAPI_GetErrorMessage
        return Marshal.PtrToStringAnsi((nint)msg);
    }
}
