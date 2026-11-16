using System.Diagnostics;
using System.Text.RegularExpressions;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.Warming;

/// <summary>How a warm's launcher lowers its priority: <paramref name="SystemdRun"/>, when a systemd user manager runs, puts
/// it in a scope of its own (<paramref name="SystemdVersion"/>: 0 when unknown); without one, nice and ionice.</summary>
sealed record ProtonTools(string? SystemdRun, int SystemdVersion)
{
    /// <summary>systemd-run on <paramref name="searchPath"/> (default: PATH), when the user manager's private socket is in
    /// <paramref name="runtimeDir"/> (default: XDG_RUNTIME_DIR), as systemd-run --user reaches it.</summary>
    public static ProtonTools Find(string? searchPath = null, string? runtimeDir = null)
    {
        runtimeDir ??= Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir) || !Path.Exists(Path.Combine(runtimeDir, "systemd", "private"))) return new(null, 0);
        var run = (searchPath ?? Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, "systemd-run")).FirstOrDefault(File.Exists);
        return run == null ? new(null, 0) : new(run, Version(run));
    }

    /// <summary>The version systemd-run --version prints first ("systemd 256 (...)"); 0 when it doesn't say.</summary>
    static int Version(string systemdRun)
    {
        try
        {
            var psi = new ProcessStartInfo(systemdRun, "--version") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p == null) return 0;
            var first = p.StandardOutput.ReadLineAsync();
            if (!first.Wait(TimeSpan.FromSeconds(5)) || !p.WaitForExit(TimeSpan.FromSeconds(5))) { p.Kill(); return 0; }
            return Regex.Match(first.Result ?? "", @"^systemd (\d+)") is { Success: true } m && int.TryParse(m.Groups[1].Value, out var v) ? v : 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { return 0; }
    }
}

/// <summary>A warm's launcher command line and environment, as functions of their inputs: a Proton warm's, and the native
/// Vulkan warm's (<see cref="NativeCommand"/>).</summary>
static class ProtonLaunch
{
    /// <summary>The nice a warm runs at without a scope: idle 19 (and the idle I/O class), below normal 10, whose CPU weight is
    /// about a tenth of a normal process's, the scope's below-normal weight.</summary>
    public const int IdleNice = 19, BelowNice = 10;

    /// <summary>/bin/sh sends the launcher's stdout and stderr to <paramref name="log"/> (appended) and its stdin from
    /// /dev/null, then setsid gives it a session of its own (a terminal's Ctrl+C reaches Core alone), the priority prefix
    /// lowers it, and Steam's runtime entry point runs Proton's run verb on <paramref name="warmExe"/> (a Windows path).
    /// Never runinprefix: it skips Proton's prefix setup, where vkd3d-proton's d3d12 override is set. The scope:
    /// CPUWeight=idle (cpu.idle) and IOWeight=1 for idle, CPUWeight=10 for below normal, which hold whatever nice a process
    /// in it gets (ananicy renices the staged child, named like the game, to the game's). Without a scope: an absolute nice
    /// (nice -n adds to <paramref name="coreNice"/>, the starting thread's own) and for idle ionice's idle class.</summary>
    public static List<string> Command(ProtonTools tools, WarmPriority priority, int coreNice, string unit, string log, string runtime, string proton,
        string warmExe, IReadOnlyList<string> args) =>
        [.. Launcher(tools, priority, coreNice, unit, log), Path.Combine(runtime, SteamSource.EntryPoint), "--verb=run", "--", Path.Combine(proton, "proton"), "run", warmExe, .. args];

    /// <summary>The command line as sh would read it, for the log: an argument with a space, a quote or nothing in single quotes.</summary>
    public static string ShellLine(IEnumerable<string> argv) =>
        string.Join(' ', argv.Select(a => a.Length > 0 && !a.Any(c => char.IsWhiteSpace(c) || c is '\'' or '"') ? a : "'" + a.Replace("'", @"'\''") + "'"));

    /// <summary>The Flatpak Steam's app, whose sandbox a warm of its games runs in.</summary>
    public const string FlatpakApp = "com.valvesoftware.Steam";

