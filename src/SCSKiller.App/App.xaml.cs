using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using SCSKiller.App.Design;
using SCSKiller.App.Pages;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.App;

public partial class App : Application
{
    public static IScsKiller Core { get; private set; } = null!;
    public static MainWindow Main { get; private set; } = null!;
    /// <summary>Sign in with Patreon: <see cref="Account.SignInAsync"/> is the entry point for Settings and the welcome dialog.</summary>
    public static Account Account { get; private set; } = null!;

    /// <summary>At the session's end (shutdown, restart, sign-out), how long a running compile gets to stop before Windows
    /// goes on (it won't wait much longer); the warm job then kills what is left. A stopped warm exits once its in-flight
    /// items finish; the NVIDIA driver then writes its cache as the process exits, which took ~4 min after a full Jedi
    /// Survivor warm (3.49 GB). Ending the process earlier loses what the driver hadn't written: the game compiles those
    /// pipelines itself when it meets them (the stutter this app prevents), and the next warm redoes them, since an ended
    /// run keeps its old resume point. AMD writes during the run: only the in-flight items are lost. The tray's Quit waits
    /// instead (<see cref="QuitAsync"/>).</summary>
    static readonly TimeSpan SessionEndWait = TimeSpan.FromSeconds(10);

    static Tray? tray;
    static bool notifications, toldAboutTray, quitting, checkingDriver, checkAgain;
    static string? toldDriver;   // the driver and games the last driver-update notification was about
    static Dictionary<string, string>? told;   // NewShaders' notified store
    static TaskCompletionSource? quitNow;   // set while quitting waits for the compile to finish: the tray's Quit again ends it
    static readonly CancellationTokenSource stopWatching = new();
    static Task watcher = Task.CompletedTask;

    public App() => InitializeComponent();

    public static bool Quitting => quitting;
    /// <summary>Closing the window hides it to the notification area (the tray's Quit really quits).</summary>
    public static bool HidesOnClose => tray is { Added: true };   // also while quitting waits: closing must not end the process

