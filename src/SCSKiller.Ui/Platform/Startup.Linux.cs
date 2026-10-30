using System.Runtime.InteropServices;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Platform;

namespace SCSKiller.Ui;

/// <summary>Start when you sign in: the XDG autostart entry, or a Flatpak's Background portal (<see cref="LinuxStartup"/>),
/// real data only. And the CLI hand-off the driver-update service runs (<see cref="RewarmService"/>).</summary>
public partial class App
{
#if DEBUG
    const bool ReleaseBuild = false;
#else
    const bool ReleaseBuild = true;
#endif

    static readonly Lazy<(string? Program, string? Refused)> program = new(() => (LinuxStartup.ThisProgram(out var refused), refused));

    static string EntryFile => LinuxStartup.EntryFile(Environment.GetEnvironmentVariable);

    /// <summary><paramref name="byUser"/>: Settings' toggle changed, otherwise the start's reconcile, which also keeps the
    /// driver-update service on this copy. Debug builds leave the entry as it is.</summary>
    static async Task ApplyStartWithWindows(bool byUser)
    {
        if (!Host.Platform.StartAtSignIn) return;   // macOS: no XDG autostart or systemd
        var on = Host.Core.Settings.StartWithWindows;
        if (LinuxStartup.InFlatpak)
        {
            var granted = Host.Core.Settings.StartAtSignInGranted;
            if (Environment.ProcessPath is { } exe && await LinuxStartup.ApplyFlatpak(byUser, ReleaseBuild, on, exe, new BackgroundPortal(), granted) is var now && now != granted)
                Host.Core.Settings = Host.Core.Settings with { StartAtSignInGranted = now };
            return;
        }
        var (target, refused) = program.Value;
        if (refused != null && (byUser || on && File.Exists(EntryFile))) Log($"Start when you sign in: {refused}");
        bool installed;
        try { installed = Updater.Installed; }
        catch (Exception) { installed = false; }   // not known: not the installed app
        try
        {
            LinuxStartup.Apply(byUser, ReleaseBuild, on, target, installed, EntryFile, File.Exists);
            if (!byUser && ReleaseBuild && target != null && RewarmService.ThisCliRefused(target) is var cliRefused
                && RewarmService.Reconcile(target, cliRefused, installed, RewarmService.UnitDir(Environment.GetEnvironmentVariable), new RewarmService.Systemctl(), File.Exists)
                && cliRefused != null)
                Log($"Driver-update check: its service is removed: {cliRefused}");
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException) { Log($"Start when you sign in: {e.Message}"); }
    }

    static void Log(string line) => (Host.Core as ScsKiller)?.Log?.Report(line);

    /// <summary>The entry as it is, or a Flatpak's <see cref="LinuxStartup.FlatpakShows"/>. A Debug build shows the setting.</summary>
    static bool? StartsAtSignIn() => !ReleaseBuild || !Host.Platform.StartAtSignIn ? null
        : LinuxStartup.InFlatpak ? LinuxStartup.FlatpakShows(Host.Core.Settings)
        : program.Value.Program is { } exe && LinuxStartup.IsOn(exe, EntryFile);

    /// <summary>`SCSKiller.Ui --cli &lt;command&gt;`: the CLI beside the app runs in this process's place (execv), for the
    /// driver-update service, before anything else of a start (Velopack, a window, the tray). An AppImage's service runs
    /// the AppImage's file with these arguments, which its AppRun passes through to here, so the CLI it starts is its
    /// own image's. Null for any other start.</summary>
    public static int? RunCli(string[] args)
    {
        if (args is not [RewarmService.CliArg, .. var rest]) return null;
        var cli = Path.Combine(AppContext.BaseDirectory, RewarmService.Cli);
        execv(cli, [cli, .. rest, null]);
        var why = Marshal.GetPInvokeErrorMessage(Marshal.GetLastPInvokeError());   // before Console.Error, whose setup calls libc too
        Console.Error.WriteLine($"error: couldn't start {cli}: {why}");
        return 127;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int execv([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv);
}
