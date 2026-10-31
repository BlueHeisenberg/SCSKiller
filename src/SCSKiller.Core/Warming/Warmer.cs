using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.Warming;

/// <summary>One line of scskiller_warm.exe's stdout protocol (ARCHITECTURE.md). Unknown or non-JSON lines parse to null.
/// A "retry" event (the process's driver was poisoned by a fault or hang in ray tracing, <paramref name="Reason"/> "rt", its
/// device was removed, "removed", a pipeline's create raised an exception, "fault", creates kept hanging with the device
/// intact, "hang", the process died without a last line, "died", planned by its parent from the item states it left, or it
/// stopped itself to free memory, "memory", with <paramref name="PrivateMB"/> and <paramref name="FreeMB"/> then)
/// says where a new process goes on: <paramref name="From"/> (--start), <paramref name="RtThreads"/>
/// (--rt-threads), <paramref name="FailedItem"/> (-1 or an item that faulted alone: --skip from then on),
/// <paramref name="Skip"/> (one that died alone, at or after From: --skip from then on), <paramref name="Isolate"/> (--isolate: in flight at a removal, or hung in a hang retry).
/// <paramref name="Crashed"/> (retry and done): the record keys of the items that crash the driver, skipped or blamed by
/// that process (--skip-keys from then on). <paramref name="Stage"/> (the "stage" event, the first line): the process's own
/// staging folder, where its scskiller.log ends up. <paramref name="Workers"/> of <paramref name="Threads"/> (progress): the
/// threads its memory budget lets run now; 0 from a warm that doesn't say.
/// <paramref name="DriverSkipped"/> (progress, retry and done, counted like Failed): <see cref="WarmResult.DriverSkipped"/>.
/// <paramref name="Invalid"/> (progress and done, the process's own): <see cref="WarmResult.Invalid"/>.
/// <paramref name="Unsupported"/> (progress and done, the process's own): <see cref="WarmResult.Unsupported"/>.
/// <paramref name="Known"/> (retry and done, counted like DriverSkipped): <see cref="WarmResult.Known"/>. <paramref name="Culprits"/>
/// (retry, done and error): the record keys of the process's own items skipped for hanging or faulting the driver (--known-keys
/// on this driver from then on).</summary>
public sealed record WarmEvent(string Event, long Done, long Total, long Failed, double Rate, double Seconds, bool Stopped, string? Message,
    long From = 0, int RtThreads = 0, long FailedItem = -1, string? Reason = null, IReadOnlyList<string>? Crashed = null, IReadOnlyList<long>? Isolate = null,
    string? Stage = null, int Workers = 0, int Threads = 0, long DriverSkipped = 0, IReadOnlyList<long>? Skip = null, long Invalid = 0,
    long Unsupported = 0, bool NoFlush = false, long Work = 0, long PassedOver = -1, long Cached = 0, long Known = 0, IReadOnlyList<string>? Culprits = null,
    long PrivateMB = 0, long FreeMB = 0)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WarmEvent? Parse(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            if (JsonSerializer.Deserialize<WarmEvent>(line, Json) is not { Event: not null } e) return null;
            // null when empty: records compare lists by reference, and Pump folds equal progress lines
            return e with { Crashed = e.Crashed is { Count: > 0 } ? e.Crashed : null, Isolate = e.Isolate is { Count: > 0 } ? e.Isolate : null, Skip = e.Skip is { Count: > 0 } ? e.Skip : null,
                Culprits = e.Culprits is { Count: > 0 } ? e.Culprits : null };
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>Drives scskiller_warm.exe (staged path: the vendor cache is keyed by exe name).</summary>
public sealed class Warmer(IGpuVendorBackend vendor, string? warmExe = null) : IWarmer
{
    /// <summary>After Stop: a warm that hasn't printed its done event by then is stuck (workers deadlocked in the driver
    /// ignore the stop event) and is ended. Replaceable for tests.</summary>
    public TimeSpan StuckAfter { get; init; } = TimeSpan.FromSeconds(30);
    public const string StuckError = "the compile didn't stop cleanly (the driver may be stuck), so it was ended";
    public const string NoDoneError = "scskiller_warm exited without a done event";
    public const string DiedError = "scskiller_warm died without an exit code";
    public const string StalledError = "the compile stopped making progress (the GPU driver or an add-on of the game's layer may hang), so it was ended";
    public const string OutputHeldError = "the compile's process ended but its output stayed open (a process it started, or one the GPU driver keeps from ending, still holds it), so the compile was ended";

    /// <summary>Removed devices, faults or hangs in a row whose retry blamed no new item before a warm gives up. Replaceable for tests.</summary>
    public int MaxRecoveries { get; init; } = 20;

    /// <summary>A pass stops once this many pipelines crashed the driver and they are more than 1 in <see cref="CrashShare"/> of the
    /// items it went past. A ceiling until crash rates on real drivers are measured. The floor is replaceable for tests.</summary>
    public int CrashFloor { get; init; } = 1000;
    public const int CrashShare = 4;

    /// <summary>The error of a warm that stopped on removed devices, by the number of items it skipped for crashing the driver.</summary>
    public static string CrashedError(int skipped) => skipped == 0
        ? "the GPU driver kept crashing with no single pipeline to blame, so the compile stopped. " + GoesOn
        : skipped == 1 ? "1 pipeline that crashes the GPU driver was skipped. " + GoesOn
        : $"{skipped} pipelines that crash the GPU driver were skipped. " + GoesOn;

    public const string CrashStormError = "the GPU driver crashed on too many pipelines, so the compile stopped. " + GoesOn;
    public const string HungError = "the GPU driver kept hanging, so the compile stopped. " + GoesOn;
    public const string FaultingError = "the GPU driver kept faulting on ray tracing, so the compile stopped. " + GoesOn;
    const string GoesOn = "Compiling again continues where it stopped";

    /// <summary>A warm that gave up on the driver's recoveries (<see cref="CrashedError"/>, <see cref="HungError"/>,
    /// <see cref="FaultingError"/>): its resume point is where the driver kept failing. Not a <see cref="CrashStormError"/>,
    /// whose crashers are all blamed: the items past it were never tried.</summary>
    public static bool GaveUp(string? error) => error != CrashStormError && error?.EndsWith(GoesOn, StringComparison.Ordinal) == true;

    /// <summary>Added to scskiller_warm's environment (tests: SCSKILLER_WARM_* fault injection).</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }

    public IProgress<string>? Log { get; set; }

    /// <summary>Where the run's processes start, one host per run (null: <see cref="WarmHost.For"/>). Replaceable for tests.</summary>
    internal Func<Game, string, IWarmHost>? Host { get; init; }

    /// <summary>The Steam folder of the Steam games it warms (null: <see cref="Games.SteamSource.Root"/>).</summary>
    internal string? SteamRoot { get; init; }

    /// <summary>Why <paramref name="game"/> can't be warmed here (<see cref="WarmHost.Refusal"/>); null when it can.</summary>
    public string? Refusal(Game game, bool vulkan = false) => WarmHost.Refusal(game, SteamRoot, vulkan, MesaLinux.Is(vendor.Caps));

    /// <summary>The game's AGS registration (ScsKiller.AgsFor); null or returning null: a plain device.</summary>
    public Func<Game, AgsRegistration?>? Ags { get; set; }

    /// <summary>How the game starts NVIDIA Aftermath (<see cref="NvAftermath.Of"/>); null or returning null: the warm doesn't.</summary>
    public Func<Game, AftermathInit?>? Aftermath { get; set; }

    /// <summary>A folder holding a copy of the game's layer (ReShade's dll, its ini, the add-ons), staged next to the warm's
    /// exe (scskiller_warm --layer) so the layer changes the warm's pipelines as it changes the game's; null or returning
    /// null: none. Called with the run's work folder.</summary>
    public Func<Game, string, string?>? Layer { get; set; }

    /// <summary>The warm exe for a vendor, relative to native\: NVIDIA's runs on the segment heap, since on the NT heap its
    /// compiler threads queue on the process heap's lock. AMD's compiler is slower on the segment heap, so the others keep
    /// the NT heap. Both are named scskiller_warm.exe and stage themselves under the game's name. Off Windows a Vulkan warm
    /// runs the native scskiller_warm.</summary>
    public static string ExeFor(GpuVendor vendor, bool vulkan = false) => vulkan && !OperatingSystem.IsWindows() ? "scskiller_warm"
        : vendor == GpuVendor.Nvidia ? Path.Combine("segheap", "scskiller_warm.exe") : "scskiller_warm.exe";

    /// <summary>A Vulkan warm's environment: the game's driver-cache variables as it ran with them (the replayer inherits them,
    /// and with Steam's redirect the cache it fills is the game's), then <paramref name="env"/>.</summary>
    internal static IReadOnlyDictionary<string, string>? VulkanEnv(IReadOnlyDictionary<string, string>? env, IReadOnlyDictionary<string, string>? cache)
    {
        if (cache is not { Count: > 0 }) return env;
        var all = new Dictionary<string, string>(cache, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) all[k] = v;
        return all;
    }

    public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress) =>
        Start(game, workDir, options, progress, vendor.Gpu);

    /// <summary><paramref name="gpu"/>: the adapter the run launches on (its LUID), every process of it, as the caller
    /// records the run for. A DirectX plan's work folder with Steam's pipelines in it (<see cref="Vulkan.VulkanPlan.WorkFile"/>)
    /// replays those first (<see cref="SteamFirstRun"/>).</summary>
    public IWarmRun Start(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress, GpuInfo gpu) =>
        options.Vulkan || !File.Exists(Path.Combine(workDir, Vulkan.VulkanPlan.WorkFile)) ? StartOne(game, workDir, options, progress, gpu)
            : new SteamFirstRun((o, p) => StartOne(game, workDir, o, p, gpu), workDir, options, progress, m => Log?.Report($"{game.Name}: {m}"));

    IWarmRun StartOne(Game game, string workDir, WarmOptions options, IProgress<WarmProgress>? progress, GpuInfo gpu)
    {
        IWarmHost host;
        try { host = Host?.Invoke(game, workDir) ?? WarmHost.For(game, workDir, gpu, Log, SteamRoot, options.Vulkan, options.CacheCap, MesaLinux.Is(vendor.Caps)); }
        catch (CacheFullException e)
        {
            Log?.Report($"{game.Name}: {e.Message}: nothing more to compile");
            return new FullRun(new WarmResult(WarmOutcome.Completed, options.StartAt, options.StartAt, 0, TimeSpan.Zero, 0,
                Path.Combine(workDir, "stage", "scskiller.log"), null, CacheKeysAdded: e.Keys, Full: e.Message));
        }
        try
        {
            var own = GpuBackends.OwnDriver(vendor);
            var exe = warmExe ?? NativeTools.Find(ExeFor(own, options.Vulkan))
                      ?? throw new FileNotFoundException($"{ExeFor(own, options.Vulkan)} not found next to the app or in {Path.Combine("proxy", "build", "Release")}");
            string? stagePath = null;
            var warmWork = host.WarmPath(workDir);
            // the game's D3D layer, runtime and AGS don't apply, and NVIDIA's Vulkan cache key is the file name alone (Linux: the
            // app name). AMD's default Vulkan cache is keyed on the exe's path in part: the replayer is staged in the install's layout
            if (options.Vulkan)
            {
                if (OperatingSystem.IsWindows() && (stagePath = StagePath(game, workDir, out var vwhy)) == null)
                    Log?.Report($"{game.Name}: the compile runs without the install's folder layout: {vwhy}");
                return new WarmRun(host, vendor, gpu, exe, game, workDir, options, progress, StuckAfter, stagePath, MaxRecoveries, CrashFloor, VulkanEnv(Environment, options.CacheEnv), ["--vulkan"], Log);
            }
            if (Games.XboxSource.AppUserModelId(game) == null && (stagePath = StagePath(game, workDir, warmWork == workDir ? null : warmWork, out var why)) == null)
                Log?.Report($"{game.Name}: the compile runs without the install's folder layout: {why}");
            var reg = vendor.Vendor == GpuVendor.Amd ? Ags?.Invoke(game) : null;
            var agsDll = reg == null ? null : AmdAgs.DllFor(game, NativeTools.Find(AmdAgs.DllName));
            var ags = AgsArgs(vendor.Vendor, reg, agsDll == null ? null : host.WarmPath(agsDll), out var agsWhy);
            if (agsWhy != null) Log?.Report($"{game.Name}: {agsWhy}");
            if (Layer?.Invoke(game, workDir) is { } layer) ags = [.. ags, "--layer", host.WarmPath(layer)];
            if (AgilityDir(game.ExePath) is { } d3d12) ags = [.. ags, "--d3d12", host.WarmPath(d3d12)];
            if (OperatingSystem.IsWindows())
            {
                App.D3DSCache.SweepWarmRegistrations();
                if (options.Delivery != null && App.D3DSCache.Delivered(game.ExePath) is { } asd && asd.Stamp == options.Delivery) ags = [.. ags, .. AsdArgs(asd)];
            }
            var env = Environment;
            if (DxvkDll(own, game.ExePath, options.CacheEnv != null) is { } d3d11) (ags, env) = ([.. ags, "--d3d11", host.WarmPath(d3d11)], VulkanEnv(Environment, options.CacheEnv));
            if (own == GpuVendor.Nvidia && Aftermath?.Invoke(game) is { } am)
                ags = [.. ags, .. AftermathArgs(own, am, NvAftermath.DllFor(game) is { } amDll ? host.WarmPath(amDll) : null)];
            // read here: a packaged (Xbox) child doesn't get this process's environment
            if (options.D3DSCacheNoFlush && System.Environment.GetEnvironmentVariable("SCSKILLER_WARM_NO_FLUSH_HOOK") is { Length: > 0 })
                Log?.Report($"{game.Name}: D3DSCache flushes stay on, so the compile runs at normal speed: turned off (SCSKILLER_WARM_NO_FLUSH_HOOK)");
            else if (options.D3DSCacheNoFlush) ags = [.. ags, "--d3dscache-noflush", "1"];
            return new WarmRun(host, vendor, gpu, exe, game, workDir, options, progress, StuckAfter, stagePath, MaxRecoveries, CrashFloor, env, ags, Log);
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }

    /// <summary>scskiller_warm's --ags arguments: on AMD, for a game that registers with AGS (an Xbox app game holds its
    /// app name's key too, measured), the child creates its device through <paramref name="agsDll"/> under the
    /// game's app and engine names and versions, so the driver keys its cache like the game's. Empty otherwise, with the reason when a
    /// registration can't be passed.</summary>
    public static string[] AgsArgs(GpuVendor vendor, AgsRegistration? reg, string? agsDll, out string? why)
    {
        why = null;
        if (vendor != GpuVendor.Amd || reg == null) return [];
        if (agsDll == null)
        {
            why = $"{AmdAgs.DllName} not found next to the app: the compile fills the exe name's cache, not the one of the game's AGS app name {reg.App}";
            return [];
        }
        return ["--ags", agsDll, "--ags-app", reg.App, "--ags-engine", reg.Engine, "--ags-version", $"{reg.AppVersion:x},{reg.EngineVersion:x}"];
    }

    /// <summary>scskiller_warm's --asd-* arguments: the game's Advanced Shader Delivery, which the warm registers for its staged
    /// exe while it runs, so the driver compiles only what the delivery doesn't serve. Empty when it delivers no db.</summary>
    public static string[] AsdArgs(App.D3DSCache.Delivery d) => d.Psdbs.Count == 0 || d.Families is not { } f || f.Count != d.Psdbs.Count ? []
        : ["--asd-name", d.Name, "--asd-engine", d.Engine, "--asd-version", $"{d.Version},{d.EngineVersion}",
            .. d.Psdbs.Zip(f).SelectMany(p => new[] { "--asd-psdb", $"{p.Second}={p.First}" })];

    /// <summary>scskiller_warm's --aftermath arguments: on NVIDIA, for a game that starts Aftermath in a way that changes the
    /// driver's key, the child starts it the same way through the game's own <paramref name="dll"/>. Empty otherwise.</summary>
    public static string[] AftermathArgs(GpuVendor vendor, AftermathInit? init, string? dll) =>
        vendor == GpuVendor.Nvidia && init is { ChangesKey: true } && dll != null ? ["--aftermath", dll, "--aftermath-init", init.Arg] : [];

    /// <summary>scskiller_warm's --d3d11: the game's d3d11.dll when it is DXVK's, on NVIDIA, whose Vulkan cache is keyed on
    /// the exe name, on AMD when the warm runs under Steam's redirect (<paramref name="steam"/>, keyed on the app: AMD's own
    /// VkCache is keyed on the full exe path, which the staged warm doesn't share), and under Proton on Mesa too, whose cache
    /// is keyed on content; null otherwise.</summary>
    public static string? DxvkDll(GpuVendor vendor, string exePath, bool steam = false)
    {
        try
        {
            var dll = Games.GameFiles.Find(Path.GetDirectoryName(Path.GetFullPath(exePath))!, App.ScsKiller.Dll11);
            return (vendor == GpuVendor.Nvidia || vendor == GpuVendor.Amd && steam || !OperatingSystem.IsWindows()) && Games.ReShade.IsDxvk(dll) ? dll : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    /// <summary>scskiller_warm's --d3d12: the folder of the Agility SDK runtime the game's exe asks for (its D3D12SDKPath
    /// export, relative to the exe, else its D3D12 or D3D12\x64 folder), when it holds a D3D12Core.dll; null otherwise.
    /// The warm then runs on that runtime, as the game does: where the system's is older (Windows 10), it rejects what the
    /// game recorded on the newer one.</summary>
    public static string? AgilityDir(string exePath)
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Path.GetFullPath(exePath))!;
            string? sdkPath = null;
            try
            {
                using var pe = Carved.PeFile.Open(exePath);
                sdkPath = Carved.PeFile.ExportedString(pe, "D3D12SDKPath");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException) { }
            // a protected or packed exe's exports can't be read from the file: the folder the Agility SDK installs to, or
            // its x64 subfolder (Fortnite's)
            var d3d12 = Games.GameFiles.Find(exeDir, "D3D12");
            var dir = !string.IsNullOrEmpty(sdkPath) ? AgilityFolder(exeDir, sdkPath)
                : File.Exists(Games.GameFiles.Find(d3d12, "D3D12Core.dll")) ? d3d12 : Games.GameFiles.Find(d3d12, "x64");
            return dir != null && File.Exists(Games.GameFiles.Find(dir, "D3D12Core.dll")) ? dir : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    /// <summary>The folder a D3D12SDKPath export names: a relative path that stays inside the exe's folder, as the D3D12
    /// loader takes it; null for anything else (an absolute path, a drive, a ".." out of it).</summary>
    public static string? AgilityFolder(string exeDir, string? sdkPath)
    {
        if (string.IsNullOrEmpty(sdkPath) || Path.IsPathRooted(sdkPath) || sdkPath.Contains(':')) return null;
        var dir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Games.GameFiles.Find(exeDir, sdkPath)));
        var rel = Path.GetRelativePath(exeDir, dir);
        return rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(rel) ? null : dir;
    }

    /// <summary>The D3D12SDKVersion export of the D3D12Core.dll in <paramref name="dir"/> (<see cref="AgilityDir"/>), as
    /// scskiller_warm reads it; 0 when it can't be read.</summary>
    public static int AgilitySdk(string dir)
    {
        try
        {
            using var pe = Carved.PeFile.Open(Path.Combine(dir, "D3D12Core.dll"));
            return Carved.PeFile.ExportData(pe, "D3D12SDKVersion") is { RemainingBytes: >= 4 } v ? v.ReadInt32() : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or BadImageFormatException or ArgumentException) { return 0; }
    }

    /// <summary>The newest shader model the runtime the warm loads compiles, on Windows build <paramref name="osBuild"/> with the
    /// game's runtime of that D3D12SDKVersion (null: none, the system's runs, 0: unreadable); null when that isn't known.
    /// The loader takes the newer of the two, so only Windows 10 (19041 and later) pins it.</summary>
    public static Version? RuntimeShaderModel(int osBuild, int? agilitySdk) => osBuild is < 19041 or >= 22000 ? null : agilitySdk switch
    {
        null => new(6, 5),
        // Agility SDK release notes: 1.4 brought Shader Model 6.6, 1.606 6.7, 1.613 6.8 (1.618's isn't stated)
        4 or (>= 600 and < 606) => new(6, 6),
        >= 606 and < 613 => new(6, 7),
        >= 613 and < 617 => new(6, 8),
        _ => null,
    };

    /// <summary>The game page's and queue's warning that the runtime the warm loads can't compile the plan's newest shaders
    /// (<see cref="PlanStats.MaxShaderModel"/>); null when it can or that isn't known.</summary>
    public static string? RuntimeWarning(string? planShaderModel, int osBuild, int? agilitySdk) =>
        !Version.TryParse(planShaderModel, out var need) || RuntimeShaderModel(osBuild, agilitySdk) is not { } have || need <= have ? null
        : agilitySdk == null ? $"Windows 10's DirectX 12 can't compile this game's Shader Model {need} shaders, so pipelines that use them will fail. Updating to Windows 11 can fix it."
        : $"The game's own DirectX 12 runtime can't compile its Shader Model {need} shaders, so pipelines that use them will fail. Updating the game or Windows can fix it.";

    public const string NoDx12Warning = "This GPU doesn't support DirectX 12, so this game's shaders can't be compiled on it.";

    /// <summary><see cref="RuntimeWarning(string?, int, int?)"/> for the runtime the warm would load for this exe. Its files are
    /// read only when the system's runtime alone would warn: a game's runtime only ever raises the shader model.</summary>
    public static string? RuntimeWarning(string? planShaderModel, string exePath, int osBuild) =>
        RuntimeWarning(planShaderModel, osBuild, null) == null ? null
        : RuntimeWarning(planShaderModel, osBuild, AgilityDir(exePath) is { } dir ? AgilitySdk(dir) : null);

    const int MaxPath = 260;
    const string LongestStagedName = "scskiller_warm_times.csv";
    const string LongestStageDir = "stage-4294967295-99";   // scskiller_warm's staging folder: stage-<pid>-<n>

    /// <summary>scskiller_warm's --stage-path: the install folder's name, the exe's folder inside the install, the exe's
    /// file name (AMD app profiles such as Hogwarts Legacy's match the launched path's tail, e.g.
    /// "Hogwarts Legacy\Phoenix\Binaries\Win64\HogwartsLegacy.exe"). Null, with the reason, for an exe outside the install
    /// or a staged path that would reach MAX_PATH (scskiller_warm then stages the exe alone).</summary>
    public static string? StagePath(Game game, string workDir, out string? why) => StagePath(game, workDir, null, out why);

    /// <param name="warmWork">the work folder as a warm under Wine opens it (<see cref="IWarmHost.WarmPath"/>, a Windows path):
    /// MAX_PATH then measures that form, and the stage path comes in it too</param>
    public static string? StagePath(Game game, string workDir, string? warmWork, out string? why)
    {
        var install = Path.TrimEndingDirectorySeparator(Path.GetFullPath(game.InstallDir));
        var rel = Path.GetRelativePath(install, Path.GetFullPath(game.ExePath));
        why = Path.GetFileName(install).Length == 0 ? $"the install folder {game.InstallDir} is a drive root"
            : Path.IsPathRooted(rel) || rel == "." || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? $"{game.ExePath} is outside {game.InstallDir}"
            : null;
        if (why != null) return null;
        var path = Path.Combine(Path.GetFileName(install), rel);
        var dir = warmWork == null ? Path.GetFullPath(Path.Combine(workDir, LongestStageDir, Path.GetDirectoryName(path)!))
            : string.Join('\\', warmWork.TrimEnd('\\'), LongestStageDir, Path.GetDirectoryName(path)!.Replace('/', '\\'));
        if (dir.Length + 1 + Math.Max(Path.GetFileName(path).Length, LongestStagedName.Length) < MaxPath) return warmWork == null ? path : path.Replace('/', '\\');
        why = $"the staged path under {workDir} would be longer than {MaxPath - 1} characters";
        return null;
    }
}

