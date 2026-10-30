using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Core.Warming;

static partial class WarmHost
{
    public const string NotSteamError = "compiling on Linux works only for games Steam, Heroic or Lutris runs with Wine or Proton",
        NotSteamVulkanError = "compiling Vulkan games on Linux works only for games Steam runs",
        InFlatpakError = "the Flatpak version of SCSKiller can't compile or record Flatpak Steam games yet",
        FlatpakVulkanError = "compiling Vulkan games from the Flatpak Steam isn't available yet",
        FlatpakSteamMesaError = "compiling games from the Flatpak Steam isn't available yet on AMD or Intel graphics",
        FlatpakNoSystemdError = "compiling games from the Flatpak Steam needs a systemd user session",
        FlatpakNoCommandError = "compiling games from the Flatpak Steam needs the flatpak command",
        FlatpakColonError = "compiling games from the Flatpak Steam needs a data folder path without a ':'",
        FlatpakNoPidfdError = "compiling games from the Flatpak Steam needs a newer Linux kernel",
        FlatpakMesaError = "compiling games of a Flatpak Heroic or Lutris isn't available yet on Mesa's drivers: Mesa's cache of their games is in the Flatpak's folder";

    /// <summary>A game Steam runs, its own or a non-Steam game added to it (<see cref="SteamShortcuts"/>): its warm runs in
    /// its Proton prefix (<see cref="ProtonWarmHost"/>). A Windows game of Heroic or Lutris (<see cref="LauncherPrefix"/>): in
    /// the launcher's prefix, on its Proton (as a non-Steam game's) or its Wine, with the driver's default cache the game
    /// reads (<see cref="LauncherCache"/>). A game of the Flatpak Steam: in that Steam's sandbox, on NVIDIA under Proton alone,
    /// each process in a folder of its own (<see cref="FlatpakExchange"/>, whose leftovers an exited Core left go first).
    /// Throws with the reason when it can't yet (<see cref="Refusal"/>), and
    /// for any other game. A Vulkan warm runs natively, a game's under Proton too: the driver's cache is the
    /// host's, keyed on the app name the warm and the game are given, or on Mesa on content alone. First ends the warms a
    /// Core that has exited left in the data folder (<see cref="WarmJob.EndOrphans"/>).</summary>
    public static partial IWarmHost For(Game game, string workDir, GpuInfo gpu, IProgress<string>? log, string? steamRoot, bool vulkan, long? cacheCap, bool mesa)
    {
        if (vulkan ? !SteamRuns(game) : !SteamRuns(game) && !LauncherPrefix.Is(game)) throw new NotSupportedException(vulkan ? NotSteamVulkanError : NotSteamError);
        if (FlatpakSteam(game, steamRoot, vulkan, mesa) is { } refused) throw new NotSupportedException(refused);
        ProtonContext? ctx = null;
        LauncherPrefix? launched = null;
        if (!vulkan && LauncherPrefix.Is(game))
            (ctx, launched) = Launched(game, steamRoot ?? SteamSource.Root(), mesa, out var why) ?? throw new InvalidOperationException(why);
        else if (!vulkan)
        {
            ctx = Context(game, steamRoot ?? SteamSource.Root(), out var why) ?? throw new InvalidOperationException(why);
            if (ctx.Note != null) log?.Report($"{game.Name}: {ctx.Note}");
        }
        var tools = ProtonTools.Find();
        if (tools.SystemdRun == null) log?.Report($"{game.Name}: no systemd user manager, so the compile's priority is set by nice and ionice");
        // work\ is in the game's folder, games\<id>\ of the data folder (AppStore.GameDir)
        var gameDir = Path.GetDirectoryName(Path.GetFullPath(workDir))!;
        var dataDir = Path.GetDirectoryName(Path.GetDirectoryName(gameDir))!;
        WarmJob.EndOrphans(dataDir, log == null ? null : log.Report);
        string? flatpak = null;
        if (SteamRuns(game) && SteamSource.Sandbox(steamRoot ?? SteamSource.Root()) == "Flatpak")
        {
            // flatpak run's --filesystem takes what follows a ':' for its mode
            if (FlatpakExchange.PathOf(dataDir, "").Contains(':')) throw new NotSupportedException(FlatpakColonError);
            flatpak = OnPath("flatpak") ?? throw new NotSupportedException(FlatpakNoCommandError);
            // the sandbox is told from the host and ended only through pidfds: without them no warm may run (a kernel before 5.3, or seccomp)
            if (Libc.PidFd(Environment.ProcessId, out var error) is not { } probe)
            {
                log?.Report($"{game.Name}: pidfd_open failed ({System.Runtime.InteropServices.Marshal.GetPInvokeErrorMessage(error)})");
                throw new NotSupportedException(FlatpakNoPidfdError);
            }
            probe.Dispose();
            FlatpakExchange.Sweep(dataDir, log == null ? null : m => log.Report($"{game.Name}: {m}"));
        }
        return new ProtonWarmHost(ctx, game, workDir, gameDir, dataDir, gpu, tools, log: log, cacheCap: cacheCap,
            wine: launched is { Proton: false } ? launched : null, cache: launched == null ? null : LauncherCache(launched), mesa: mesa, flatpak: flatpak);
    }

