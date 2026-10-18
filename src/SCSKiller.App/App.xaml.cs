using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
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
    /// <summary>The data folder's store: the real one, or the design data's temporary folder.</summary>
    public static AppStore Store { get; private set; } = null!;
    /// <summary>The signed news.json in use (the bell's notifications).</summary>
    public static News News => Core is ScsKiller k ? k.News : FakeScsKiller.SampleNews;

    /// <summary>At the session's end (shutdown, restart, sign-out), how long a running compile gets to stop before Windows
    /// goes on (it won't wait much longer); the warm job then kills what is left. A stopped warm exits once its in-flight
    /// items finish; the NVIDIA driver then writes its cache as the process exits, which took ~4 min after a full Jedi
    /// Survivor warm (3.49 GB). Ending the process earlier loses what the driver hadn't written: the game compiles those
    /// pipelines itself when it meets them (the stutter this app prevents), and the next warm redoes them, since an ended
    /// run keeps its old resume point. AMD writes during the run: only the in-flight items are lost. The tray's Quit waits
    /// instead (<see cref="QuitAsync"/>).</summary>
    static readonly TimeSpan SessionEndWait = TimeSpan.FromSeconds(10);

    static Tray? tray;
    static bool notifications, quitting, checkingDriver, checkAgain;
    static string? toldDriver;   // the driver and games the last driver-update notification was about
    static Dictionary<string, string>? told;   // NewShaders' notified store
    static readonly HashSet<string> autoQueued = [];   // games NotifyNewShaders queued, while they wait for idle
    static readonly DateTimeOffset session = DateTimeOffset.UtcNow;   // NewShaders.Plan: what an earlier one queued was lost with it
    static Coalesced? newShadersCheck;
    static TaskCompletionSource? quitNow;   // set while quitting waits for the compile to finish: the tray's Quit again ends it
    static readonly CancellationTokenSource stopWatching = new();
    static Task watcher = Task.CompletedTask;

    public App()
    {
        UnhandledException += (_, e) => Program.LogCrash(e.Exception);   // logged, not handled: WinUI ends the app as it would have
        InitializeComponent();
    }

    public static bool Quitting => quitting;
    /// <summary>Closing the window hides it to the notification area (the tray's Quit really quits).</summary>
    public static bool HidesOnClose => tray is { Added: true };   // also while quitting waits: closing must not end the process

    protected override void OnLaunched(LaunchActivatedEventArgs e)
    {
        try { Launch(); }
        catch (Exception ex)
        {
            Program.StartFailed(ex, Environment.GetCommandLineArgs());
            throw;
        }
    }

    // Args: --fake (sample data), --screenshots <dir> (fake data, off-screen, never activated, then exit),
    // --driver-updated (PLATFORM's scheduled task after a driver update leaves stale games in Ask mode),
    // --tray (the sign-in entry, WindowsStartup: starts in the notification area, no window).
    void Launch()
    {
        var args = Environment.GetCommandLineArgs();
        int shots = Array.IndexOf(args, "--screenshots");
        bool driverUpdated = args.Contains("--driver-updated");
        Core = shots >= 0 || args.Contains("--fake") ? new FakeScsKiller() : ScsKiller.CreateDefault();
        // The design data starts signed out and never touches the real auth.dat.
        var fakeDir = Path.Combine(Path.GetTempPath(), $"SCSKiller-fake-{Environment.ProcessId}");
        Account = shots >= 0 ? FakeAccount.Create(fakeDir) : new Account(Core is FakeScsKiller ? fakeDir : AppStore.DefaultDir);   // screenshots: a fake server
        Store = Core is ScsKiller ks ? ks.Store : new AppStore(fakeDir);
        if (Core is ScsKiller real)
        {
            real.Log = new AppLog(real.Store.DataDir);
            Codecs.Log = real.Log;
            real.Log.Report($"SCSKiller {AppVersion.Current} started on Windows {Environment.OSVersion.Version}, {real.Vendor.Gpu.Name}, driver {real.Vendor.Gpu.DriverVersion}");
            real.Community = new Community(AppStore.DefaultDir, Account.GetDbTokenAsync) { TokenWith = Account.GetTokenAsync };
            real.DbEntitled = () => Account.SignedIn && (Account.Status == null || Account.HasDb);
            real.Sharing = new Sharing(AppStore.DefaultDir, () => real.Settings.ShareRecordings);   // anonymous: never the Patreon sign-in
            real.Diagnostics = new Diagnostics(() => real.Settings.ShareDiagnostics);   // no token at all
            real.ContentRoutes = RouteFailover.Default;
            real.ChannelAccess = async () => (await Account.GetAccessTokenAsync(), Account.Status?.Ent);
            // the entitlements first: the update check picks the channel they allow
            real.UserFetch = async () => { await Account.RefreshAsync(); await Updater.CheckAsync(); };
            real.ActiveCheck = new ActiveCheck(AppStore.DefaultDir, () => ActiveCheck.Allowed(real.Settings), real.Vendor.Vendor);
            // not in the unattended --driver-updated launch: nobody at the PC, no game folder is written
            real.ManageRecorders = !driverUpdated;
            real.CleanupHelper = Environment.ProcessPath;
            real.CheckPlans = true;
            var hadDb = false;   // signed in (or the membership turned active): check at once, not at the next scan
            Account.Changed += () => { var db = Account.Status?.Ent.Contains("db") == true; if (db && !hadDb) real.StartCommunitySync(); hadDb = db; };
            // signed in, the entitlements are known once a token came
            void Channels() { if (!Account.SignedIn || Account.Status != null) real.UseChannels(Account.Status?.Ent); }
            Account.Changed += Channels;
            Channels();
            watcher = real.WatchGames(stopWatching.Token);
        }
        Main = new MainWindow();
        Main.ApplyTheme(Core.Settings.Theme);
        if (shots < 0) Main.RestorePlacement(Core is ScsKiller own ? own.Store : new AppStore(fakeDir));   // screenshots: always the default size
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(Main);
        if (Core is ScsKiller seen) seen.Unseen = () => !IsWindowVisible(hwnd) || IsIconic(hwnd);

        string? toastError = null;
        try
        {
            AppNotificationManager.Default.NotificationInvoked += (_, a) => Main.DispatcherQueue.TryEnqueue(() => OnToast(a.Arguments));
            AppNotificationManager.Default.Register();
        }
        catch (Exception ex) { toastError = ex.Message; }   // the app works without notifications
        notifications = toastError == null;
        // after Register(): a toast click's relaunch fail-fasts in the Windows App SDK when the args are read first
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();

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
                File.WriteAllText(Path.Combine(dir, "toast-new-shaders-compiling.xml"), NewShadersToast([Core.Games[0] with { RecordedSinceWarm = 42 }], compiling: true).Payload);
            }
            _ = Main.TakeScreenshotsAsync(dir, (FakeScsKiller)Core);
            return;   // no tray icon, no notification, no single-instance registration
        }

        BeforeAfterUi.Watch();

        // design data: the bundled text, no network
        if (!Core.Settings.WelcomeSeen)
        {
            WhatsNew.AtStart(Store, firstRun: true, AppVersion.Current);
            WhenShown(root => ShowWelcomeAsync(root, Core is FakeScsKiller ? Task.FromResult<WelcomeContent?>(null) : WelcomeContent.FetchAsync()));
        }
        else WhenShown(async root =>   // one dialog at a time
        {
            if (ActiveCheck.Asks(Core.Settings)) await AskActiveCheckAsync(root);
            if (Diagnostics.Asks(Core.Settings)) await AskDiagnosticsAsync(root);
            if (WhatsNew.AtStart(Store, firstRun: false, AppVersion.Current) is { } news) await ShowWhatsNewAsync(root, news);
        });

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
            ApplyStartWithWindows(byUser: false);
            Updater.Start();
            if (activation.Kind == ExtendedActivationKind.Launch) _ = Updater.ApplyAtStartAsync(args[1..]);
            if (Core is ScsKiller k && !driverUpdated)
            {
                var check = newShadersCheck = new Coalesced(Main.DispatcherQueue, () => NotifyNewShaders(k.Store));
                Core.GameChanged += _ => check.Request();
                // a compile starting clears its "waits for idle", one ending lets the games it held back compile
                Core.QueueChanged += q => { if (q.Stage is QueueStage.Indexing or QueueStage.Done or QueueStage.Failed or QueueStage.Stopped) check.Request(); };
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
        else if (line.Contains(MemoryArg)) LogMemory();
        else if (!line.Contains(WindowsStartup.TrayArg)) ShowWindow();
    }

    /// <summary>`SCSKiller.exe --memory` while the app runs: a line of what it holds in memory.log, for a bug report. Counts
    /// only, nothing of the process's contents. The window stays as it is.</summary>
    const string MemoryArg = "--memory";

    static void LogMemory()
    {
        if (Core is ScsKiller k)
            Task.Run(() => ScsKiller.AppendLog(k.Store.DataDir, ProcessMemory.LogFile, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {ProcessMemory.Line()}", 1 << 20));
    }

    /// <summary>Brings the window back from the notification area (or from behind other windows).</summary>
    public static void ShowWindow()
    {
        if (Main.AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized } p) p.Restore();
        Main.AppWindow.Show();
        Main.Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(Main));
    }

    /// <summary>The window's close button: hide to the notification area; the first time ever, say where it went.</summary>
    public static void HideToTray()
    {
        Main.AppWindow.Hide();
        if (Core.Settings.TrayNoticeShown || !notifications) return;   // notifications off: shown once they're on
        Core.Settings = Core.Settings with { TrayNoticeShown = true };
        AppNotificationManager.Default.Show(new AppNotificationBuilder()
            .AddArgument("action", "open")
            .AddText("SCSKiller is still running")
            .AddText("It's in the notification area, right-click the icon to quit.")
            .BuildNotification());
    }

    /// <summary>A toast for a step the user takes in another app, shown over it; gone from the notification center after
    /// a few minutes, when the step is stale.</summary>
    public static void Hint(string title, string text)
    {
        if (!notifications) return;
        var toast = new AppNotificationBuilder().AddText(title).AddText(text).BuildNotification();
        toast.Expiration = DateTimeOffset.Now.AddMinutes(5);
        AppNotificationManager.Default.Show(toast);
    }

    /// <summary>Runs <paramref name="show"/> once the window is first shown (a --tray or toast start shows it later).</summary>
    static void WhenShown(Func<XamlRoot, Task> show)
    {
        var root = (FrameworkElement)Main.Content;
        root.Loaded += OnLoaded;

        async void OnLoaded(object _, RoutedEventArgs __)
        {
            root.Loaded -= OnLoaded;
            await show(root.XamlRoot);
        }
    }

    /// <summary>The first-run welcome: the server's text (<paramref name="fetch"/>, at most 3 s) or the bundled one, as plain
    /// text. Closing it either way marks it seen and saves its checkboxes (<see cref="Settings.ShareRecordings"/>,
    /// <see cref="Settings.ShareDiagnostics"/> as the answer to <see cref="Diagnostics.Asks"/>, and <see cref="Settings.ActiveCheck"/>).</summary>
    static async Task ShowWelcomeAsync(XamlRoot root, Task<WelcomeContent?> fetch)
    {
        var c = await fetch ?? WelcomeContent.Default;
        var panel = new StackPanel { Spacing = 12 };
        foreach (var p in c.Paragraphs) panel.Children.Add(new TextBlock { Text = p, TextWrapping = TextWrapping.Wrap });
        if (c.Link != null) panel.Children.Add(new HyperlinkButton { Content = c.Link.Text, NavigateUri = new Uri(c.Link.Url), Padding = new Thickness(0) });
        var share = new CheckBox { Content = c.Share, IsChecked = false };   // never pre-ticked: that isn't consent
        panel.Children.Add(share);
        var diagnostics = new CheckBox { Content = "Share diagnostics to help fix problems", IsChecked = false };
        panel.Children.Add(diagnostics);
        var activeCaption = new TextBlock { Text = "Helps us count active installs so we can size our servers. Once a day, the app's version and GPU vendor, no identifier.", TextWrapping = TextWrapping.Wrap,
            Style = (Style)Current.Resources["Caption"] };
        var active = new CheckBox { IsChecked = false, Content = new StackPanel { Spacing = 2, Children = { new TextBlock { Text = "Send an anonymous daily check", TextWrapping = TextWrapping.Wrap }, activeCaption } } };
        AutomationProperties.SetName(active, "Send an anonymous daily check");
        panel.Children.Add(active);
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = c.Title, Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = c.SignIn, CloseButtonText = c.Dismiss, DefaultButton = ContentDialogButton.Primary,
        };
        ContentDialogResult result;
        try { result = await Themed(dialog).ShowAsync(); }
        catch (COMException) { return; }   // another dialog is open (the user got there first): next start
        Core.Settings = ActiveCheck.Answered(Diagnostics.Answered(Core.Settings, diagnostics.IsChecked == true), active.IsChecked == true) with { WelcomeSeen = true, ShareRecordings = share.IsChecked == true };   // either button, or Esc
        if (result == ContentDialogResult.Primary) SignInWithPatreon();
    }

    /// <summary>The one-time daily check question (<see cref="ActiveCheck.Asks"/>): Esc or closing keeps it off and answers it.</summary>
    static async Task AskActiveCheckAsync(XamlRoot root)
    {
        ContentDialogResult result;
        try { result = await Themed(ActiveCheckDialog(root)).ShowAsync(); }
        catch (COMException) { return; }   // another dialog is open: next start
        Core.Settings = ActiveCheck.Answered(Core.Settings, result == ContentDialogResult.Primary);
    }

    public static ContentDialog ActiveCheckDialog(XamlRoot root) => new()
    {
        XamlRoot = root, Title = "Anonymous daily check",
        Content = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = "Once a day SCSKiller can tell our server that an install is active, with its version and GPU vendor and no identifier. "
                        + "This helps us count active installs so we can size our servers. It's off until you turn it on.",
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = "Change it any time in Settings.", TextWrapping = TextWrapping.Wrap, Style = (Style)Current.Resources["Caption"] },
            },
        },
        PrimaryButtonText = "Turn it on", CloseButtonText = "Keep it off", DefaultButton = ContentDialogButton.None,   // no nudge either way
    };

    /// <summary>The one-time diagnostics question (<see cref="Diagnostics.Asks"/>): either button, or Esc, answers it.</summary>
    static async Task AskDiagnosticsAsync(XamlRoot root)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = "Share diagnostics to help fix problems?",
            Content = new TextBlock
            {
                Text = "When a compile fails, send its error codes, once per compile. You can change this in Settings.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Share diagnostics", CloseButtonText = "No thanks", DefaultButton = ContentDialogButton.Close,
        };
        ContentDialogResult result;
        try { result = await Themed(dialog).ShowAsync(); }
        catch (COMException) { return; }   // another dialog is open: next start
        Core.Settings = Diagnostics.Answered(Core.Settings, result == ContentDialogResult.Primary);
    }

    /// <summary>The bullets of a version's CHANGELOG section.</summary>
    public static async Task ShowWhatsNewAsync(XamlRoot root, WhatsNewSection section)
    {
        try { await Themed(WhatsNewDialog(root, section)).ShowAsync(); }
        catch (COMException) { }   // another dialog is open: About shows it again
    }

    public static ContentDialog WhatsNewDialog(XamlRoot root, WhatsNewSection section)
    {
        var panel = new StackPanel { Spacing = 10 };
        foreach (var b in section.Bullets)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "• " });
            foreach (var s in WhatsNew.Spans(b))
            {
                Microsoft.UI.Xaml.Documents.Inline inline = s.Kind switch
                {
                    SpanKind.Bold => new Microsoft.UI.Xaml.Documents.Bold { Inlines = { new Microsoft.UI.Xaml.Documents.Run { Text = s.Text } } },
                    SpanKind.Code => new Microsoft.UI.Xaml.Documents.Run { Text = s.Text, FontFamily = new("Consolas") },
                    SpanKind.Link => new Microsoft.UI.Xaml.Documents.Hyperlink { NavigateUri = new(s.Url!), Inlines = { new Microsoft.UI.Xaml.Documents.Run { Text = s.Text } } },
                    _ => new Microsoft.UI.Xaml.Documents.Run { Text = s.Text },
                };
                text.Inlines.Add(inline);
            }
            panel.Children.Add(text);
        }
        return new ContentDialog { XamlRoot = root, Title = $"What's new in {section.Version}", Content = new ScrollViewer { Content = panel }, CloseButtonText = "Close" };
    }

    /// <summary>A question with Cancel and one action; true = the action was chosen.</summary>
    public static async Task<bool> ConfirmAsync(Page page, string title, object content, string primaryText, ContentDialogButton defaultButton = ContentDialogButton.Close) =>
        await ShowAsync(Confirm(page, title, content, primaryText, defaultButton)) == ContentDialogResult.Primary;

    public static ContentDialog Confirm(Page page, string title, object content, string primaryText, ContentDialogButton defaultButton = ContentDialogButton.Close) => new()
    {
        XamlRoot = page.XamlRoot, Title = title, Content = content, PrimaryButtonText = primaryText, CloseButtonText = "Cancel", DefaultButton = defaultButton,
    };

    /// <summary>None when another dialog is open: WinUI shows one at a time and throws, which an async void
    /// handler can't survive.</summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        try { return await Themed(dialog).ShowAsync(); }
        catch (COMException) { return ContentDialogResult.None; }
    }

    // a dialog opens in the window's popup layer, outside the root the theme is set on
    static ContentDialog Themed(ContentDialog dialog)
    {
        dialog.RequestedTheme = Main.Theme;
        return dialog;
    }

    /// <summary>The welcome's sign-in button: starts the browser flow and shows Settings, whose Account card follows it.</summary>
    public static void SignInWithPatreon()
    {
        _ = Account.SignInAsync();
        Main.Navigate(typeof(SettingsPage));
    }

    /// <summary>The sign-in entry: <paramref name="byUser"/>, the user turned the setting on or off here
    /// (<see cref="WindowsStartup.Set"/>); otherwise the app start (<see cref="WindowsStartup.Reconcile"/>, which restores a
    /// missing value, or one whose exe is gone, only for the installed copy; Velopack counts its portable zip as installed
    /// too). Real data only, and only in Release builds (build\publish.ps1): a Debug build from a checkout or worktree must
    /// not point the sign-in entry at itself. In Debug the setting is saved but the registry is left as it is.</summary>
    public static void ApplyStartWithWindows(bool byUser)
    {
#if !DEBUG
        if (Core is FakeScsKiller || Environment.ProcessPath is not { } exe) return;
        var on = Core.Settings.StartWithWindows;
        try
        {
            if (byUser) WindowsStartup.Set(on, exe, WindowsStartup.ReadRegistry, WindowsStartup.WriteRegistry);
            else WindowsStartup.Reconcile(on, exe, Updater.Installed && !AppStore.IsPortable, WindowsStartup.ReadRegistry, WindowsStartup.WriteRegistry, File.Exists);
        }
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
                Main.SavePlacement();
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

    static (string Line, double? Done) Status()
    {
        var q = Running();
        return Format.TrayStatus(q, q == null ? null : Core.Games.FirstOrDefault(g => g.Game.Id == q.GameId)?.Game.Name ?? q.GameId, quitting);
    }

    static void UpdateTip()
    {
        if (tray != null) tray.Tip = "SCSKiller: " + Status().Line;
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
            if (Core is ScsKiller { Log: AppLog log }) await log.FlushAsync(TimeSpan.FromSeconds(1));
            DisposeTray();
            await Updater.ApplyOnExitAsync();   // a downloaded update installs once this process has exited
        }
        finally
        {
            ReportWindow.CloseAll();
            Current.Exit();   // Quit quits, whatever went wrong before
        }
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

    /// <summary>One notification for every compiled game that has pipelines to compile again, and the "when idle" compile of
    /// the ones whose setting compiles automatically (<see cref="NewShaders.Plan"/>).</summary>
    static void NotifyNewShaders(AppStore store)
    {
        var (withdraw, forget) = NewShaders.Withdrawn(autoQueued, Core.Queue, Core.Games, Core.Settings);
        foreach (var id in forget) autoQueued.Remove(id);
        foreach (var id in withdraw)
        {
            Core.Remove(id);
            autoQueued.Remove(id);
        }
        told ??= store.LoadNotified();
        var (due, compile, notified) = NewShaders.Plan(Core.Games, Core.Queue, told, Core.DriverStaleGames().Select(s => s.Game.Id).ToHashSet(),
            DateTimeOffset.UtcNow, s => NewShaders.ModeOf(s, Core.Settings), Core.Compiling, forget.ToHashSet(), session);
        if (due.Count == 0 && notified.Count == told.Count && notified.All(e => told.GetValueOrDefault(e.Key) == e.Value)) return;
        store.SaveNotified(told = notified);   // before showing: never twice
        foreach (var s in compile)
        {
            Core.EnqueueWhenIdle(s.Game.Id);
            autoQueued.Add(s.Game.Id);
        }
        if (!notifications) return;
        var compiling = due.Where(compile.Contains).ToList();
        if (compiling.Count > 0) AppNotificationManager.Default.Show(NewShadersToast(compiling, compiling: true));
        if (due.Except(compiling).ToList() is { Count: > 0 } ask) AppNotificationManager.Default.Show(NewShadersToast(ask));
    }

    /// <summary>The Settings page changed the global choice: queued automatic compiles follow it.</summary>
    public static void NewShadersChanged() => newShadersCheck?.Request();

    static AppNotification NewShadersToast(IReadOnlyList<GameState> games, bool compiling = false)
    {
        var ids = string.Join('|', games.Select(g => g.Game.Id));
        var n = NewShaders.Count(games[0]);
        var b = new AppNotificationBuilder()
            .AddArgument("action", "shaders-show").AddArgument("games", ids);
        if (compiling)
            b.AddText("Compiling new shaders")
                .AddText(games.Count == 1 ? $"{games[0].Game.Name} has {n:N0} new pipeline{(n == 1 ? "" : "s")}. They compile when the PC is idle."
                    : $"{games.Count} games have new shaders. They compile when the PC is idle.");
        else
            b.AddText("New shaders to compile")
                .AddText(games.Count == 1 ? $"{games[0].Game.Name} has {n:N0} new pipeline{(n == 1 ? "" : "s")}. Compile now so they don't stutter."
                    : $"{games.Count} games have new shaders to compile");
        return b.AddButton(new AppNotificationButton("Compile now").AddArgument("action", "shaders-compile").AddArgument("games", ids))
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
    /// them normal ones). Plan checks aren't listed: Enqueue would turn them into compiles of games nobody picked.</summary>
    public static void CompileQueue()
    {
        foreach (var q in Core.Queue.Where(q => q.Stage == QueueStage.Waiting && q.Note == ScsKiller.WhenIdleNote && !q.PlanCheck).ToList()) Core.Enqueue(q.GameId);
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