    /// <summary>A Flatpak game's warm's (the Flatpak Steam's, or a Flatpak launcher's <paramref name="app"/>): <see cref="Command"/>'s
    /// shell, session and nice and ionice (flatpak run moves itself into a scope of its own, and the game's processes start in
    /// another, which <see cref="FlatpakScopes"/> lowers), then <paramref name="flatpak"/> run of the runtime's entry point in
    /// the app's sandbox (no <paramref name="runtime"/>: Proton's script itself, as Heroic runs it without umu), given
    /// <paramref name="exchange"/> (<see cref="FlatpakExchange"/>) and no other folder of Core's.
    /// <paramref name="cacheHome"/>: the game's XDG_CACHE_HOME, which flatpak run would otherwise set to the app's own.</summary>
    public static List<string> FlatpakCommand(string flatpak, string app, WarmPriority priority, int coreNice, string log, string exchange, string? runtime, string proton,
        string warmExe, IReadOnlyList<string> args, string? cacheHome = null) =>
        [.. Launcher(new(null, 0), priority, coreNice, "", log), flatpak, "run",
            $"--command={(runtime == null ? Path.Combine(proton, "proton") : Path.Combine(runtime, SteamSource.EntryPoint))}", $"--filesystem={exchange}",
            .. cacheHome == null ? Array.Empty<string>() : [$"--env=XDG_CACHE_HOME={cacheHome}"],
            app, .. runtime == null ? Array.Empty<string>() : ["--verb=run", "--", Path.Combine(proton, "proton")], "run", warmExe, .. args];

    /// <summary>What a Flatpak Steam game's warm keeps of Core's environment, which flatpak run hands into the sandbox: what
    /// flatpak needs to find the app and reach the session (its folders, the session bus, the displays), the locale, and
    /// NVIDIA's settings other than <see cref="Dropped"/>'s, as the game gets them from the Steam's session, and on Mesa
    /// (<paramref name="mesa"/>) its device choice and drivers' switches. No other variable of Core's reaches the sandbox.</summary>
    internal static bool FlatpakKept(string name, bool mesa = false) =>
        mesa && MesaSetting(name) ||
        name is "PATH" or "HOME" or "USER" or "LOGNAME" or "LANG" or "LANGUAGE" or "XDG_RUNTIME_DIR" or "DBUS_SESSION_BUS_ADDRESS"
            or "XDG_DATA_HOME" or "XDG_DATA_DIRS" or "XDG_CONFIG_HOME" or "XDG_CONFIG_DIRS" or "XDG_CACHE_HOME" or "XDG_STATE_HOME"
            or "DISPLAY" or "XAUTHORITY" or "WAYLAND_DISPLAY" or "XDG_SESSION_TYPE" or "XDG_CURRENT_DESKTOP" or "XDG_SESSION_DESKTOP"
            or "FLATPAK_USER_DIR" or "FLATPAK_SYSTEM_DIR" or "FLATPAK_CONFIG_DIR"
        || name.StartsWith("LC_", StringComparison.Ordinal)
        || (name.StartsWith("__GL_", StringComparison.Ordinal) || name.StartsWith("__NV_", StringComparison.Ordinal)) && !Dropped(name);

    /// <summary>The native Vulkan warm's: <see cref="Command"/>'s shell, session and priority, then <paramref name="warmExe"/>
    /// (a Linux path) itself.</summary>
    public static List<string> NativeCommand(ProtonTools tools, WarmPriority priority, int coreNice, string unit, string log, string warmExe, IReadOnlyList<string> args) =>
        [.. Launcher(tools, priority, coreNice, unit, log), warmExe, .. args];