    // Args: --fake (sample data), --screenshots <dir> (fake data, off-screen, never activated, then exit),
    // --driver-updated (PLATFORM's scheduled task after a driver update leaves stale games in Ask mode),
    // --tray (the sign-in entry, WindowsStartup: starts in the notification area, no window).
    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        int shots = Array.IndexOf(args, "--screenshots");
        bool driverUpdated = args.Contains("--driver-updated");
        Core = shots >= 0 || args.Contains("--fake") ? new FakeScsKiller() : ScsKiller.CreateDefault();
        // The design data starts signed out and never touches the real auth.dat.
        var fakeDir = Path.Combine(Path.GetTempPath(), $"SCSKiller-fake-{Environment.ProcessId}");
        Account = shots >= 0 ? FakeAccount.Create(fakeDir) : new Account(Core is FakeScsKiller ? fakeDir : AppStore.DefaultDir);   // screenshots: a fake server
        if (Core is ScsKiller real)
        {
            real.Community = new Community(AppStore.DefaultDir, Account.GetDbTokenAsync);
            real.Sharing = new Sharing(AppStore.DefaultDir, () => real.Settings.ShareRecordings);   // anonymous: never the Patreon sign-in
            real.ContentRoutes = RouteFailover.Default;
            // the entitlements first: the update check picks the channel they allow
            real.UserFetch = async () => { await Account.RefreshAsync(); await Updater.CheckAsync(); };
            // not before the welcome, which says it is sent and where to turn it off
            real.ActiveCheck = new ActiveCheck(AppStore.DefaultDir, () => real.Settings is { ActiveCheck: true, WelcomeSeen: true }, real.Vendor.Vendor);
            // not in the unattended --driver-updated launch: nobody at the PC, no game folder is written
            real.ManageRecorders = !driverUpdated;
            real.CleanupHelper = Environment.ProcessPath;
            real.CheckPlans = true;
            var hadDb = false;   // signed in (or the membership turned active): check at once, not at the next scan
            Account.Changed += () => { var db = Account.Status?.Ent.Contains("db") == true; if (db && !hadDb) real.StartCommunitySync(); hadDb = db; };
            watcher = real.WatchGames(stopWatching.Token);
        }
        Main = new MainWindow();
        if (shots < 0) Main.RestorePlacement(Core is FakeScsKiller ? fakeDir : AppStore.DefaultDir);   // screenshots: always the default size
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Main);
        if (Core is ScsKiller seen) seen.Unseen = () => !IsWindowVisible(hwnd) || IsIconic(hwnd);

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        string? toastError = null;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, a) => Main.DispatcherQueue.TryEnqueue(() => OnToast(a.Arguments));
            AppNotificationManager.Default.Register();
        }
        catch (Exception ex) { toastError = ex.Message; }   // the app works without notifications
        notifications = toastError == null;

        if (shots >= 0)
        {
            string dir = shots + 1 < args.Length ? args[shots + 1] : "screenshots";
            Directory.CreateDirectory(dir);
            // Built, never shown: the toast's XML, or why notifications can't register.
            File.WriteAllText(Path.Combine(dir, "toast.xml"), toastError ?? DriverToast(Core.Games.Take(1).ToList()).Payload);
            if (toastError == null)
            {
                File.WriteAllText(Path.Combine(dir, "toast-new-shaders.xml"), NewShadersToast([Core.Games[0] with { RecordedSinceWarm = 42 }]).Payload);
                File.WriteAllText(Path.Combine(dir, "toast-new-shaders-games.xml"), NewShadersToast(Core.Games.Take(3).ToList()).Payload);
            }
            _ = Main.TakeScreenshotsAsync(dir, (FakeScsKiller)Core);
            return;   // no tray icon, no notification, no single-instance registration
        }

        // design data: the bundled text, no network
        if (!Core.Settings.WelcomeSeen) ShowWelcome(Core is FakeScsKiller ? Task.FromResult<WelcomeContent?>(null) : WelcomeContent.FetchAsync());

        tray = CreateTray();
        UpdateTip();
        if (Core is not FakeScsKiller)   // the design data never touches the registry or the running app
        {
            AppInstance.GetCurrent().Activated += (_, a) => Main.DispatcherQueue.TryEnqueue(() => OnActivated(a));
            // the unattended --driver-updated launch leaves game folders alone only until someone opens the window
            Main.Activated += (_, a) =>
            {
                if (a.WindowActivationState == WindowActivationState.Deactivated || Core is not ScsKiller { ManageRecorders: false } r) return;
                r.ManageRecorders = true;
                Task.Run(() => r.ReconcileRecorders());
            };
            // a game played while the window was away changed its cache size
            var lastSizes = DateTime.MinValue;
            Main.Activated += (_, a) =>
            {
                if (a.WindowActivationState == WindowActivationState.Deactivated || DateTime.UtcNow - lastSizes < TimeSpan.FromSeconds(10)) return;
                lastSizes = DateTime.UtcNow;
                Task.Run(Core.RefreshCacheSizes);
            };
            if (Core is ScsKiller gpu) gpu.GpuChanged += () => Main.DispatcherQueue.TryEnqueue(OnGpuChanged);
            ApplyStartWithWindows();
            Updater.Start();
            if (activation.Kind == ExtendedActivationKind.Launch) _ = Updater.ApplyAtStartAsync(args[1..]);
            if (Core is ScsKiller k && !driverUpdated)
            {
                var check = new Coalesced(Main.DispatcherQueue, () => NotifyNewShaders(k.Store));
                Core.GameChanged += _ => check.Request();
            }
            if (Updater.HasResume)   // restarted by "Restart to update": the queue goes on once the games are known
                _ = Core.ScanAsync(CancellationToken.None).ContinueWith(_ => Main.DispatcherQueue.TryEnqueue(Updater.ResumeQueue));
            else if (args.Contains(WindowsStartup.TrayArg))   // no window to scan: the game watcher and the notifications need the games
                _ = Core.ScanAsync(CancellationToken.None);
        }

        if (activation.Kind == ExtendedActivationKind.AppNotification)
        {
            // Relaunched by a toast click: OnToast activates the window for everything but "idle" and "skip".
            var toastArgs = ((AppNotificationActivatedEventArgs)activation.Data).Arguments;
            OnToast(toastArgs, relaunched: true);
            return;
        }

        if (driverUpdated)
        {
            // Stay in the notification area (no window) unless the user later picks Compile now or the toast body; the
            // NotificationInvoked handler above then runs OnToast in this same process.
            NotifyDriverUpdate(exitIfNothing: true);
            return;
        }

        if (!args.Contains(WindowsStartup.TrayArg)) Main.Activate();
        ShowDriverToast();
    }

    /// <summary>One scan at a time (UI thread): a request while one runs scans again after it, since only a scan started
    /// after the request sees what it was about.</summary>
    static void NotifyDriverUpdate(bool exitIfNothing)
    {
        if (checkingDriver) { checkAgain = true; return; }
        checkingDriver = true;
        Core.ScanAsync(CancellationToken.None).ContinueWith(_ => Main.DispatcherQueue.TryEnqueue(() =>
        {
            checkingDriver = false;
            if (checkAgain)
            {
                checkAgain = false;
                NotifyDriverUpdate(exitIfNothing);
            }
            else if (!ShowDriverToast() && exitIfNothing) _ = QuitAsync();
        }));
    }

    /// <summary>The driver-update notification, once per driver and set of stale games; false when there is nothing to notify.</summary>
    static bool ShowDriverToast()
    {
        if (!notifications || !Core.ShouldNotifyStale()) return false;
        var stale = Core.DriverStaleGames();
        var about = $"{(Core as ScsKiller)?.DriverId}|{string.Join('|', stale.Select(g => g.Game.Id).Order())}";
        if (about != toldDriver) AppNotificationManager.Default.Show(DriverToast(stale));
        toldDriver = about;
        return true;
    }

    /// <summary>A driver update found by a scan, a compile or the watcher's check: the scan that follows lists the games it
    /// made stale.</summary>
    static void OnGpuChanged()
    {
        Main.ShowGpu();
        if (Core is ScsKiller { GpuRestartNote: null }) NotifyDriverUpdate(exitIfNothing: false);
    }

    /// <summary>Another launch handed its activation to this instance (Program): the shortcut or the app again (show the
    /// window), the sign-in entry (already here), the driver-update task (check and notify), a toast.</summary>
    static void OnActivated(AppActivationArguments a)
    {
        if (a.Kind == ExtendedActivationKind.AppNotification) { OnToast(((AppNotificationActivatedEventArgs)a.Data).Arguments); return; }
        var line = (a.Data as Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs)?.Arguments ?? "";
        if (line.Contains("--driver-updated")) NotifyDriverUpdate(exitIfNothing: false);
        else if (!line.Contains(WindowsStartup.TrayArg)) ShowWindow();
    }

    /// <summary>Brings the window back from the notification area (or from behind other windows).</summary>
    public static void ShowWindow()
    {
        if (Main.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p) p.Restore();
        Main.AppWindow.Show();
        Main.Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(Main));
    }

    /// <summary>The window's close button: hide to the notification area; the first time per run, say where it went.</summary>
    public static void HideToTray()
    {
        Main.AppWindow.Hide();
        if (toldAboutTray || !notifications) return;
        toldAboutTray = true;
        AppNotificationManager.Default.Show(new AppNotificationBuilder()
            .AddArgument("action", "open")
            .AddText("SCSKiller is still running")
            .AddText("It's in the notification area; right-click the icon to quit.")
            .BuildNotification());
    }

    /// <summary>The first-run welcome, once the window is first shown (a --tray or toast start shows it later): the server's
    /// text (<paramref name="fetch"/>, at most 3 s) or the bundled one, as plain text. Closing it either way marks it seen and saves its
    /// share checkbox (<see cref="Settings.ShareRecordings"/>).</summary>
    static void ShowWelcome(Task<WelcomeContent?> fetch)
    {
        var root = (FrameworkElement)Main.Content;
        root.Loaded += OnLoaded;

        async void OnLoaded(object _, RoutedEventArgs __)
        {
            root.Loaded -= OnLoaded;
            var c = await fetch ?? WelcomeContent.Default;
            var panel = new StackPanel { Spacing = 12 };
            foreach (var p in c.Paragraphs) panel.Children.Add(new TextBlock { Text = p, TextWrapping = TextWrapping.Wrap });
            if (c.Link != null) panel.Children.Add(new HyperlinkButton { Content = c.Link.Text, NavigateUri = new Uri(c.Link.Url), Padding = new Thickness(0) });
            var share = new CheckBox { Content = c.Share, IsChecked = false };   // never pre-ticked: that isn't consent
            panel.Children.Add(share);
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot, Title = c.Title, Content = new ScrollViewer { Content = panel },
                PrimaryButtonText = c.SignIn, CloseButtonText = c.Dismiss, DefaultButton = ContentDialogButton.Primary,
            };
            ContentDialogResult result;
            try { result = await dialog.ShowAsync(); }
            catch (COMException) { return; }   // another dialog is open (the user got there first): next start
            Core.Settings = Core.Settings with { WelcomeSeen = true, ShareRecordings = share.IsChecked == true };   // either button, or Esc
            if (result == ContentDialogResult.Primary) SignInWithPatreon();
        }
    }

    /// <summary>A question with Cancel and one action; true = the action was chosen.</summary>
    public static async Task<bool> ConfirmAsync(Page page, string title, object content, string primaryText, ContentDialogButton defaultButton = ContentDialogButton.Close) =>
        await new ContentDialog
        {
            XamlRoot = page.XamlRoot, Title = title, Content = content, PrimaryButtonText = primaryText, CloseButtonText = "Cancel", DefaultButton = defaultButton,
        }.ShowAsync() == ContentDialogResult.Primary;

    /// <summary>The welcome's sign-in button: starts the browser flow and shows Settings, whose Account card follows it.</summary>
    public static void SignInWithPatreon()
    {
        _ = Account.SignInAsync();
        Main.Navigate(typeof(SettingsPage));
    }

    /// <summary>The sign-in entry follows the setting (and the exe, if the app moved). Real data only, and only in Release
    /// builds (build\publish.ps1): a Debug build from a checkout or worktree must not point the sign-in entry at
    /// itself. In Debug the setting is saved but the registry is left as it is.</summary>
    public static void ApplyStartWithWindows()
    {
#if !DEBUG
        if (Core is FakeScsKiller || Environment.ProcessPath is not { } exe) return;
        try { WindowsStartup.Apply(Core.Settings.StartWithWindows, exe); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }   // a policy-locked key: the setting just doesn't apply
#endif
    }

    static Tray CreateTray()
    {
        var t = new Tray(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"))
        {
            Open = ShowWindow,
            PauseLabel = () => Running() is not { } q ? null : q.Stage == QueueStage.Paused ? "Resume" : "Pause compiling",
            PauseOrResume = () => Fmt.PauseOrResume(Running()),
            Quit = () => _ = QuitAsync(),
            QuitLabel = () => quitNow != null ? "Quit now (loses unsaved cache)" : "Quit",
            SessionEnding = hwnd =>
            {
                if (Running() == null) return;
                ShutdownBlockReasonCreate(hwnd, "Stopping the compile so the graphics driver can save its shader cache");
                StopAll();
            },
            SessionEnd = hwnd =>
            {
                // the session ends when this returns: bounded, then the warm job kills whatever is left
                for (var clock = System.Diagnostics.Stopwatch.StartNew(); Running() != null && clock.Elapsed < SessionEndWait;) Thread.Sleep(100);
                ShutdownBlockReasonDestroy(hwnd);
            },
        };
        var tip = new Coalesced(Main.DispatcherQueue, UpdateTip);
        Core.QueueChanged += _ => tip.Request();
        Core.GameChanged += _ => tip.Request();
        return t;
    }

    static void UpdateTip()
    {
        if (tray == null) return;
        var q = Running();
        var name = q == null ? null : Core.Games.FirstOrDefault(g => g.Game.Id == q.GameId)?.Game.Name ?? q.GameId;
        tray.Tip = quitting ? "SCSKiller: finishing, the driver is saving the shader cache"
            : q == null ? "SCSKiller: idle"
            : q.PlanCheck ? "SCSKiller: checking games for more to compile"
            : q.Stage == QueueStage.Paused ? $"SCSKiller: paused ({name})"
            : q is { Stage: QueueStage.Warming, Progress: { Total: > 0 } p } ? $"Compiling {name}, {100.0 * p.Done / p.Total:0}%"
            : $"Compiling {name}";
    }

    static QueueItem? Running() => Core.Queue.FirstOrDefault(Format.Running);

    /// <summary>Stops the running compile gracefully (the stop event: in-flight items finish, the driver writes its cache)
    /// and drops the waiting items, which don't outlive the app anyway ("when idle" ones would otherwise start).</summary>
    static void StopAll()
    {
        foreach (var q in Core.Queue.Where(q => q.Stage == QueueStage.Waiting)) Core.Remove(q.GameId);
        Core.StopQueue();
    }

    /// <summary>The real quit (the tray's Quit). With a compile running: stop it gracefully, hide the window, keep the icon
    /// ("finishing, the driver is saving the shader cache") and exit once the warm has exited, however long the driver's
    /// cache write takes (scskiller_warm bounds it: 600 s after its last line; a warm that ignores the stop is ended after
    /// 30 s). Quit again meanwhile ("Quit now (loses unsaved cache)") exits at once: the warm job kills the warm.</summary>
    public static async Task QuitAsync()
    {
        if (quitNow != null) { quitNow.TrySetResult(); return; }
        if (quitting) return;
        quitting = true;
        Main.SavePlacement();
        try
        {
            if (Core.Compiling)   // a removed item's warm too: it is still saving
            {
                quitNow = new TaskCompletionSource();
                Main.AppWindow.Hide();
                UpdateTip();
                StopAll();
                var idle = Task.Run(async () => { while (Core.Compiling) await Task.Delay(250); });
                await Task.WhenAny(idle, quitNow.Task);
            }
            stopWatching.Cancel();
            await Task.WhenAny(watcher, Task.Delay(1000));   // let a poll's refresh in flight finish
            DisposeTray();
            await Updater.ApplyOnExitAsync();   // a downloaded update installs once this process has exited
        }
        finally { Current.Exit(); }   // Quit quits, whatever went wrong before
    }

    public static void DisposeTray()
    {
        tray?.Dispose();
        tray = null;
    }

    // Toast.dc.html. Windows draws the buttons, so "Compile now" can't be accent-coloured.
    static AppNotification DriverToast(IReadOnlyList<GameState> stale)
    {
        var time = TimeSpan.FromTicks(stale.Sum(g => (g.EstimatedWarmTime ?? TimeSpan.Zero).Ticks));
        string games = stale.Count == 1 ? "1 game needs" : $"{stale.Count} games need";
        return new AppNotificationBuilder()
            .AddArgument("action", "open")
            .AddText($"{Fmt.Vendor(Core.Vendor.Vendor)} driver updated")
            .AddText($"Driver {Core.Vendor.Gpu.DriverVersion} cleared the shader cache. {games} rebuilding: " +
                     $"{string.Join(", ", stale.Select(g => g.Game.Name))}" + (time > TimeSpan.Zero ? $", about {Format.Duration(time)}." : "."))
            .AddButton(new AppNotificationButton("Compile now").AddArgument("action", "now"))
            .AddButton(new AppNotificationButton("When idle").AddArgument("action", "idle"))
            .AddButton(new AppNotificationButton("Skip").AddArgument("action", "skip"))
            .BuildNotification();
    }

    /// <summary>One notification for every compiled game that has pipelines to compile again (<see cref="NewShaders"/>).</summary>
    static void NotifyNewShaders(AppStore store)
    {
        if (!notifications || !Core.Settings.NotifyNewShaders) return;
        told ??= store.LoadNotified();
        var (due, notified) = NewShaders.Due(Core.Games, Core.Queue, told, Core.DriverStaleGames().Select(s => s.Game.Id).ToHashSet());
        if (due.Count == 0 && notified.Count == told.Count) return;   // entries only drop without a notification
        store.SaveNotified(told = notified);   // before showing: never twice
        if (due.Count > 0) AppNotificationManager.Default.Show(NewShadersToast(due));
    }

    static AppNotification NewShadersToast(IReadOnlyList<GameState> games)
    {
        var ids = string.Join('|', games.Select(g => g.Game.Id));
        var n = NewShaders.Count(games[0]);
        return new AppNotificationBuilder()
            .AddArgument("action", "shaders-show").AddArgument("games", ids)
            .AddText("New shaders to compile")
            .AddText(games.Count == 1 ? $"{games[0].Game.Name} has {n:N0} new pipeline{(n == 1 ? "" : "s")}. Compile now so they don't stutter."
                : $"{games.Count} games have new shaders to compile")
            .AddButton(new AppNotificationButton("Compile now").AddArgument("action", "shaders-compile").AddArgument("games", ids))
            .AddButton(new AppNotificationButton("Show").AddArgument("action", "shaders-show").AddArgument("games", ids))
            .BuildNotification();
    }

    /// <summary>A toast clicked after the app was quit starts it with no games known yet.</summary>
    static void AfterScan(Action act)
    {
        if (Core.Games.Count > 0) act();
        else Core.ScanAsync(CancellationToken.None).ContinueWith(_ => Main.DispatcherQueue.TryEnqueue(() => act()));
    }

    /// <summary><paramref name="relaunched"/>: the click started the app, which only stays for what the click queued.</summary>
    static void OnToast(IDictionary<string, string> args, bool relaunched = false)
    {
        // called after the scan: a driver toast names no games, they are the stale ones
        string[] Ids() => args.TryGetValue("games", out var list) ? list.Split('|') : Core.DriverStaleGames().Select(g => g.Game.Id).ToArray();
        switch (ActionOf(args))
        {
            case "shaders-compile":
                AfterScan(() => CompileNow(Ids()));
                break;
            case "shaders-show":
                AfterScan(() =>
                {
                    var ids = Ids();
                    if (ids.Length == 1 && Core.Games.Any(s => s.Game.Id == ids[0])) Main.Navigate(typeof(DetailPage), ids[0]);
                    else Main.Navigate(typeof(LibraryPage));
                    ShowWindow();
                });
                break;
            case "now":
                AfterScan(() => CompileNow(Ids()));
                break;
            case "idle":
                AfterScan(() => { foreach (var id in Ids()) Core.EnqueueWhenIdle(id); });
                break;
            case "skip":
                AfterScan(() =>
                {
                    Core.DismissStale();
                    if (relaunched) _ = QuitAsync();   // nothing queued: no reason to stay resident
                });
                break;
            default:   // toast body clicked
                ShowWindow();
                break;
        }
    }

    /// <summary>"Compile queue" (the library's and the queue's): everything listed, "when idle" items too (Enqueue makes
    /// them normal ones).</summary>
    public static void CompileQueue()
    {
        foreach (var q in Core.Queue.Where(q => q.Stage == QueueStage.Waiting && q.Note == ScsKiller.WhenIdleNote).ToList()) Core.Enqueue(q.GameId);
        Core.StartQueue();
    }

    static void CompileNow(IEnumerable<string> ids)
    {
        foreach (var id in ids) Core.Enqueue(id);
        Core.StartQueue();
        Main.Navigate(typeof(QueuePage));
        ShowWindow();
    }

    static string? ActionOf(IDictionary<string, string> args) => args.TryGetValue("action", out var a) ? a : null;

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool ShutdownBlockReasonCreate(nint hwnd, string reason);
    [DllImport("user32.dll")] static extern bool ShutdownBlockReasonDestroy(nint hwnd);
}
