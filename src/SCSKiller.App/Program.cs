using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Velopack;

namespace SCSKiller.App;

/// <summary>Our own entry point (DISABLE_XAML_GENERATED_MAIN): one SCSKiller per user session. A second launch (the
/// shortcut clicked while the app waits in the notification area, the sign-in entry, the driver-update task's
/// --driver-updated) hands its activation to the running instance (App.OnActivated) and exits. Design-data runs
/// (--fake, --screenshots) never register nor redirect: they must not reach a real running instance.</summary>
public static class Program
{
    public const string InstanceKey = "SCSKiller";

    [STAThread]
    static int Main(string[] args)
    {
        if (IsDesignRun(args)) Core.App.UpdateHooks.DesignDir = Path.Combine(Path.GetTempPath(), $"SCSKiller-fake-{Environment.ProcessId}");
        Core.App.Format.PlainGroupSeparator();
        // logged, not handled: the app still ends as it would have
        AppDomain.CurrentDomain.UnhandledException += (_, e) => LogCrash(e.ExceptionObject as Exception ?? new Exception($"{e.ExceptionObject}"));
        TaskScheduler.UnobservedTaskException += (_, e) => LogCrash(e.Exception);
        // Update.exe runs the install, update and uninstall hooks through here, and they exit: before anything but the crash
        // handlers, and in Main itself, where vpk checks for it
        Core.App.UpdateHooks.Hooks(VelopackApp.Build()).Run();
        if (!IsDesignRun(args) && Core.App.AppStore.RefusesLiveData())
        {
            MessageBoxW(0, Core.App.AppStore.DevBuildRefusal, "SCSKiller", 0x10 /* MB_ICONERROR */);
            return 1;
        }
        // an offline session's cleanup helper: no window, no instance of its own
        if (args is [Core.App.ScsKiller.CleanupArg, var game]) return Core.App.ScsKiller.RunOfflineCleanup(new Core.App.AppStore(Core.App.AppStore.DefaultDir), game, exe: Environment.ProcessPath);
        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            if (!IsDesignRun(args) && RedirectedToRunning(args)) return 0;
            Microsoft.UI.Xaml.Application.Start(p =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                new App();
            });
        }
        catch (Exception e)
        {
            StartFailed(e, args);
            throw;
        }
        return 0;
    }

    public static void LogCrash(Exception e) => Core.App.CrashLog.Write(Core.App.UpdateHooks.DataDir, e);

    /// <summary>Logs a start that failed and, unless it is a design-data run, which nobody watches, says where the log is.</summary>
    public static void StartFailed(Exception e, string[] args)
    {
        LogCrash(e);
        if (!IsDesignRun(args)) MessageBoxW(0, $"SCSKiller couldn't start. Details are in {Path.Combine(Core.App.AppStore.DefaultDir, Core.App.CrashLog.FileName)}", "SCSKiller", 0x10 /* MB_ICONERROR */);
    }

    public static bool IsDesignRun(string[] args) => args.Any(a => a is "--fake" or "--screenshots");

    static bool RedirectedToRunning(string[] args)
    {
        var key = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (key.IsCurrent) return false;
        // a toast click's COM relaunch exits and the click is lost: the running instance crashes reading a redirected toast's args
        if (args.Any(a => a.StartsWith("----AppNotificationActivated:", StringComparison.Ordinal))) return true;
        AllowSetForegroundWindow(key.ProcessId);   // its window may come to the front: this launch has the user's click
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        using var done = new ManualResetEvent(false);
        // On another thread: awaiting the redirect on this STA thread hangs. WaitOne pumps COM meanwhile.
        Task.Run(async () =>
        {
            try { await key.RedirectActivationToAsync(activation); }
            finally { done.Set(); }
        });
        done.WaitOne(TimeSpan.FromSeconds(30));
        return true;
    }

    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int MessageBoxW(nint hwnd, string text, string caption, uint type);
}
