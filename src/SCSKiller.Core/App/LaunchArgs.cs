using System.Runtime.InteropServices;

namespace SCSKiller.Core.App;

/// <summary>The CLI's commands: its dispatch and the app's refusal of a command given to it (<see cref="LaunchArgs"/>) share them.</summary>
public static class CliCommands
{
    public const string Help = "help", Version = "version", Scan = "scan", Status = "status", Add = "add", Exe = "exe", Compile = "compile",
        Queue = "queue", BeforeAfter = "before-after", RewarmStale = "rewarm-stale", Cache = "cache", Record = "record", Shared = "shared",
        Index = "index", Rehydrate = "rehydrate", Reference = "reference", Key = "key", Task = "task", ShaderCleanup = "shader-cleanup",
        ProtonEnv = "proton-env", SteamPlace = "steam-place", NvidiaSnapshot = "nvidia-snapshot", NvidiaAutoShader = "nvidia-auto-shader",
        FetchCodecs = "fetch-codecs";

    public static readonly string[] All = [Help, Version, Scan, Status, Add, Exe, Compile, Queue, BeforeAfter, RewarmStale, Cache, Record, Shared,
        Index, Rehydrate, Reference, Key, Task, ShaderCleanup, ProtonEnv, SteamPlace, NvidiaSnapshot, NvidiaAutoShader, FetchCodecs];
}

/// <summary>A launch of the app (SCSKiller.exe, the AppImage) whose first argument asks for the version or the usage, names a CLI
/// command, or is an option the app doesn't take: it prints and exits before anything of a start, never opening the window
/// or the data folder. Any other first argument starts the app as before.</summary>
public static class LaunchArgs
{
    // Velopack's hook arguments (Velopack.dll's VelopackApp), before Known: static fields start in this order
    static readonly string[] VelopackHooks = [.. new[] { "install", "updated", "obsolete", "uninstall" }.SelectMany(h => new[] { "--veloapp-" + h, "--squirrel-" + h })];

    /// <summary>The options the app is started with: by the user, its sign-in entry, the driver-update task, a CLI run's notice,
    /// the offline cleanup, Velopack's hooks and COM activation (-Embedding).</summary>
    static readonly string[] Known = [WindowsStartup.TrayArg, ScheduledTask.DriverUpdatedArg, ScsKiller.NoticeArg, ScsKiller.CleanupArg,
        "--fake", "--screenshots", "--memory", .. VelopackHooks, "-Embedding"];

    // with a payload appended: a toast's relaunch on Windows, a Finder launch on macOS
    static readonly string[] KnownPrefixes = ["----AppNotificationActivated:", "-psn_"];

    /// <summary>The exit code of a launch that prints instead of starting (0 for the version or the usage, 2 otherwise), else null.</summary>
    public static int? Handled(string[] args)
    {
        if (args.Length == 0) return null;
        var first = args[0];
        void stdout(string line) { Attach(); Console.Out.WriteLine(line); }
        void stderr(string line) { Attach(); Console.Error.WriteLine(line); }
        if (first is "--version" or "-v") { stdout(AppVersion.Current.ToString()); return 0; }
        if (first is "--help" or "-h") { stdout(Usage); return 0; }
        if (CliCommands.All.Contains(first, StringComparer.Ordinal))
        {
            stderr($"{first} is a command of SCSKiller's CLI\n{Usage}");
            return 2;
        }
        if (first.StartsWith('-') && !Known.Contains(first, StringComparer.Ordinal) && !KnownPrefixes.Any(p => first.StartsWith(p, StringComparison.Ordinal)))
        {
            stderr($"unknown argument: {first}\n{Usage}");
            return 2;
        }
        return null;
    }

    // a GUI exe has no console of its own: the shell's that started it, when there is one
    static void Attach()
    {
        if (OperatingSystem.IsWindows()) AttachConsole(-1);
    }

    [DllImport("kernel32.dll")] static extern bool AttachConsole(int processId);

    static string Usage => OperatingSystem.IsWindows()
        ? "usage: SCSKiller [--version | --help]\nCommands go to the CLI in the app's cli folder: scskiller <command>, for example scskiller status <game>."
        : "usage: SCSKiller [--version | --help]\nCommands go to the CLI: run this app with --cli <command>, for example --cli status <game>.";
}
