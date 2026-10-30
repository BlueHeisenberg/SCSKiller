using System.Runtime.InteropServices;
using Avalonia;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Platform;
using Velopack;

namespace SCSKiller.Ui;

static class Program
{
#if DEBUG
    const bool Release = false;
#else
    const bool Release = true;
#endif

    /// <summary>The single-instance key and toast identity of a real run.</summary>
    public static InstanceIdentity Identity { get; private set; } = null!;

    // Args: --cli <command> (Linux: the CLI beside the app in its place), --fake (sample data), --screenshots <dir> (sample
    // data, rendered off-screen to PNG files, then exit), else the real core on the data folder (SCSKILLER_DATA_DIR moves
    // it): one instance per data folder, a second launch hands its activation to the running one and exits. Design runs
    // register nothing, redirect nowhere and write no crash log.
    [STAThread]
    public static int Main(string[] args)
    {
        Core.App.Format.PlainGroupSeparator();
        if (App.RunCli(args) is { } cli) return cli;
        Host.Platform = Features.Current;
        if (!Host.Platform.Tray) args = [.. args.Where(a => a != WindowsStartup.TrayArg)];   // no icon to come back from
        int shots = Array.IndexOf(args, "--screenshots");
        if (shots >= 0) return Screenshots.Run(shots + 1 < args.Length ? args[shots + 1] : "screenshots");
        bool real = !IsDesignRun(args);
        if (real)
        {
            // logged, not handled: the app still ends as it would have
            AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception ?? new Exception($"{e.ExceptionObject}"));
            TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(e.Exception);
            // Update.exe runs the install, update and uninstall hooks through here, and they exit: before anything else of a
            // real run, and in Main itself, where vpk checks for it. It sets the locator Updater.Installed needs. A Flatpak or
            // a package never starts Velopack: its own manager updates it.
            if (Installation.ThisElsewhere == null) UpdateHooks.Hooks(VelopackApp.Build()).Run();
            if (AppStore.RefusesLiveData())
            {
                if (OperatingSystem.IsWindows()) MessageBoxW(0, AppStore.DevBuildRefusal, "SCSKiller", 0x10 /* MB_ICONERROR */);
                else Console.Error.WriteLine(AppStore.DevBuildRefusal);
                return 1;
            }
            // an offline session's cleanup helper (ScsKiller.CleanupHelper is this exe): no window
            if (args is [ScsKiller.CleanupArg, var game]) return ScsKiller.RunOfflineCleanup(new AppStore(AppStore.DefaultDir), game, exe: Environment.ProcessPath);
            if (SignInStart.Quits(args, AlphaGate.Needed(AppVersion.Current), () => AlphaGate.Passed(AppStore.DefaultDir))) return 0;
            if (InstanceIdentity.RefusesStart(Release, AppStore.Moved, Installed, args)) return 0;
            Identity = InstanceIdentity.For(Release, AppStore.Moved, AppStore.DefaultDir);
        }
        try
        {
            if (real)
            {
                Notifications.UseIdentity(Identity);   // before any window
                if (SingleInstance.RedirectedToRunning(Identity, toastStart: args.Any(a => a.StartsWith(InstanceIdentity.ToastRelaunchArg, StringComparison.Ordinal)))) return 0;
            }
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception e) when (real && !App.Started)
        {
            if (StartFailed(e)) throw;
            return 1;
        }
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => App.WithSelawik(AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace());

    static bool Installed()
    {
        try { return Updater.Installed; }
        catch (Exception) { return false; }   // not known: not the installed app
    }

    public static bool IsDesignRun(string[] args) => args.Any(a => a is "--fake" or "--screenshots");

    static void LogCrash(Exception e) => CrashLog.Write(AppStore.DefaultDir, e);

    /// <summary>Logs a start that failed and says where the log is, as the WinUI app does. True: rethrown, as there. Linux
    /// returns an exit code instead: rethrown, the runtime would abort the process and dump its core.</summary>
    static bool StartFailed(Exception e)
    {
        var logged = CrashLog.Write(AppStore.DefaultDir, e);
        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(0, $"SCSKiller couldn't start. Details are in {Path.Combine(AppStore.DefaultDir, CrashLog.FileName)}", "SCSKiller", 0x10 /* MB_ICONERROR */);
            return true;
        }
        var failure = StartFailure.Of(e, AppStore.DefaultDir, logged);
        Console.Error.WriteLine(failure.Text);
        StartFailedWindow.Show(failure);
        return false;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
