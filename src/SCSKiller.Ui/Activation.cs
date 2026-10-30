using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Pages;
using SCSKiller.Ui.Platform;

namespace SCSKiller.Ui;

/// <summary>Toasts and their clicks, another launch's activation and the starts without a window (--tray, --driver-updated,
/// a toast click), as the WinUI app handles them.</summary>
static class Activation
{
    /// <summary>`SCSKiller.exe --memory` while the app runs: a line of what it holds in memory.log, for a bug report. Counts
    /// only, nothing of the process's contents. The window stays as it is.</summary>
    const string MemoryArg = "--memory", DriverUpdatedArg = "--driver-updated";

    static bool checkingDriver, checkAgain, newsReady;
    static readonly DriverNotice driverNotice = new(Notifications.Driver);
    static Func<Task> quit = () => Task.CompletedTask;

    public static bool DriverUpdated(string[] args) => args.Contains(DriverUpdatedArg);

    /// <summary>Game folders are this instance's to manage (<see cref="InstanceIdentity.GameFolders"/>, <see cref="SingleInstance.Held"/>).</summary>
    public static bool ManagesRecorders => SingleInstance.Held && Program.Identity.GameFolders && !OperatingSystem.IsMacOS();   // no recorder for macOS yet

    /// <summary>After the main window is built. True: show it now. The window's activation turns game-folder writes back on
    /// after a start that left them off (--driver-updated: nobody at the PC).</summary>
    /// <param name="toast">False after the alpha gate: a toast's activation has expired by then, its arguments can't be read.</param>
    public static bool Start(ScsKiller real, string[] args, Func<Task> quitAsync, bool toast = true)
    {
        quit = quitAsync;
        if (Notifications.Register(a => Host.Ui.TryEnqueue(() => OnToast(a)))) App.Hint = Notifications.Hint;
        SingleInstance.Listen(line => Host.Ui.TryEnqueue(() => OnLaunched(line)), a => Host.Ui.TryEnqueue(() => OnToast(a)));
        if (ManagesRecorders)
        {
            App.Main.Activated += (_, _) =>
            {
                if (real.ManageRecorders) return;
                real.ManageRecorders = true;
                Task.Run(() => real.ReconcileRecorders());
            };
        }
        bool tray = SignInStart.Is(args);
        switch (SignInStart.For(args, OperatingSystem.IsWindows()))
        {
            case SignInStart.Pass.ScanThenDriverCheck: NotifyDriverUpdate(exitIfNothing: false); break;
            case SignInStart.Pass.Scan: _ = real.ScanAsync(CancellationToken.None); break;   // no window to scan: the game watcher and the notifications need the games
        }
        if (toast && SingleInstance.LaunchToast() is { } launched)
        {
            // OnToast shows the window for everything but "idle" and "skip"
            OnToast(launched, relaunched: true);
            return false;
        }
        if (DriverUpdated(args))
        {
            // no window unless the user picks Compile now or the toast body
            NotifyDriverUpdate(exitIfNothing: true);
            return false;
        }
        ShowDriverToast();
        newsReady = true;
        ToastNews();
        return !tray;
    }

    /// <summary>A notification for each unread news item not shown yet (<see cref="NewsState.ToastDue"/>), once the window and
    /// the notification area exist.</summary>
    public static void ToastNews()
    {
        if (!newsReady || !Notifications.Available) return;
        var (shown, _) = MainWindow.Notifications();
        var (show, more) = NewsState.ToastDue(Host.Store, shown, MainWindow.Feed(), DateOnly.FromDateTime(DateTime.Now));
        foreach (var item in show) Notifications.Show(Toasts.News(item));
        if (more > 0) Notifications.Show(Toasts.NewsMore(more));
    }

    /// <summary>A driver update found by a scan, a compile or the watcher's check: the scan that follows lists the games it
    /// made stale.</summary>
    public static void GpuChanged()
    {
        if (Host.Core is ScsKiller { GpuRestartNote: null }) NotifyDriverUpdate(exitIfNothing: false);
    }

    /// <summary>One scan at a time (UI thread): a request while one runs scans again after it, since only a scan started
    /// after the request sees what it was about.</summary>
    static void NotifyDriverUpdate(bool exitIfNothing)
    {
        if (checkingDriver) { checkAgain = true; return; }
        checkingDriver = true;
        Host.Core.ScanAsync(CancellationToken.None).ContinueWith(_ => Host.Ui.TryEnqueue(() =>
        {
            checkingDriver = false;
            if (checkAgain)
            {
                checkAgain = false;
                NotifyDriverUpdate(exitIfNothing);
            }
            else if (ShowDriverToast() is var shown && exitIfNothing && !App.Main.IsVisible)   // opened meanwhile: stays
            {
                if (!shown) _ = quit();
                // Right away queued them: the unattended launch leaves once they are compiled, unless the window was opened
                else if (Host.Core is ScsKiller { Settings.DriverRightAway: true } k)
                    _ = k.WhenQueueIdle().ContinueWith(_ => Host.Ui.TryEnqueue(() => { if (!App.Main.IsVisible) _ = quit(); }));
            }
        }));
    }