    public static partial string? Refusal(Game game, string? steamRoot, bool vulkan, bool mesa) =>
        FlatpakSteam(game, steamRoot, vulkan, mesa) is { } flatpak ? flatpak
        : vulkan ? !SteamRuns(game) ? NotSteamVulkanError : null
        : LauncherPrefix.Is(game) ? Launched(game, steamRoot ?? SteamSource.Root(), mesa, out var off) == null
            ? off == FlatpakMesaError ? off : $"not ready: {off}" : null
        : !SteamRuns(game) ? NotSteamError : Context(game, steamRoot ?? SteamSource.Root(), out var why) == null ? $"not ready: {why}" : null;

    /// <summary>The cache variables a launcher's game runs with, which its warm takes: none, so both reach the driver's default
    /// cache (Mesa's keyed on content; NVIDIA's under Wine on the process image Wine's processes share, each holder of it
    /// taking the next numbered file), or a Flatpak launcher's XDG_CACHE_HOME, where its games' default cache is.</summary>
    // ponytail: NVIDIA's hit under Wine's shared key is unmeasured, key the warm's file as the game's if the game misses it
    internal static List<(string Name, string Value)> LauncherCache(LauncherPrefix p) => p.CacheHome is { } home ? [("XDG_CACHE_HOME", home)] : [];

    /// <summary>A launcher's game as its warm runs: on a Proton build its context (<see cref="ProtonContext.For(LauncherPrefix, string, string, out string?, IEnumerable{string}?)"/>),
    /// on a Wine build the prefix alone once <see cref="WineProblem"/> has none. Null with the reason when it isn't ready.</summary>
    /// <param name="mesa">the GPU runs Mesa, whose cache of a Flatpak launcher's games the warm doesn't reach</param>
    internal static (ProtonContext? Ctx, LauncherPrefix Prefix)? Launched(Game game, string steamRoot, bool mesa, out string? why)
    {
        if (LauncherPrefix.Of(game, out why) is not { } p) return null;
        if (mesa && p.CacheHome != null)
        {
            why = FlatpakMesaError;
            return null;
        }
        if (p.Proton) return ProtonContext.For(p, game.InstallDir, steamRoot, out why) is { } ctx ? (ctx, p) : null;
        why = WineProblem(p);
        return why == null ? (null, p) : null;
    }