    static List<string> Launcher(ProtonTools tools, WarmPriority priority, int coreNice, string unit, string log)
    {
        var idle = priority == WarmPriority.Idle;
        List<string> prefix;
        if (tools.SystemdRun is { } run)
        {
            // CPUWeight=idle is systemd 251's, and from 254 systemd-run expands ${...} in a scope's command line itself
            prefix = [run, "--user", "--scope", "-q", "--collect", $"--unit={unit}",
                "-p", idle ? tools.SystemdVersion >= 251 ? "CPUWeight=idle" : "CPUWeight=1" : "CPUWeight=10"];
            if (idle) prefix.AddRange(["-p", "IOWeight=1"]);
            if (tools.SystemdVersion >= 254) prefix.Add("--expand-environment=no");
        }
        else
        {
            prefix = ["nice", "-n", Math.Max(0, (idle ? IdleNice : BelowNice) - coreNice).ToString()];
            if (idle) prefix.AddRange(["ionice", "-c", "3"]);
        }
        return ["/bin/sh", "-c", "exec \"$@\" </dev/null >>\"$0\" 2>&1", log, "setsid", "-w", .. prefix];
    }

    /// <summary>The picked GPU's name again, for the warm's own fallback when a filter the prefix's registry sets matches no GPU.</summary>
    internal const string WarmGpu = "SCSKILLER_WARM_GPU";