    /// <summary>The driver-update notification, once per driver and set of stale games, or Right away's compile with no
    /// notification; false when there is nothing to notify or compile.</summary>
    static bool ShowDriverToast() => (Host.Core as ScsKiller)?.RewarmRightAway() > 0 || Notifications.Available && driverNotice.Show(Host.Core);

    /// <summary>Another launch handed its activation to this instance: the shortcut or the app again (show the window), the
    /// sign-in entry (already here), the driver-update task (check and notify).</summary>
    static void OnLaunched(string line)
    {
        if (line.Contains(DriverUpdatedArg)) NotifyDriverUpdate(exitIfNothing: false);
        else if (line.Contains(MemoryArg)) LogMemory();
        else if (!line.Contains(WindowsStartup.TrayArg)) App.ShowWindow();
    }

    static void LogMemory()
    {
        if (Host.Core is ScsKiller k)
            Task.Run(() => ScsKiller.AppendLog(k.Store.DataDir, ProcessMemory.LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {ProcessMemory.Line()}", 1 << 20));
    }

    /// <summary>The window's close button hid it to the notification area: the first time ever, say where it went.
    /// Notifications off: shown once they're on.</summary>
    public static void TrayNotice()
    {
        var core = Host.Core;
        if (core.Settings.TrayNoticeShown || !Notifications.Available) return;
        core.Settings = core.Settings with { TrayNoticeShown = true };
        Notifications.TrayNotice();
    }

    /// <summary>The new-shaders notifications for what <see cref="NewShaders.Plan"/> found due: one for the games it queued
    /// "when idle", one asking about the rest.</summary>
    public static void NewShaders(IReadOnlyList<GameState> due, IReadOnlyCollection<GameState> compile)
    {
        if (!Notifications.Available) return;
        var compiling = due.Where(compile.Contains).ToList();
        if (compiling.Count > 0) Notifications.NewShaders(compiling, compiling: true);
        if (due.Except(compiling).ToList() is { Count: > 0 } ask) Notifications.NewShaders(ask, compiling: false);
    }

    /// <summary>A toast clicked after the app was quit starts it with no games known yet.</summary>
    static void AfterScan(Action act)
    {
        if (Host.Core.Games.Count > 0) act();
        else Host.Core.ScanAsync(CancellationToken.None).ContinueWith(_ => Host.Ui.TryEnqueue(act));
    }

    /// <summary><paramref name="relaunched"/>: the click started the app, which only stays for what the click queued.</summary>
    static void OnToast(IDictionary<string, string> args, bool relaunched = false)
    {
        var core = Host.Core;
        // called after the scan: a driver toast names no games, they are the stale ones
        string[] Ids() => args.TryGetValue("games", out var list) ? list.Split('|') : core.DriverStaleGames().Select(g => g.Game.Id).ToArray();
        switch (args.TryGetValue("action", out var a) ? a : null)
        {
            case "shaders-compile":
            case "now":
                AfterScan(() => CompileNow(Ids()));
                break;
            case "shaders-show":
                AfterScan(() =>
                {
                    var ids = Ids();
                    if (ids.Length == 1 && core.Games.Any(s => s.Game.Id == ids[0])) App.Main.Navigate(typeof(DetailPage), ids[0]);
                    else App.Main.Navigate(typeof(LibraryPage));
                    App.ShowWindow();
                });
                break;
            case "news":
                AfterScan(() =>
                {
                    App.ShowWindow();
                    App.Main.OpenBell();
                });
                break;
            case "idle":
                AfterScan(() => { foreach (var id in Ids()) core.EnqueueWhenIdle(id); });
                break;
            case "skip":
                AfterScan(() =>
                {
                    core.DismissStale();
                    if (relaunched) _ = quit();   // nothing queued: no reason to stay resident
                });
                break;
            default:   // toast body clicked
                App.ShowWindow();
                break;
        }
    }

    static void CompileNow(IEnumerable<string> ids)
    {
        foreach (var id in ids) Host.Core.Enqueue(id);
        Host.Core.StartQueue();
        App.Main.Navigate(typeof(QueuePage));
        App.ShowWindow();
    }
}