    /// <summary>Why a warm on a launcher's Wine build would change the prefix or miss the game's cache: no prefix yet, a
    /// .update-timestamp other than the build's wine.inf time (wineboot would set the prefix up again, as for another Wine),
    /// or a d3d12.dll in its system32 that is Wine's own, not vkd3d-proton's. Null when there's none.</summary>
    internal static string? WineProblem(LauncherPrefix p)
    {
        if (!File.Exists(p.Bin) || Libc.RealPath(p.Bin) is not { } bin) return $"its Wine {p.Bin} isn't there";
        if (!new WinePrefix(p.Prefix).Made) return $"it has no Wine prefix yet: start it once from {p.Launcher}";
        // the build's own wine.inf, its bin/ link resolved: another build's time would let wineboot update the prefix
        var inf = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(bin)!)!, "share", "wine", "wine.inf");
        if (Libc.Modified(inf) is not (var seconds, _)) return $"its Wine {p.Bin} has no wine.inf beside it, so whether it would set the prefix up again can't be told";
        if (ProtonScript.WineTimestamp(Path.Combine(p.Prefix, ".update-timestamp")) is { } stamp && stamp != unchecked((ulong)seconds))
            return $"its Wine would set its prefix up again (another Wine ran it last): start it once from {p.Launcher}";
        if (!Vkd3d(Path.Combine(new WinePrefix(p.Prefix).DriveC, "windows", "system32", "d3d12.dll")))
            return $"its prefix's d3d12.dll is Wine's own, not vkd3d-proton's: turn on VKD3D for it in {p.Launcher}";
        return null;
    }

    /// <summary>A d3d12.dll that is no stand-in of Wine's (a fake DLL says "Wine builtin DLL" or "Wine placeholder DLL" in its
    /// first bytes): vkd3d-proton's, as a launcher installs it.</summary>
    static bool Vkd3d(string dll)
    {
        try
        {
            using var f = GameFiles.OpenRegular(GameFiles.Find(Path.GetDirectoryName(dll)!, Path.GetFileName(dll)));
            if (f == null) return false;
            var head = new byte[4096];
            var span = head.AsSpan(0, f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false));
            return span.Length >= 64 && span.IndexOf("Wine builtin DLL"u8) < 0 && span.IndexOf("Wine placeholder DLL"u8) < 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    public const string StartsAnother = "Steam starts another game for it now: scan again";

    /// <summary>Why a game Steam runs can't be compiled when it is the Flatpak Steam's: <see cref="FlatpakSteamGames"/>, or a
    /// Vulkan warm (<paramref name="vulkan"/>), which runs natively; null for any other game.</summary>
    static string? FlatpakSteam(Game game, string? steamRoot, bool vulkan, bool mesa) =>
        !SteamRuns(game) ? null : FlatpakSteamGames(steamRoot ?? SteamSource.Root(), mesa) ?? (vulkan && SteamSource.Sandbox(steamRoot ?? SteamSource.Root()) == "Flatpak" ? FlatpakVulkanError : null);

    /// <summary>Why no game of the Steam in <paramref name="steamRoot"/> can be compiled when it is the Flatpak Steam: when
    /// SCSKiller runs as a Flatpak too (<see cref="InFlatpakError"/>, which can't record them either), on Mesa
    /// (<paramref name="mesa"/>), or without a systemd user manager, which lowers the priority of the sandbox's processes.
    /// Null for any other Steam.</summary>
    internal static string? FlatpakSteamGames(string steamRoot, bool mesa) =>
        SteamSource.Sandbox(steamRoot) != "Flatpak" ? null : LinuxStartup.InFlatpak ? InFlatpakError : mesa ? FlatpakSteamMesaError
        : ProtonTools.Find().SystemdRun == null ? FlatpakNoSystemdError : null;

    /// <summary>The program <paramref name="name"/> in PATH's folders; null when none has it.</summary>
    static string? OnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    static bool SteamRuns(Game game) => game.Store == Store.Steam || SteamShortcuts.Is(game.Id);

    /// <summary><see cref="ProtonContext.For(Game, string, out string?, IEnumerable{string}?)"/>, for a non-Steam game the
    /// shortcut's as Steam lists it now, still one SCSKiller lists (<see cref="SteamShortcutSource.GameOf"/>) and still
    /// <paramref name="game"/>: the same game folder, the same exe or one of that folder's (the exe SCSKiller follows the game
    /// running). A shortcut Steam starts from another folder now is listed again by a scan. Also refused when the game's
    /// Steam launch options (a shortcut's own) run it without vkd3d-proton, whose cache the warm fills.</summary>
    /// <param name="toolFolders">the system's compatibilitytools.d folders (default: Steam's)</param>
    internal static ProtonContext? Context(Game game, string steamRoot, out string? why, IEnumerable<string>? toolFolders = null)
    {
        ProtonContext? ctx;
        string? options;
        if (SteamShortcuts.AppId(game.Id) is { } app)
        {
            if (SteamShortcuts.Find(steamRoot, app) is not { } shortcut)
            {
                why = "Steam doesn't list it as a non-Steam game anymore";
                return null;
            }
            if (SteamShortcutSource.GameOf(steamRoot, shortcut, out why) is not { } now)
            {
                why ??= SteamShortcutSource.NoProton;
                return null;
            }
            if (!GameFiles.DirKey(now.InstallDir).Equals(GameFiles.DirKey(game.InstallDir), GameFiles.Names)
                || !now.ExePath.Equals(game.ExePath, GameFiles.Names) && !GameFiles.Inside(now.InstallDir, game.ExePath, GameFiles.Names))
            {
                why = StartsAnother;
                return null;
            }
            (ctx, options) = (ProtonContext.For(shortcut, steamRoot, out why, toolFolders), shortcut.LaunchOptions);
        }
        else (ctx, options) = (ProtonContext.For(game, steamRoot, out why, toolFolders), null);
        if (ctx == null) return null;
        // a script that can't be read is taken to read every variable
        var script = ProtonScript.Read(ctx.Proton);
        if (SteamLaunchOptions.OffVkd3d(options ?? SteamSource.LaunchOptions(steamRoot, ctx.AppId), name => script?.Reads(name) ?? true) is not { } off) return ctx;
        why = off;
        return null;
    }
}