    /// <summary>The launcher's environment: <paramref name="inherited"/> (Core's, which the game Steam starts shares) without
    /// what would tie the warm to Steam's app or change its device or keys, then the variables Steam sets for the game's
    /// launch, the app id as STEAM_COMPAT_APP_ID alone (Proton's per-game config, which Steam ignores: SteamAppId or
    /// SteamGameId make Proton's steam.exe register the game as running), the warm's cache folders and app name
    /// (<paramref name="cache"/>), the picked GPU by name (<paramref name="gpuName"/>: Wine's DXGI has LUIDs of its own),
    /// the job's token and <paramref name="extra"/> last. STEAM_COMPAT_MOUNTS holds the <paramref name="mounts"/> outside
    /// <paramref name="home"/>, which the runtime's container otherwise doesn't see. Logs, dumps and crash reports never go
    /// outside <paramref name="work"/>: DXVK's file log is off and Proton's log goes there.</summary>
    public static Dictionary<string, string> Environment(IEnumerable<KeyValuePair<string, string>> inherited, ProtonContext c, IEnumerable<(string Name, string Value)> cache,
        string? gpuName, string token, IReadOnlyDictionary<string, string> extra, string? home, IEnumerable<string> mounts, string work)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in inherited)
            if (!Dropped(k)) env[k] = v;
        WithSessionDisplay(env);
        env["DXVK_LOG_PATH"] = "none";
        env["DXVK_LOG_LEVEL"] = "info";   // Proton's default is none: the devices DXVK found or skipped reach the launcher's output
        env["PROTON_LOG_DIR"] = work;   // PROTON_LOG writes to the home folder otherwise
        env["STEAM_COMPAT_DATA_PATH"] = c.CompatData;
        env["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = c.SteamRoot;
        env["STEAM_COMPAT_INSTALL_PATH"] = c.Install;
        env["STEAM_COMPAT_LIBRARY_PATHS"] = string.Join(':', c.Libraries.Select(l => Path.Combine(l, "steamapps")));
        env["STEAM_COMPAT_TOOL_PATHS"] = $"{c.Proton}:{c.Runtime}";
        var outside = mounts.Select(Path.GetFullPath).Where(m => string.IsNullOrEmpty(home) || !Below(Path.GetFullPath(home), m)).Distinct(StringComparer.Ordinal).ToList();
        if (outside.Count > 0) env["STEAM_COMPAT_MOUNTS"] = string.Join(':', outside);
        env["STEAM_COMPAT_APP_ID"] = c.AppId.ToString();
        foreach (var (k, v) in cache) env[k] = v;
        if (!string.IsNullOrEmpty(gpuName)) env["VKD3D_FILTER_DEVICE_NAME"] = env["DXVK_FILTER_DEVICE_NAME"] = env[WarmGpu] = gpuName;
        env[WarmJob.Variable] = token;
        foreach (var (k, v) in extra) env[k] = v;
        return env;
    }

    /// <summary>The display variables of the graphical session, for a warm started without one (the idle and driver-update
    /// compiles' timer, a compile over SSH): Wine with no display can deadlock at its start (Proton 11's win32u: its Vulkan
    /// init loads the display driver, whose GPU list waits on that same init, measured on ARM64 Proton), and the game always
    /// runs with the session's. Taken from the systemd user manager, where a desktop session puts them, else from
    /// gamescope's $XDG_RUNTIME_DIR/gamescope-environment (gamescope-session-plus, a handheld's game mode, puts none in the
    /// manager), read at each warm: the session can change between them. Added only on arm64 (where the deadlock was
    /// measured; elsewhere the warm keeps the environment it had), when <paramref name="env"/> has neither DISPLAY nor
    /// WAYLAND_DISPLAY, and only what the session names; nothing without a graphical session.</summary>
    internal static void WithSessionDisplay(Dictionary<string, string> env)
    {
        if (System.Runtime.InteropServices.RuntimeInformation.OSArchitecture != System.Runtime.InteropServices.Architecture.Arm64) return;
        if (env.ContainsKey("DISPLAY") || env.ContainsKey("WAYLAND_DISPLAY")) return;
        foreach (var (k, v) in SessionDisplay(env.GetValueOrDefault("XDG_RUNTIME_DIR")))
            if (k is "DISPLAY" or "WAYLAND_DISPLAY" or "XAUTHORITY" or "XDG_SESSION_TYPE" || !env.ContainsKey(k)) env[k] = v;
    }

    static readonly string[] DisplayNames = ["DISPLAY", "WAYLAND_DISPLAY", "XAUTHORITY", "XDG_SESSION_TYPE", "XDG_RUNTIME_DIR"];

    static List<(string Name, string Value)> SessionDisplay(string? runtimeDir)
    {
        static Dictionary<string, string> Parse(string text) => text.Split('\n').Select(l => l.Trim().Split('=', 2)).Where(kv => kv.Length == 2 && kv[0].Length > 0)
            .GroupBy(kv => kv[0]).ToDictionary(g => g.Key, g => g.Last()[1], StringComparer.Ordinal);
        static bool Shows(Dictionary<string, string> v) => v.GetValueOrDefault("DISPLAY") is { Length: > 0 } || v.GetValueOrDefault("WAYLAND_DISPLAY") is { Length: > 0 };
        try
        {
            using var p = Process.Start(new ProcessStartInfo("systemctl", ["--user", "show-environment"])
                { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (p != null)
            {
                var read = p.StandardOutput.ReadToEndAsync();
                if (!p.WaitForExit(3000)) { try { p.Kill(); } catch (InvalidOperationException) { } }
                else if (p.ExitCode == 0 && Parse(read.Result) is var manager && Shows(manager))
                    return [.. DisplayNames.Where(manager.ContainsKey).Select(k => (k, manager[k]))];
            }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
        try
        {
            var dir = runtimeDir is { Length: > 0 } r ? r : $"/run/user/{App.Libc.UserId}";
            var file = Path.Combine(dir, "gamescope-environment");
            if (File.Exists(file) && Parse(File.ReadAllText(file)) is var gamescope && Shows(gamescope))
                return [.. DisplayNames.Where(gamescope.ContainsKey).Select(k => (k, gamescope[k])), ("XDG_RUNTIME_DIR", dir)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return [];
    }

    /// <summary>A Wine warm's environment, in a launcher's prefix <paramref name="prefix"/> (<see cref="LauncherPrefix"/>):
    /// <paramref name="inherited"/> without what <see cref="Dropped"/> names or another prefix's, then WINEPREFIX, the prefix's
    /// vkd3d-proton as d3d12 (native first, as Proton and Lutris set it), the warm's cache variables, the picked GPU by name,
    /// the job's token and <paramref name="extra"/> last. DXVK's file log and Wine's debug output off.</summary>
    public static Dictionary<string, string> WineEnvironment(IEnumerable<KeyValuePair<string, string>> inherited, string prefix, IEnumerable<(string Name, string Value)> cache,
        string? gpuName, string token, IReadOnlyDictionary<string, string> extra)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in inherited)
            if (!Dropped(k) && k is not ("WINEPREFIX" or "WINEARCH" or "WINELOADER" or "WINESERVER" or "WINEDEBUG")) env[k] = v;
        WithSessionDisplay(env);
        env["DXVK_LOG_PATH"] = "none";
        env["DXVK_LOG_LEVEL"] = "info";   // Proton's default is none: the devices DXVK found or skipped reach the launcher's output
        env["WINEDEBUG"] = "-all";
        env["WINEPREFIX"] = prefix;
        env["WINEDLLOVERRIDES"] = "d3d12,d3d12core=n,b;winemenubuilder.exe=d";
        foreach (var (k, v) in cache) env[k] = v;
        if (!string.IsNullOrEmpty(gpuName)) env["VKD3D_FILTER_DEVICE_NAME"] = env["DXVK_FILTER_DEVICE_NAME"] = env[WarmGpu] = gpuName;
        env[WarmJob.Variable] = token;
        foreach (var (k, v) in extra) env[k] = v;
        return env;
    }

    /// <summary>The native Vulkan warm's environment: of <paramref name="inherited"/> only what <see cref="NativeKept"/> names
    /// for the GPU's driver (<paramref name="mesa"/>: Mesa's, else NVIDIA's) and <paramref name="alsoKept"/> adds (by exact
    /// name), and in an internal build the warm's switches (<see cref="WarmSwitch"/>), never what <see cref="NativeExcluded"/> names, then the warm's cache variables (<paramref name="cache"/>), the
    /// job's token, and of <paramref name="extra"/> only the warm's and the replayer's own switches (<see cref="WarmSwitch"/>):
    /// no secret or other program's setting reaches the native binaries, nor the game's recorded cache redirect or Steam's
    /// app id.</summary>
    public static Dictionary<string, string> NativeEnvironment(IEnumerable<KeyValuePair<string, string>> inherited, IEnumerable<(string Name, string Value)> cache,
        bool mesa, string token, IReadOnlyDictionary<string, string> extra, IReadOnlyCollection<string>? alsoKept = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in inherited)
            if (!NativeExcluded(k, mesa) && (NativeKept(k, mesa) || TestHooks && WarmSwitch(k) || alsoKept?.Contains(k, StringComparer.Ordinal) == true)) env[k] = v;
        foreach (var (k, v) in cache) env[k] = v;
        foreach (var (k, v) in extra)
            if (WarmSwitch(k)) env[k] = v;
        env[WarmJob.Variable] = token;   // after extra: SCSKILLER_WARM_JOB is a warm switch by name
        return env;
    }

    /// <summary>What the native warm keeps of Core's environment: the user's folders, runtime folder and session bus (where
    /// systemd-run finds the user manager), the locale, the Vulkan loader's driver search, and the settings of the GPU's
    /// driver alone: on Mesa (<paramref name="mesa"/>) its device choice and RADV's, ACO's and ANV's switches (they change
    /// what the driver compiles for the game, so the warm's must be the same), else NVIDIA's other than the cache's and PRIME
    /// offload's, and the X display with its authority file: NVIDIA's driver names its cache folder by whether it reaches the X
    /// server, as the game under Steam does. The user's own cache variables are never kept. No library search path: the
    /// native binaries need glibc alone and load the system's libvulkan.so.1.</summary>
    internal static bool NativeKept(string name, bool mesa) =>
        name is "PATH" or "HOME" or "USER" or "LOGNAME" or "LANG" or "LANGUAGE" or "TMPDIR" or "XDG_RUNTIME_DIR" or "DBUS_SESSION_BUS_ADDRESS"
            or "XDG_DATA_HOME" or "XDG_DATA_DIRS" or "XDG_CONFIG_HOME" or "XDG_CONFIG_DIRS"
            or "VK_DRIVER_FILES" or "VK_ICD_FILENAMES" or "VK_ADD_DRIVER_FILES"
        || name.StartsWith("LC_", StringComparison.Ordinal)
        || (mesa ? MesaSetting(name)
            : name is "DISPLAY" or "XAUTHORITY"
                || (name.StartsWith("__GL_", StringComparison.Ordinal) || name.StartsWith("__NV_", StringComparison.Ordinal)) && !Dropped(name));

    internal static bool MesaSetting(string name) =>
        name is "MESA_VK_DEVICE_SELECT" or "DRI_PRIME" or "RADV_PERFTEST" or "RADV_DEBUG" or "ACO_DEBUG" or "ANV_DEBUG" or "INTEL_DEBUG";

    /// <summary>What never reaches the native warm of Core's environment, whatever keeps it: the dynamic linker's variables,
    /// a preload of any kind, the Vulkan loader's layers, sh's startup file, the user's own Mesa cache variables, and the
    /// other driver's settings (<paramref name="mesa"/>: NVIDIA's, else Mesa's). Names match exactly, case included.</summary>
    internal static bool NativeExcluded(string name, bool mesa) =>
        name.StartsWith("LD_", StringComparison.Ordinal) || name.EndsWith("_PRELOAD", StringComparison.Ordinal)
        || name.StartsWith("VK_LAYER", StringComparison.Ordinal) || name is "VK_INSTANCE_LAYERS" or "VK_ADD_LAYER_PATH"
        || name is "ENV"   // not BASH_ENV: the host tests' stand-in warm is a bash that runs its script from it
        || MesaLinux.CacheVariable(name)
        || (mesa ? name.StartsWith("__GL_", StringComparison.Ordinal) || name.StartsWith("__NV_", StringComparison.Ordinal) : MesaSetting(name));

#if SCSK_TEST_HOOKS
    // the CLI's own warm and replayer switches reach the native warm too (E2E), never in a release build
    const bool TestHooks = true;
#else
    const bool TestHooks = false;
#endif

    /// <summary>scskiller_warm's and fossilize-replay's own switches (fault injection and limits in tests).</summary>
    static bool WarmSwitch(string name) =>
        name.StartsWith("SCSKILLER_WARM_", StringComparison.Ordinal) || name.StartsWith("SCSKILLER_REPLAY_", StringComparison.Ordinal);

    static bool Below(string dir, string path) =>
        path == dir || path.StartsWith(Path.TrimEndingDirectorySeparator(dir) + "/", StringComparison.Ordinal);

    /// <summary>Steam's app identity and compat variables, NVAPI's of any kind (DXVK_NVAPI_*, PROTON_*NVAPI*), PRIME offload's,
    /// the cache (NVIDIA's and Mesa's of any kind), device and job variables the launcher gets from Core, those that change the
    /// settings Proton records in the prefix's config_info (its run takes Proton's defaults, <see cref="ProtonScript.Refreshes"/>),
    /// and any that names where a log, a dump, a profile or a crash report is written (Wine reads Windows' variables ignoring
    /// case).</summary>
    internal static bool Dropped(string name) =>
        name is "SteamAppId" or "SteamGameId" or "SteamOverlayGameId" or "VKD3D_FILTER_DEVICE_NAME" or "DXVK_FILTER_DEVICE_NAME" or "VKD3D_SHADER_CACHE_PATH" or WarmJob.Variable or WarmGpu
            or "__NV_PRIME_RENDER_OFFLOAD" or "__NV_PRIME_RENDER_OFFLOAD_PROVIDER" or "__VK_LAYER_NV_optimus" or "__GLX_VENDOR_LIBRARY_NAME" or "DRI_PRIME"
            or "PROTON_USE_WINED3D" or "PROTON_USE_WINED3D11" or "PROTON_DXVK_D3D8" or "PROTON_DLL_COPY" or "PROTON_ADD_CONFIG" or "WINEDLLOVERRIDES"
        || name.StartsWith("STEAM_COMPAT_", StringComparison.Ordinal) || name.StartsWith("__GL_SHADER_DISK_CACHE", StringComparison.Ordinal)
        || MesaLinux.CacheVariable(name)
        || name.Contains("NVAPI", StringComparison.OrdinalIgnoreCase)
        || name.Equals("VKD3D_QUEUE_PROFILE", StringComparison.OrdinalIgnoreCase)
        || WrittenTo.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));

    static readonly string[] WrittenTo = ["_LOG_PATH", "_LOG_DIR", "_LOG_FILE", "_LOG_FILENAME", "_DUMP_PATH", "_DUMP_FILE", "_CRASH_REPORT_DIR"];
}