/// <summary>A DirectX warm whose work folder holds Steam's pipelines of the game (<see cref="Vulkan.VulkanPlan.WorkFile"/>, a
/// game under Proton): the native Vulkan replay of those, then the DirectX warm, as one run whose items are the replay's,
/// then the warm's. A stop past the replay's resumes in the warm alone, one in it replays them all again. A replay that
/// fails as a whole skips its pipelines for this compile (none counts as created), never the DirectX warm.</summary>
sealed class SteamFirstRun : IWarmRun
{
    public const string Note = "compiling Steam's pipelines of the game first";
    const string FailedDir = "steam-replay";

    readonly Lock _gate = new();
    IWarmRun? _now;
    bool _stopped, _paused;

    public Task<WarmResult> Completion { get; }

    public SteamFirstRun(Func<WarmOptions, IProgress<WarmProgress>?, IWarmRun> start, string workDir, WarmOptions options, IProgress<WarmProgress>? progress, Action<string> log)
    {
        var foz = Path.Combine(workDir, Vulkan.VulkanPlan.WorkFile);
        var names = Vulkan.Foz.List(foz).Where(e => Vulkan.Foz.IsPipeline(e.Tag)).Select(e => e.Name).ToList();
        Completion = Run();

        // a stop before the DirectX warm counts its items too: the whole plan's total, as the warm would count it
        long WarmItems()
        {
            try { return WarmPasses.ItemSets(workDir).Sets.Count; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        }

        async Task<WarmResult> Run()
        {
            long steam = names.Count;
            WarmResult? first = null;
            if (options.StartAt < steam)
            {
                if (Directory.Exists(Path.Combine(workDir, FailedDir))) Directory.Delete(Path.Combine(workDir, FailedDir), true);   // an earlier compile's: this one replays them again
                try { first = await Phase(start(options with { Vulkan = true, StartAt = 0 }, progress == null ? null : new Relay(p => progress.Report(p with { Note = p.Note ?? Note })))); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or NotSupportedException or Win32Exception)
                {
                    first = new WarmResult(WarmOutcome.Failed, 0, steam, 0, TimeSpan.Zero, 0, Path.Combine(workDir, "stage", "scskiller.log"), e.Message);
                }
                if (first.Outcome == WarmOutcome.Stopped) return first with { Total = first.Total + WarmItems() };
                if (first.Outcome == WarmOutcome.Failed)
                {
                    log($"Steam's pipelines of the game weren't compiled, the rest goes on: {first.Error}");
                    var dir = Path.Combine(workDir, FailedDir);
                    Directory.CreateDirectory(dir);
                    File.WriteAllBytes(Path.Combine(dir, App.Recordings.FailedFile), [.. names.SelectMany(n => Convert.FromHexString(Vulkan.VulkanPlan.Key(n)))]);
                    first = first with { DriverSkipped = first.DriverSkipped + Math.Max(0, steam - first.Done) };
                }
                lock (_gate)
                    if (_stopped) return first with { Outcome = WarmOutcome.Stopped, Done = steam, Total = steam + WarmItems(), Resumable = true };
            }
            var (failed, skipped, invalid, unsupported) = (first?.Failed ?? 0, first?.DriverSkipped ?? 0, first?.Invalid ?? 0, first?.Unsupported ?? 0);
            var cached = first?.Cached ?? 0;
            var rest = await Phase(start(options with { StartAt = Math.Max(0, options.StartAt - steam) }, progress == null ? null
                : new Relay(p => progress.Report(p with { Done = p.Done + steam, Total = p.Total + steam, Failed = p.Failed + failed, DriverSkipped = p.DriverSkipped + skipped,
                    Invalid = p.Invalid + invalid, Unsupported = p.Unsupported + unsupported }))));
            return rest with
            {
                Done = rest.Done + steam, Total = rest.Total + steam, Failed = rest.Failed + failed, DriverSkipped = rest.DriverSkipped + skipped,
                Invalid = rest.Invalid + invalid, Unsupported = rest.Unsupported + unsupported, Cached = rest.Cached + cached,
                Elapsed = rest.Elapsed + (first?.Elapsed ?? TimeSpan.Zero), CacheGrowthBytes = rest.CacheGrowthBytes + (first?.CacheGrowthBytes ?? 0),
                CacheKeysAdded = first?.CacheKeysAdded is { } added ? [.. added, .. rest.CacheKeysAdded ?? []] : rest.CacheKeysAdded, Full = rest.Full ?? first?.Full,
            };
        }
    }

    /// <summary>Steam's pipelines in the work folder (<see cref="Vulkan.VulkanPlan.WorkFile"/>), which the replay creates first.</summary>
    internal static long Pipelines(string workDir)
    {
        var foz = Path.Combine(workDir, Vulkan.VulkanPlan.WorkFile);
        return File.Exists(foz) ? Vulkan.Foz.List(foz).Count(e => Vulkan.Foz.IsPipeline(e.Tag)) : 0;
    }

    Task<WarmResult> Phase(IWarmRun run)
    {
        lock (_gate)
        {
            _now = run;
            if (_stopped) run.Stop();
            else if (_paused) run.Pause();
        }
        return run.Completion;
    }

    public void Pause() { lock (_gate) { _paused = true; _now?.Pause(); } }
    public void Resume() { lock (_gate) { _paused = false; _now?.Resume(); } }
    public void Stop() { lock (_gate) { _stopped = true; _now?.Stop(); } }

    sealed class Relay(Action<WarmProgress> report) : IProgress<WarmProgress>
    {
        public void Report(WarmProgress value) => report(value);
    }
}

/// <summary>A warm whose host had no room for its first process (<see cref="CacheFullException"/>): over before it starts.</summary>
sealed class FullRun(WarmResult result) : IWarmRun
{
    public Task<WarmResult> Completion { get; } = Task.FromResult(result);
    public void Pause() { }
    public void Resume() { }
    public void Stop() { }
}

/// <summary>One warm, possibly several scskiller_warm processes: a process whose driver was poisoned by a fault or hang in
/// ray tracing ends with a "retry" event, and a new one goes on from its first unfinished item with the same ray tracing
/// threads, the state object that faulted or hung skipped (a quarter of the threads when that item is unknown, or after
/// <see cref="WarmRun.MaxKnownSkips"/> such skips). Failed counts add up across them; the progress note says what the retry
/// did meanwhile. A process whose device was removed, or whose replay of a pipeline faulted, ends with a "removed" or "fault"
/// retry: the next one creates the items that were in flight alone first and skips the ones that crash the driver (by record
/// key). A process whose creates kept hanging ends with a "hang" retry: the next one creates the hung items alone first, and
/// one that hangs again alone is skipped and remembered (WarmEvent.Culprits). Both go on until MaxRecoveries such retries in a row blame no new item, or the first MaxRecoveries + 1 processes
/// created nothing (a driver that crashes on everything), per pass of a careful warm. A work folder
/// with a pass file (<see cref="WarmPasses"/>) runs one process per pass, in order,
/// with the crash keys found so far; Done and StartAt then count the whole warm's items
/// (<see cref="WarmPasses.Overall"/>), so a stopped careful warm resumes in its pass. A process started with --aftermath that
/// fails or dies before its first item (the game's Aftermath may fault or hang there) is run again without it, once, from the
/// warm's first item (<see cref="WarmResult.AftermathDropped"/>).</summary>
sealed class WarmRun : IWarmRun
{
    static readonly TimeSpan CacheSampleInterval = TimeSpan.FromSeconds(5), ReportEvery = TimeSpan.FromMilliseconds(250), OutputHeldAfter = TimeSpan.FromSeconds(30);
    // The proxy gives up on an item after 60 s (a state object 60 s + 250 ms per collection it links) and counts it skipped,
    // so a live D3D warm moves at least that often. ponytail: fixed, a state object linking over 2,000 collections may exceed it
    static readonly TimeSpan StalledAfter = TimeSpan.FromSeconds(int.TryParse(Environment.GetEnvironmentVariable("SCSKILLER_TEST_STALLED_S"), out var s) ? s : 600);
    // Known state objects skipped in one compile before the retries lower the ray tracing threads too (a crash storm).
    const int MaxKnownSkips = 8;
    const string StuckVariable = "SCSKILLER_WARM_STUCK_S";
    // under StalledAfter: an item given up on still moves the warm before the app calls it stalled
    const int MaxStuckS = 300;
    // "memory" retries in one run: a restart every few hours on a 10 h compile (Marvel Rivals grew 2.4 GB/h), each needs
    // the PC low on memory for 10 min after a third of the budget's growth, so it can't loop fast.
    const int MaxMemoryRestarts = 8;
    IWarmProcess _p = null!;
    readonly IWarmHost _host;
    readonly object _lock = new();
    readonly TimeSpan _stuckAfter;
    volatile TaskCompletionSource _doneSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);   // the final done event, or a stopping one
    public Task LastDone => _doneSeen.Task;
    bool _paused, _stopping;
    long _resumed;   // when the last Resume ran (Stopwatch timestamp)
    volatile string? _ended;   // why Stop had to end the process
    volatile bool _retried;    // the current process printed its retry: it may be writing the driver cache, as after a done event

    public Task<WarmResult> Completion { get; }

    public WarmRun(IWarmHost host, IGpuVendorBackend vendor, GpuInfo gpu, string exe, Game game, string workDir, WarmOptions o, IProgress<WarmProgress>? progress, TimeSpan stuckAfter,
        string? stagePath, int maxRecoveries, int crashFloor, IReadOnlyDictionary<string, string>? env, string[]? extra = null, IProgress<string>? log = null)
    {
        _host = host;
        _stuckAfter = stuckAfter;
        // a Vulkan warm writes the driver's Vulkan cache: under a redirect (Steam's shader pre-caching) there, else in the vendor's own folder
        var redirect = OperatingSystem.IsWindows() && o.Vulkan && (Vulkan.VulkanPlan.CachePath(env, vendor.Vendor) ?? Vulkan.VulkanPlan.DefaultCache(vendor.Vendor)) is { } path
            && Directory.Exists(path) ? path : null;
        long Usage()
        {
            if (redirect == null) return vendor.GetCacheUsage().BytesOnDisk;
            try { return new DirectoryInfo(redirect).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0; }
        }
        long before = Usage();
        var clock = Stopwatch.StartNew();
        var stderr = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var skipKeys = new HashSet<string>(o.SkipKeys ?? [], StringComparer.OrdinalIgnoreCase);
        var passes = WarmPasses.Read(workDir);
        var (pass, from) = passes?.Locate(o.StartAt) ?? (0, o.StartAt);
        var rt = 0;   // the ray tracing threads of the last process
        IReadOnlyList<long> isolated = [];   // the items it created alone first
        var memoryRestarts = 0;
        // the proxy's per-item limit: hung items that compile alone were slow on many threads (Phantom Blade Zero on NVIDIA)
        var stuckS = int.TryParse(System.Environment.GetEnvironmentVariable(StuckVariable), out var s0) && s0 > 0 ? s0 : 60;
        long Whole(long done) => passes?.Overall(pass, done) ?? done;
        IWarmProcess Launch(long start, int rtThreads, List<long> skip, IReadOnlyList<long> isolate)
        {
            (from, rt, isolated) = (start, rtThreads, isolate);
            _retried = false;
            var threads = passes != null && !passes.IsFast(pass) && o.CarefulThreads > 0 ? o.CarefulThreads : o.Threads;
            List<string> args = [host.WarmPath(workDir), Path.GetFileName(game.ExePath), "--threads", threads.ToString(),
                "--priority", o.Priority == WarmPriority.Idle ? "idle" : "below", "--start", start.ToString(),
                .. host.StopArgs, .. host.AdapterArgs(gpu)];
            if (o.MemoryMB > 0) { args.Add("--memory-mb"); args.Add(o.MemoryMB.ToString()); }
            if (o.MemoryMB > 0 && !o.Vulkan && memoryRestarts < MaxMemoryRestarts) { args.Add("--memory-restart"); args.Add("1"); }   // the D3D12 proxy's, the Linux Vulkan warmer takes no such option
            if (rtThreads > 0) { args.Add("--rt-threads"); args.Add(rtThreads.ToString()); }
            if (skip.Count > 0) { args.Add("--skip"); args.Add(string.Join(',', skip)); }
            if (stagePath != null) { args.Add("--stage-path"); args.Add(stagePath); }
            args.AddRange(extra ?? []);
            if (vendor.Caps.PackageKeyed && !o.Vulkan && Games.XboxSource.AppUserModelId(game) is { } app) { args.Add("--package"); args.Add(app); }
            void Keys(string option, IEnumerable<string> set)
            {
                var keys = string.Join(',', set.Order(StringComparer.Ordinal));
                if (keys.Length == 0) return;
                if (keys.Length > 8000)   // a command line holds 32,767 characters: a long list goes through a file
                {
                    var file = Path.GetFullPath(Path.Combine(workDir, option[2..] + ".txt"));
                    File.WriteAllText(file, keys);
                    keys = "@" + host.WarmPath(file);
                }
                args.Add(option);
                args.Add(keys);
            }
            Keys("--skip-keys", skipKeys);
            if (!o.Vulkan) Keys("--known-keys", o.KnownKeys ?? []);
            if (isolate.Count > 0) { args.Add("--isolate"); args.Add(string.Join(',', isolate)); }
            if (passes != null) { args.Add("--pass"); args.Add(passes.Number(pass).ToString()); }
            var all = new Dictionary<string, string>(env ?? new Dictionary<string, string>()) { [StuckVariable] = stuckS.ToString() };
            return host.Start(exe, args, all,
                l => { stderr.Enqueue(l); log?.Report($"{game.Name}: compile: {l}"); });
        }
        _p = Launch(from, 0, [], []);
        Completion = Task.Run(async () =>
        {
            try { return await Run(); }
            catch
            {
                IWarmProcess p;
                lock (_lock) p = _p;
                try { p.Kill(); }
                finally { _host.Dispose(); }
                throw;
            }
        });

        async Task<WarmResult> Run()
        {
            Stopwatch? sampled = null;
            long growth = 0;
            long Growth()   // a directory walk for NVIDIA: not on every report
            {
                if (sampled == null || sampled.Elapsed >= CacheSampleInterval) (growth, sampled) = (Math.Max(0, Usage() - before), Stopwatch.StartNew());
                return growth;
            }
            long carried = 0, carriedSkips = 0, carriedKnown = 0;   // failures and skips the earlier processes counted (before their retry point, or in earlier passes)
            var culprits = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var diedAlone = new List<long>();   // items whose process died creating them alone: no process left to name their keys
            var unnamed = false;   // a process died: what it didn't create is in no failed-keys file
            var lastCarried = false;   // the last process's failures are in carried already
            string? note = null;
            long noteTill = long.MaxValue;   // a crash or hang retry's note holds while the next process is still at its retry point
            long total = 0;   // the last progress event's: a process that ends before its first one reports none
            var skip = new List<long>();
            var crashed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var added = new HashSet<string>(StringComparer.Ordinal);
            int removals = 0, knownSkips = 0;
            long passed = 0;          // the items this pass's processes went past
            long cached = 0;          // what the processes created without a compile stored (WarmResult.Cached)
            long passedOver = 0;      // of them, the ones passed over without a create (an earlier compile's crash key, another pass's)
            long crashers = 0;        // of them, the ones this pass blamed or skipped for crashing the driver
            bool created = false;     // a process of this pass created pipelines
            WarmEvent? last, done;
            string? error;
            int? code;
            bool betweenPasses = false;   // stopped after a pass ended: the next one resumes from its first item
            bool resumable = false;       // gave up at a clean point: the driver's recoveries, or the host's halt
            bool dropped = false;         // run again without --aftermath
            string? full = null;          // the host's halt for want of room: what ran is the compile
            string? stage = null;         // the last process's staging folder (its stage event)
            for (var n = 1; ; n++)
            {
                var passNote = passes == null ? null : passes.IsFast(pass) ? "careful compile: the plan's other pipelines at full speed"
                    : $"careful compile: recorded pipelines, pass {passes.Number(pass)} of {passes.CarefulCount}";
                var report = progress == null ? null : new Adjusted(progress, carried, carriedSkips, note, noteTill, passNote, passes?.Total);
                var lastPass = passes == null || pass == passes.Count - 1;
                stage = null;   // this process's, never an earlier one's
                (last, done, error) = await WarmOutput.Pump(_p.Output, report, Growth, ReportEvery,
                    () => { if (lastPass || _stopping) _doneSeen.TrySetResult(); }, Whole, s => stage = host.HostPath(s), _p.Exited, OutputHeldAfter,
                    o.Vulkan ? TimeSpan.Zero : StalledAfter, () => _paused || _stopping, () => Volatile.Read(ref _resumed), c => culprits.UnionWith(c), () => _retried = true);
                if (error == Warmer.StalledError)
                {
                    _ended = error;
                    _p.Kill();
                }
                if (last is { Total: > 0 }) total = last.Total;
                code = await _p.Exited;
                var end = await host.AfterProcess(n => progress?.Report(new WarmProgress(Whole(last?.Done ?? from), passes?.Total ?? total,
                    carried + (last?.Failed ?? 0), 0, Growth(), Note: n, DriverSkipped: carriedSkips + (last?.DriverSkipped ?? 0), Invalid: last?.Invalid ?? 0,
                    Unsupported: last?.Unsupported ?? 0)));
                added.UnionWith(end.Added);
                lastCarried = false;
                crashed.UnionWith(done?.Crashed ?? []);
                if (done is { Event: "retry", Reason: "died" })
                {
                    unnamed = true;
                    diedAlone.AddRange(done.Skip ?? []);
                }
                cached += last?.Cached ?? 0;
                bool stopping;
                lock (_lock) stopping = _stopping;
                var clean = error == null && (done is { Event: "retry" } || code == 0 && done != null);   // the next compile may go on from it
                // a full folder ends the compile even when a stop came meanwhile: no later process would fit
                if (end.Halt is { } halt && (!stopping || end.Full && clean) && !(clean && done is { Event: "done", Stopped: false } && lastPass))
                {
                    if (end.Full && clean) full = halt;
                    else (error, resumable) = (error ?? halt, clean);
                    break;
                }
                if (end.Segment && clean && done is { Event: "done", Stopped: true } segment)
                {
                    lock (_lock)
                    {
                        if (_stopping) break;
                        (carried, carriedSkips, carriedKnown, lastCarried, note) = (carried + segment.Failed, carriedSkips + segment.DriverSkipped, carriedKnown + segment.Known, true, null);
                        (passed, passedOver) = (passed + segment.Done - from, passedOver + Math.Max(0, segment.PassedOver));
                        _doneSeen = new(TaskCreationOptions.RunContinuationsAsynchronously);   // the stopped one's was no final done
                        _p = Launch(segment.Done, rt, skip, []);
                    }
                    continue;
                }
                if (error == null && code == 0 && done is { Event: "done", Stopped: false } && !lastPass)
                {
                    (carried, carriedSkips, carriedKnown, lastCarried) = (carried + done.Failed, carriedSkips + done.DriverSkipped, carriedKnown + done.Known, true);
                    lock (_lock)
                    {
                        if (_stopping) { betweenPasses = true; break; }
                        (pass, n, note, removals, created, passed, passedOver, crashers) = (pass + 1, 0, null, 0, false, 0, 0, 0);   // each pass gets its own recoveries
                        skip.Clear();
                        skipKeys.UnionWith(crashed);
                        _p = Launch(0, 0, skip, []);
                    }
                    continue;
                }
                var am = Array.IndexOf(extra ?? [], "--aftermath");
                if (am >= 0 && !_stopping && _ended == null && (error != null || done is { Event: "retry", Reason: "died", Isolate: null, Skip: null }) && (last?.Done ?? from) <= from)
                {
                    log?.Report($"{game.Name}: the compile stopped while starting NVIDIA Aftermath ({error ?? "the warm process died"}): compiling without it");
                    lock (_lock)
                    {
                        (extra, dropped, error) = ([.. extra![..am], .. extra[(am + 4)..]], true, null);   // AftermathArgs: the option, its DLL, --aftermath-init, its values
                        // what ran before filled the Aftermath key only: the plain key starts from the first item
                        (pass, n, carried, carriedSkips, carriedKnown, lastCarried, removals, knownSkips, note, created, passed, passedOver, crashers) = (0, 0, 0, 0, 0, false, 0, 0, null, false, 0, 0, 0);
                        skip.Clear();
                        _p = Launch(0, 0, skip, []);
                    }
                    continue;
                }
                if (done is not { Event: "retry" } r || error != null) break;
                (carried, carriedSkips, carriedKnown, lastCarried) = (carried + r.Failed, carriedSkips + r.DriverSkipped, carriedKnown + r.Known, true);
                var skipped = skip.Count;
                if (r.FailedItem >= 0) skip.Add(r.FailedItem);
                skip.AddRange(r.Skip ?? []);
                var blamed = skipKeys.Count;
                // crashed in this compile, not in an earlier one
                var freshKeys = (r.Crashed ?? []).Distinct(StringComparer.OrdinalIgnoreCase).Count(k => !skipKeys.Contains(k));
                // a state object that faulted and removed the device is the failed item and a blamed key too: counted once
                var fresh = freshKeys + skip.Skip(skipped).Distinct().Count(i => !(i == r.FailedItem && freshKeys > 0));
                skipKeys.UnionWith(r.Crashed ?? []);   // a retry also lists the known keys it skipped
                (passed, passedOver) = (passed + r.From - from, passedOver + Math.Max(0, r.PassedOver));
                // an item in flight at the last removal was created alone (not just the first one blamed), or the processes went
                // past more items than they passed over without a create and the items skipped before
                created |= isolated.Count > 0 && !(skip.Count + skipKeys.Count > skipped + blamed && r.From <= isolated[0] && (r.Isolate ?? []).SequenceEqual(isolated.Skip(1)))
                    || passed > (r.PassedOver >= 0 ? passedOver : (r.Crashed?.Count ?? 0) - (skipKeys.Count - blamed)) + skipped;
                var removed = r.Reason is "removed" or "fault" or "died";
                var hung = r.Reason == "hang";
                var freed = r.Reason == "memory";
                if (freed) n--;   // not a recovery: MaxMemoryRestarts bounds these, the recovery guards below count the others
                if (removed) crashers += fresh;
                var rtThreads = r.RtThreads;
                if (r.FailedItem >= 0 && ++knownSkips > MaxKnownSkips) rtThreads = Math.Max(1, rtThreads / 4);
                lock (_lock)
                {
                    // an item that died alone is skipped for this run only: no key, yet progress all the same
                    if (removed || hung) removals = skipKeys.Count > blamed || r.Culprits is { Count: > 0 } || r.Reason == "died" && r.Skip is { Count: > 0 } ? 0 : removals + 1;
                    // an item that crashes the driver costs at most two processes: the removal, then the one that blames it alone
                    var storm = removed && crashers >= crashFloor && crashers * Warmer.CrashShare > passed;
                    if (_stopping || n > 2 * r.Total + maxRecoveries || removals > maxRecoveries || !created && n > maxRecoveries || storm)
                    {
                        error = _stopping ? null
                            : storm ? Warmer.CrashStormError
                            : removed ? Warmer.CrashedError(crashed.Count)
                            : hung ? Warmer.HungError
                            : Warmer.FaultingError;
                        resumable = !_stopping;
                        break;
                    }
                    if (freed)
                    {
                        memoryRestarts++;
                        log?.Report($"{game.Name}: compile: restarted to free memory (private memory {r.PrivateMB:N0} MB, {r.FreeMB:N0} MB of RAM free), going on from item {Whole(r.From):N0}"
                            + (memoryRestarts == MaxMemoryRestarts ? $", the last of {MaxMemoryRestarts} restarts this compile may make" : ""));
                    }
                    note = removed ? "recovering from a GPU driver crash" + (crashed.Count > 0 ? $", {crashed.Count:N0} skipped so far" : "")
                        : hung ? "retrying the pipelines that hung the GPU driver, each alone"
                        : freed ? "restarted to free memory"
                        : r.FailedItem >= 0 ? $"skipping a ray tracing state object that crashed ({rtThreads} {(rtThreads == 1 ? "thread" : "threads")})"
                        : $"retrying ray tracing with fewer threads ({rtThreads})";
                    if (hung && isolated.Count > 0 && freshKeys == 0 && r.Culprits is not { Count: > 0 } && stuckS < MaxStuckS)
                    {
                        stuckS = Math.Min(stuckS * 2, MaxStuckS);
                        log?.Report($"{game.Name}: compile: the pipelines that hung on many threads compiled alone, so each gets {stuckS} s from now");
                    }
                    noteTill = removed || hung || freed ? Whole(r.From) : long.MaxValue;
                    progress?.Report(new WarmProgress(Whole(r.Done), passes?.Total ?? r.Total, carried, 0, Growth(), Note: note, DriverSkipped: carriedSkips));
                    _p = Launch(r.From, rtThreads, skip, removed || hung ? r.Isolate ?? [] : []);
                }
            }
            host.Dispose();
            growth = Math.Max(0, Usage() - before);   // after exit: the driver writes its cache then
            // stopped between processes: resumes from the retry point, or from the next pass
            var stopped = done is { Event: "retry" } && _stopping || betweenPasses;
            var outcome = full != null ? WarmOutcome.Completed
                : error == null && (stopped || code == 0 && done != null) ? stopped || done!.Stopped ? WarmOutcome.Stopped : WarmOutcome.Completed : WarmOutcome.Failed;
            var failed = carried + (lastCarried ? 0 : last?.Failed ?? 0);
            var skips = carriedSkips + (lastCarried ? 0 : last?.DriverSkipped ?? 0);
            var known = carriedKnown + (lastCarried || _ended != null ? 0 : done?.Known ?? 0);
            if (diedAlone.Count > 0)
            {
                try { culprits.UnionWith(WarmPasses.KeysOf(workDir, diedAlone)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Report($"{game.Name}: the pipelines its compile died on alone can't be named ({e.Message}): they aren't remembered"); }
            }
            var reached = last?.Done ?? from;
            if (_ended != null)
            {
                (outcome, error, resumable) = (WarmOutcome.Failed, _ended, true);
                // NVIDIA from 621 keeps what it created in the staged D3DSCache dbs, which outlive the kill and whose fold checks
                // them: the ended process's items below its first unfinished one stay done. Elsewhere the driver writes its cache
                // at exit, so the next compile goes on from where that process started.
                var (at, f, s) = !o.D3DSCacheNoFlush || o.Vulkan ? (from, lastCarried ? -(done?.Failed ?? 0) : 0, lastCarried ? -(done?.DriverSkipped ?? 0) : 0)
                    : done switch
                    {
                        { Event: "done" } d => (d.Done, d.Failed, d.DriverSkipped),
                        { Event: "retry" } r => lastCarried ? (r.From, 0, 0) : (r.From, r.Failed, r.DriverSkipped),
                        _ => Finished(stage, from) ?? (from, 0, 0),
                    };
                (reached, failed, skips) = (at, carried + f, carriedSkips + s);
            }
            if (outcome == WarmOutcome.Failed)
            {
                var tail = stderr.IsEmpty ? "" : ": " + string.Join(" | ", stderr.TakeLast(3));
                error ??= code == null ? Warmer.DiedError + tail : code != 0 ? $"scskiller_warm exited with code {code}{tail}" : Warmer.NoDoneError;
            }
            return new WarmResult(outcome, Whole(reached), passes?.Total ?? total, failed, clock.Elapsed, growth,
                Path.Combine(stage ?? Path.Combine(workDir, "stage"), "scskiller.log"), outcome == WarmOutcome.Failed ? error : null,
                Crashed: crashed.Count > 0 ? crashed : null, DriverSkipped: skips, Resumable: resumable && outcome == WarmOutcome.Failed,
                AftermathDropped: dropped, CacheKeysAdded: added.Count > 0 ? added : null, Full: outcome == WarmOutcome.Completed ? full : null,
                Invalid: last?.Invalid ?? 0, Unsupported: last?.Unsupported ?? 0, D3DSCacheNoFlush: done is { Event: "done", NoFlush: true }, Cached: cached,
                Known: Math.Min(known, skips), Culprits: culprits.Count > 0 ? culprits : null, Unnamed: unnamed || _ended != null);   // each Linux Vulkan process replays the whole archive: the last one's count is the run's
        }
    }

    /// <summary>The first unfinished item of a process started at <paramref name="start"/>, with the failures and skips below it,
    /// from the item states it left under its staging folder (proxy/warm_items.h); null when there are none to go by.</summary>
    internal static (long From, long Failed, long Skipped)? Finished(string? stage, long start)
    {
        const int header = 40;   // magic, pid, first, n12, total
        try
        {
            if (stage == null || !Directory.Exists(stage)) return null;
            foreach (var path in Directory.EnumerateFiles(stage, "scskiller_items.bin", SearchOption.AllDirectories))
            {
                // mapped, as the process wrote it
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (file.Length < header) continue;
                using var map = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(file, null, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read,
                    HandleInheritability.None, leaveOpen: true);
                using var v = map.CreateViewAccessor(0, file.Length, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
                var (magic, first, n12) = (v.ReadUInt64(0), v.ReadInt64(16), v.ReadInt64(24));
                if (magic != 0x31534D4554494B53 || first != start || n12 < first || file.Length != header + n12) continue;
                long from = first, failed = 0, skipped = 0;
                for (byte s; from < n12 && (s = v.ReadByte(header + from)) is 1 or 2 or 5; from++) (failed, skipped) = (failed + (s == 2 ? 1 : 0), skipped + (s == 5 ? 1 : 0));
                return (from, failed, skipped);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>A later process's progress, as the whole warm's: its failures and skips plus the earlier ones', with the retry
    /// or pass note; a careful warm's total is every pass's items.</summary>
    sealed class Adjusted(IProgress<WarmProgress> inner, long carried, long carriedSkips, string? note, long noteTill, string? passNote, long? total) : IProgress<WarmProgress>
    {
        public void Report(WarmProgress p) => inner.Report(p with { Total = total ?? p.Total, Failed = p.Failed + carried, DriverSkipped = p.DriverSkipped + carriedSkips,
            Note = note != null && p.Done <= noteTill ? note : passNote });
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_paused || _stopping || _p.HasExited) return;
            _p.Suspend(true);
            _paused = true;
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (!_paused) return;
            _p.Suspend(false);
            Volatile.Write(ref _resumed, Stopwatch.GetTimestamp());   // before _paused: the stall watchdog's wait starts again
            _paused = false;
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_stopping || Completion.IsCompleted) return;
            _stopping = true;
        }
        Resume();   // a suspended process can't see the stop
        try { _host.Stop(); } catch (ObjectDisposedException) { return; }
        // No done or stopping event soon after the stop = stuck (the host's stop is every process's, so a relaunch sees it too,
        // and none starts after a stop). After either the process may be writing the driver cache for minutes (NVIDIA writes
        // at exit): scskiller_warm's own exit limit (600 s) bounds that, not us. An ended run is Failed and resumable (see Run).
        _ = Task.Run(async () =>
        {
            await Task.WhenAny(_doneSeen.Task, Completion, Task.Delay(_stuckAfter));
            if (_doneSeen.Task.IsCompleted || Completion.IsCompleted || _retried) return;
            IWarmProcess p;
            lock (_lock) p = _p;
            _ended = Warmer.StuckError;
            p.Kill();
        });
    }
}

/// <summary>Reads scskiller_warm's stdout and reports its progress without ever making the child wait.</summary>
public static class WarmOutput
{
    static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(10);

    /// <summary>The reader only parses lines and keeps the latest progress; another loop reports it every
    /// <paramref name="every"/> (and every 10 s unchanged, so a silent warm still shows as stalled). A slow listener then
    /// sees the current line, not a backlog: a listener that took seconds per line (cache attribution over a 42 GB driver
    /// cache) once filled the pipe, blocked the child's progress loop on its stdout, and the queue showed numbers 10 minutes
    /// old. The rate reported is the recent one (<see cref="RecentRate"/>), not the child's average since it started.
    /// <paramref name="whole"/> maps a line's done to the reported one (a pass of a careful warm: <see cref="WarmPasses.Overall"/>).
    /// <paramref name="onStage"/> gets the stage event's staging folder. <paramref name="onDone"/> runs on the done event, and
    /// on the stopping one of a warm that passed the stop on and bounds the wait for its replayer itself (the Linux Vulkan warm).
    /// Once <paramref name="exited"/> completes, stdout gets <paramref name="held"/> to end: a process that inherited it (or
    /// one the driver keeps from tearing down) can hold it open for good. What was read by then is returned, with
    /// <see cref="Warmer.OutputHeldError"/> when no done or retry event came. Between its start event and its done, retry, stopping
    /// or error event, a process whose done and failed counts don't move for <paramref name="stalled"/> (zero: never) while not
    /// <paramref name="paused"/> (counted from the last <paramref name="resumed"/> too) is given up on the same way, with <see cref="Warmer.StalledError"/>: the caller ends it.
    /// <paramref name="onCulprits"/> gets each event's <see cref="WarmEvent.Culprits"/>, an error's too. <paramref name="onRetry"/>
    /// runs on the retry event.</summary>
    public static async Task<(WarmEvent? Last, WarmEvent? Done, string? Error)> Pump(TextReader stdout, IProgress<WarmProgress>? progress,
        Func<long> cacheGrowth, TimeSpan every, Action? onDone = null, Func<long, long>? whole = null, Action<string>? onStage = null,
        Task? exited = null, TimeSpan held = default, TimeSpan stalled = default, Func<bool>? paused = null, Func<long>? resumed = null,
        Action<IReadOnlyList<string>>? onCulprits = null, Action? onRetry = null)
    {
        whole ??= d => d;
        WarmEvent? last = null, done = null, latest = null;
        string? error = null;
        var abandoned = false;   // returned while stdout was held: a later line is no longer this process's result
        var gate = new Lock();   // a line's state and callbacks, against the abandonment's snapshot
        long lastLine = Stopwatch.GetTimestamp();
        long moved = 0;   // when the counts last moved since the start event; 0 = not between start and the end events
        (long, long, long)? counts = null;
        var reader = Task.Run(async () =>
        {
            while (await stdout.ReadLineAsync() is { } line)
            lock (gate)
            {
                if (abandoned) return;
                Volatile.Write(ref lastLine, Stopwatch.GetTimestamp());
                if (WarmEvent.Parse(line) is not { } e) continue;
                if (e.Event == "error") error ??= e.Message ?? "unknown error";
                else if (e.Event is "done" or "retry") done = e;   // retry: the process ends, a new one goes on (WarmRun)
                if (e.Culprits != null) onCulprits?.Invoke(e.Culprits);
                if (e.Event is "done" or "stopping") onDone?.Invoke();
                if (e.Event == "retry") onRetry?.Invoke();
                if (e is { Event: "stage", Stage: { Length: > 0 } st }) onStage?.Invoke(st);
                if (e.Event == "start") Volatile.Write(ref moved, Stopwatch.GetTimestamp());
                else if (e.Event is "done" or "retry" or "stopping" or "error") Volatile.Write(ref moved, 0);
                else if (e.Event == "progress" && counts != (counts = (e.Done, e.Failed, e.Work)) && Volatile.Read(ref moved) != 0) Volatile.Write(ref moved, Stopwatch.GetTimestamp());
                if (e.Event is "start" or "progress" or "done" or "retry") last = e;
                if (e.Event is "progress" or "done") Volatile.Write(ref latest, e);
            }
        });
        var clock = Stopwatch.StartNew();
        var rate = new RecentRate();
        WarmEvent? reported = null;
        var since = Stopwatch.StartNew();
        void Report(bool final)
        {
            if (Volatile.Read(ref latest) is not { } e) return;
            var d = whole(e.Done);
            rate.Add(clock.Elapsed, d);
            if (progress == null || (e == reported && !final && since.Elapsed < KeepAlive)) return;
            (reported, since) = (e, Stopwatch.StartNew());
            progress.Report(new WarmProgress(d, e.Total, e.Failed, rate.PerSecond ?? e.Rate, cacheGrowth(), Workers: e.Workers, Threads: e.Threads, DriverSkipped: e.DriverSkipped,
                Invalid: e.Invalid, Unsupported: e.Unsupported));
        }
        long? gone = null;   // when the process was seen exited with its stdout still open
        while (await Task.WhenAny(reader, Task.Delay(every)) != reader)
        {
            Report(false);
            if (stalled > TimeSpan.Zero && Volatile.Read(ref moved) is var at and not 0)
            {
                // a paused run (suspended, or stopping: Stop bounds that itself) isn't stalled, and a resume starts the wait again
                bool Stalled() => paused?.Invoke() != true && Stopwatch.GetElapsedTime(Math.Max(at, resumed?.Invoke() ?? 0)) >= stalled;
                if (Stalled())
                {
                    (WarmEvent? Last, WarmEvent? Done, string? Error) given = default;
                    lock (gate)
                        if (Volatile.Read(ref moved) == at && Stalled())   // no line, pause or resume meanwhile
                        {
                            abandoned = true;
                            given = (last, done, Warmer.StalledError);
                        }
                    if (given.Error != null) return given;
                }
            }
            if (exited is not { IsCompleted: true }) continue;
            gone ??= Stopwatch.GetTimestamp();
            // a line read since restarts the wait: output still draining isn't held
            bool Quiet() => Stopwatch.GetElapsedTime(Math.Max(gone.Value, Volatile.Read(ref lastLine))) >= held;
            if (!Quiet()) continue;
            (WarmEvent? Last, WarmEvent? Done, string? Error) kept;
            lock (gate)
            {
                if (!Quiet()) continue;   // a line came in meanwhile
                // ponytail: the read stays pending until the holder closes the pipe, one per such compile; cancel it if they pile up
                abandoned = true;
                kept = (last, done, done == null ? error ?? Warmer.OutputHeldError : error);
            }
            if (Volatile.Read(ref latest) != reported) Report(true);
            return kept;
        }
        await reader;
        if (Volatile.Read(ref latest) != reported) Report(true);   // the last line (the done event's counts)
        return (last, done, error);
    }
}

/// <summary>Pipelines per second over the last 30 s of (time, done) samples; null until they span 5 s. The child's own
/// rate is its average since it started, which stays high long after a warm slows down (the cached start is fast).</summary>
public sealed class RecentRate
{
    static readonly TimeSpan Window = TimeSpan.FromSeconds(30), MinSpan = TimeSpan.FromSeconds(5);
    readonly Queue<(TimeSpan At, long Done)> _samples = new();

    public void Add(TimeSpan at, long done)
    {
        _samples.Enqueue((at, done));
        while (_samples.Count > 2 && at - _samples.Peek().At > Window) _samples.Dequeue();
    }

    public double? PerSecond
    {
        get
        {
            if (_samples.Count < 2) return null;
            var (t0, d0) = _samples.Peek();
            var (t1, d1) = _samples.Last();
            return t1 - t0 >= MinSpan ? Math.Max(0, d1 - d0) / (t1 - t0).TotalSeconds : null;
        }
    }
}

/// <summary>Suspends or resumes a process and all its descendants (scskiller_warm runs the staged copy named like the
/// game as a child), and lists processes without opening any.</summary>
static partial class ProcessTree
{
    public static partial void Suspend(int rootPid, bool suspend);

    /// <summary>One snapshot: no process is opened; Exe has its extension.</summary>
    public static partial List<(int Pid, int Parent, string Exe)> Snapshot();

    /// <summary><see cref="Snapshot"/>, at most a second old.</summary>
    public static List<(int Pid, int Parent, string Exe)> RecentSnapshot()
    {
        lock (SnapshotLock)
        {
            if (_snapshot == null || Environment.TickCount64 - _snapshotAt > 1000) (_snapshot, _snapshotAt) = (Snapshot(), Environment.TickCount64);
            return _snapshot;
        }
    }

    static readonly Lock SnapshotLock = new();
    static List<(int, int, string)>? _snapshot;
    static long _snapshotAt;

    internal static List<int> WithDescendants(int root)
    {
        var all = Snapshot();
        var linked = new HashSet<int> { root };   // by the parent links alone: only these are opened for their creation times
        for (var more = true; more;)
        {
            more = false;
            foreach (var p in all) more |= linked.Contains(p.Parent) && linked.Add(p.Pid);
        }
        return Tree(root, all.Where(p => linked.Contains(p.Pid)).Select(p => (p.Pid, p.Parent, Created(p.Pid))));
    }

    /// <summary><paramref name="root"/> and its descendants among <paramref name="procs"/>. A child counts only if it was
    /// created at or after its parent: Windows reuses an exited process's pid, so an older process whose parent exited names
    /// a pid that may now be ours. A process without a creation time (it couldn't be opened) is left out with its
    /// descendants; a root without one, or not listed, is the whole tree.</summary>
    internal static List<int> Tree(int root, IEnumerable<(int Pid, int Parent, long? Created)> procs)
    {
        var all = procs.ToList();
        if (all.FirstOrDefault(p => p.Pid == root).Created is not { } rootCreated) return [root];
        var tree = new List<(int Pid, long Created)> { (root, rootCreated) };
        var seen = new HashSet<int> { root };
        for (int i = 0; i < tree.Count; i++)
            foreach (var p in all)
                if (p.Parent == tree[i].Pid && p.Created is { } c && c >= tree[i].Created && seen.Add(p.Pid)) tree.Add((p.Pid, c));
        return tree.Select(t => t.Pid).ToList();
    }

    /// <summary>The process's image path, without opening the process (no handle to it: nothing an anti-cheat driver's handle
    /// callbacks see). Null when it has exited or can't be told.</summary>
    public static partial string? ImagePath(int pid);

    /// <summary>An NT path (\Device\HarddiskVolume3\...) on a drive letter's device, as a DOS path; null when no drive maps it.</summary>
    internal static string? DosPath(string nt, IEnumerable<(string Drive, string Device)> devices)
    {
        // Wine names images in the DOS devices namespace (\??\C:\...)
        if (nt.Length > 7 && nt.StartsWith(@"\??\", StringComparison.Ordinal) && char.IsAsciiLetter(nt[4]) && nt[5] == ':' && nt[6] == '\\') return nt[4..];
        foreach (var (drive, device) in devices)
            if (nt.StartsWith(device + "\\", StringComparison.OrdinalIgnoreCase)) return drive + nt[device.Length..];
        return null;
    }

    /// <summary>The process's creation time, comparable between processes; null when it can't be read or it has exited.</summary>
    internal static partial long? Created(int pid);
}

/// <summary>Finds the native binaries (scskiller_warm.exe, the proxy d3d12.dll): next to the app (native\ first),
/// else, for dev builds, the nearest proxy\build\Release above the app folder. Off Windows, where nothing builds them, a
/// dev box names their folder first in SCSKILLER_NATIVE_DIR.</summary>
public static class NativeTools
{
    public const string DirVariable = "SCSKILLER_NATIVE_DIR";

    public static string? Find(string fileName)
    {
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable(DirVariable) is { Length: > 0 } dev && Path.Combine(dev, fileName) is var built && File.Exists(built))
            return Path.GetFullPath(built);
        var app = AppContext.BaseDirectory;   // the published CLI lives in cli\ next to the app's native\
        foreach (var p in new[] { Path.Combine(app, "native", fileName), Path.Combine(app, fileName), Path.GetFullPath(Path.Combine(app, "..", "native", fileName)) })
            if (File.Exists(p)) return p;
        for (var d = new DirectoryInfo(app); d != null; d = d.Parent)
        {
            var p = Path.Combine(d.FullName, "proxy", "build", "Release", fileName);
            if (File.Exists(p)) return p;
        }
        return null;
    }
}
