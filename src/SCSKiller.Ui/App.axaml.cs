using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Fonts;
using SCSKiller.App.Design;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Pages;
using SCSKiller.Ui.Platform;

namespace SCSKiller.Ui;

public partial class App : Application
{
    public static MainWindow Main { get; private set; } = null!;
    /// <summary>Sign in with Patreon. The design data's is signed out and never reads the real auth.dat.</summary>
    public static Account Account { get; private set; } = null!;
    /// <summary>The main window is up: a failure from here on is a crash, not a failed start.</summary>
    public static bool Started { get; private set; }
    /// <summary>A toast over other windows that expires after 5 minutes: title, text. Unset, nothing shows.</summary>
    public static Action<string, string>? Hint { get; set; }
    /// <summary>The signed news.json in use (the bell's notifications).</summary>
    public static News News => Host.Core is ScsKiller k ? k.News : FakeScsKiller.SampleNews;

    /// <summary>A file under Assets\. The assembly is SCSKiller on Windows and SCSKiller.Ui elsewhere.</summary>
    public static Uri Asset(string path) => new($"avares://{typeof(App).Assembly.GetName().Name}/Assets/{path}");

    /// <summary>Selawik has Segoe UI's metrics, so the pages lay out as the WinUI app's do. Glyphs it lacks (≤, ≈) come from the system's fonts.</summary>
    public static AppBuilder WithSelawik(AppBuilder builder) => builder
        .ConfigureFonts(fonts => fonts.AddFontCollection(new EmbeddedFontCollection(new("fonts:Selawik"), Asset("Selawik"))))
        .With(new FontManagerOptions { DefaultFamilyName = OperatingSystem.IsWindows() ? "Segoe UI" : "fonts:Selawik#Selawik" });

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        // Windows spaces Segoe UI's and Selawik's lines by their win metrics, Avalonia by hhea (1.2 em). A FontIcon is as tall as its glyph.
        TextBlock.FontSizeProperty.Changed.AddClassHandler<TextBlock>((text, _) =>
            text.LineHeight = text.Classes.Contains("Icon") ? text.FontSize : Math.Round(text.FontSize * 1.33, 2));
        foreach (var theme in Resources.ThemeDictionaries.Values.OfType<IResourceDictionary>())
            foreach (var (key, from) in ControlBrushes) theme[key] = theme[from];
    }

    /// <summary>The brushes WinUI's controls use, under the names the Fluent theme's controls read.</summary>
    static readonly (string Key, string From)[] ControlBrushes =
    [
        ("ButtonBackground", "ControlFillColorDefaultBrush"), ("ButtonBackgroundPointerOver", "ControlFillColorSecondaryBrush"),
        ("ButtonBackgroundPressed", "ControlFillColorTertiaryBrush"), ("ButtonBackgroundDisabled", "ControlFillColorDisabledBrush"),
        ("ButtonForeground", "TextFillColorPrimaryBrush"), ("ButtonForegroundPointerOver", "TextFillColorPrimaryBrush"),
        ("ButtonForegroundPressed", "TextFillColorSecondaryBrush"), ("ButtonForegroundDisabled", "TextFillColorDisabledBrush"),
        ("ButtonBorderBrush", "ControlElevationBorderBrush"), ("ButtonBorderBrushPointerOver", "ControlElevationBorderBrush"),
        ("ButtonBorderBrushPressed", "ControlStrokeColorDefaultBrush"), ("ButtonBorderBrushDisabled", "ControlStrokeColorDefaultBrush"),

        ("AccentButtonBackground", "AccentFillColorDefaultBrush"), ("AccentButtonBackgroundPointerOver", "AccentFillColorSecondaryBrush"),
        ("AccentButtonBackgroundPressed", "AccentFillColorTertiaryBrush"), ("AccentButtonBackgroundDisabled", "AccentFillColorDisabledBrush"),
        ("AccentButtonForeground", "TextOnAccentFillColorPrimaryBrush"), ("AccentButtonForegroundPointerOver", "TextOnAccentFillColorPrimaryBrush"),
        ("AccentButtonForegroundPressed", "TextOnAccentFillColorSecondaryBrush"), ("AccentButtonForegroundDisabled", "TextOnAccentFillColorDisabledBrush"),
        ("AccentButtonBorderBrush", "AccentControlElevationBorderBrush"), ("AccentButtonBorderBrushPointerOver", "AccentControlElevationBorderBrush"),
        ("AccentButtonBorderBrushPressed", "SubtleFillColorTransparentBrush"), ("AccentButtonBorderBrushDisabled", "SubtleFillColorTransparentBrush"),

        ("HyperlinkButtonForeground", "AccentTextFillColorPrimaryBrush"), ("HyperlinkButtonForegroundPointerOver", "AccentTextFillColorSecondaryBrush"),
        ("HyperlinkButtonForegroundPressed", "AccentTextFillColorTertiaryBrush"), ("HyperlinkButtonForegroundDisabled", "AccentTextFillColorDisabledBrush"),
        ("HyperlinkButtonForegroundVisited", "AccentTextFillColorPrimaryBrush"), ("HyperlinkButtonForegroundVisitedPointerOver", "AccentTextFillColorSecondaryBrush"),
        ("HyperlinkButtonForegroundVisitedPressed", "AccentTextFillColorTertiaryBrush"),
        ("HyperlinkButtonBackground", "SubtleFillColorTransparentBrush"), ("HyperlinkButtonBackgroundPointerOver", "SubtleFillColorSecondaryBrush"),
        ("HyperlinkButtonBackgroundPressed", "SubtleFillColorTertiaryBrush"), ("HyperlinkButtonBackgroundDisabled", "SubtleFillColorTransparentBrush"),

        ("TextControlBackground", "ControlFillColorDefaultBrush"), ("TextControlBackgroundPointerOver", "ControlFillColorSecondaryBrush"),
        ("TextControlBackgroundFocused", "ControlFillColorInputActiveBrush"), ("TextControlBackgroundDisabled", "ControlFillColorDisabledBrush"),
        ("TextControlBorderBrush", "TextControlElevationBorderBrush"), ("TextControlBorderBrushPointerOver", "TextControlElevationBorderBrush"),
        ("TextControlBorderBrushFocused", "TextControlElevationBorderFocusedBrush"), ("TextControlBorderBrushDisabled", "ControlStrokeColorDefaultBrush"),
        ("TextControlForeground", "TextFillColorPrimaryBrush"), ("TextControlForegroundPointerOver", "TextFillColorPrimaryBrush"),
        ("TextControlForegroundFocused", "TextFillColorPrimaryBrush"), ("TextControlForegroundDisabled", "TextFillColorDisabledBrush"),
        ("TextControlPlaceholderForeground", "TextFillColorSecondaryBrush"), ("TextControlPlaceholderForegroundPointerOver", "TextFillColorSecondaryBrush"),
        ("TextControlPlaceholderForegroundFocused", "TextFillColorTertiaryBrush"), ("TextControlPlaceholderForegroundDisabled", "TextFillColorDisabledBrush"),
        ("TextControlButtonForeground", "TextFillColorSecondaryBrush"),

        ("ComboBoxBackground", "ControlFillColorDefaultBrush"), ("ComboBoxBackgroundPointerOver", "ControlFillColorSecondaryBrush"),
        ("ComboBoxBackgroundPressed", "ControlFillColorTertiaryBrush"), ("ComboBoxBackgroundDisabled", "ControlFillColorDisabledBrush"),
        ("ComboBoxBorderBrush", "ControlElevationBorderBrush"), ("ComboBoxBorderBrushPointerOver", "ControlElevationBorderBrush"),
        ("ComboBoxBorderBrushPressed", "ControlStrokeColorDefaultBrush"), ("ComboBoxBorderBrushDisabled", "ControlStrokeColorDefaultBrush"),
        ("ComboBoxForeground", "TextFillColorPrimaryBrush"), ("ComboBoxForegroundDisabled", "TextFillColorDisabledBrush"),
        ("ComboBoxDropDownGlyphForeground", "TextFillColorSecondaryBrush"),
        ("ComboBoxDropDownBackground", "OverlayPaneBrush"), ("ComboBoxDropDownBorderBrush", "SurfaceStrokeColorFlyoutBrush"),
        ("ComboBoxItemBackgroundPointerOver", "SubtleFillColorSecondaryBrush"), ("ComboBoxItemBackgroundPressed", "SubtleFillColorTertiaryBrush"),
        ("ComboBoxItemBackgroundSelected", "SubtleFillColorSecondaryBrush"), ("ComboBoxItemBackgroundSelectedPointerOver", "SubtleFillColorTertiaryBrush"),

        ("CheckBoxCheckBackgroundFillUnchecked", "ControlAltFillColorSecondaryBrush"), ("CheckBoxCheckBackgroundFillUncheckedPointerOver", "ControlAltFillColorTertiaryBrush"),
        ("CheckBoxCheckBackgroundFillUncheckedPressed", "ControlAltFillColorQuarternaryBrush"),
        ("CheckBoxCheckBackgroundStrokeUnchecked", "ControlStrongStrokeColorDefaultBrush"), ("CheckBoxCheckBackgroundStrokeUncheckedPointerOver", "ControlStrongStrokeColorDefaultBrush"),
        ("CheckBoxCheckBackgroundStrokeUncheckedPressed", "ControlStrongStrokeColorDisabledBrush"),
        ("CheckBoxCheckBackgroundFillChecked", "AccentFillColorDefaultBrush"), ("CheckBoxCheckBackgroundFillCheckedPointerOver", "AccentFillColorSecondaryBrush"),
        ("CheckBoxCheckBackgroundFillCheckedPressed", "AccentFillColorTertiaryBrush"),
        ("CheckBoxCheckBackgroundStrokeCheckedPointerOver", "AccentFillColorSecondaryBrush"), ("CheckBoxCheckBackgroundStrokeCheckedPressed", "AccentFillColorTertiaryBrush"),
        ("CheckBoxCheckGlyphForegroundChecked", "TextOnAccentFillColorPrimaryBrush"), ("CheckBoxCheckGlyphForegroundCheckedPointerOver", "TextOnAccentFillColorPrimaryBrush"),
        ("CheckBoxCheckGlyphForegroundCheckedPressed", "TextOnAccentFillColorSecondaryBrush"),

        ("FlyoutPresenterBackground", "OverlayPaneBrush"), ("MenuFlyoutPresenterBackground", "OverlayPaneBrush"),
        ("MenuFlyoutPresenterBorderBrush", "SurfaceStrokeColorFlyoutBrush"),
        ("MenuFlyoutItemBackgroundPointerOver", "SubtleFillColorSecondaryBrush"), ("MenuFlyoutItemBackgroundPressed", "SubtleFillColorTertiaryBrush"),
    ];

    /// <summary>The design data (--fake, --screenshots) and the main window over it. <paramref name="screenshots"/>: a
    /// fake server for the account.</summary>
    public static MainWindow Start(bool screenshots)
    {
        var fake = new FakeScsKiller();
        var fakeDir = Path.Combine(Path.GetTempPath(), $"SCSKiller-fake-{Environment.ProcessId}");
        (Host.Core, Host.Design, Host.Ui, Host.Icons) = (fake, new FakeDesign(fake), new UiThread(), new Icons());
        (Host.Store, Host.Log) = (new AppStore(fakeDir), new AppLog(fakeDir));
        Host.Account = Account = screenshots ? FakeAccount.Create(fakeDir) : new Account(fakeDir);
        Main = new MainWindow();
        Main.ApplyTheme(Host.Core.Settings.Theme);
        Host.ApplyTheme = Main.ApplyTheme;
        Host.CopyText = CopyText;
        if (!screenshots) WatchBeforeAfter();
        return Main;
    }

    /// <summary>App-wide: a sound once a run's play time is reached, the report window when a pair turns Done.</summary>
    static void WatchBeforeAfter()
    {
        var watch = new BeforeAfterWatch(Host.Core.Games);
        Host.Core.GameChanged += g => Host.Ui.TryEnqueue(() =>
        {
            var (sound, report) = watch.Changed(g);
            if (sound && Host.Core.Settings.BeforeAfterSound) Sound.Information();
            if (report) _ = ReportWindow.OpenAsync(g.Game.Id);
        });
    }

    /// <summary>A start that failed before Avalonia was set up (Linux): the app starts for its message alone.</summary>
    public static StartFailure? Failed { get; set; }

    // flushed: kept after the app quits where the system can (X11 drops it unless a clipboard manager takes it)
    static async Task CopyText(string text)
    {
        var clipboard = Main.Clipboard ?? throw new InvalidOperationException("No clipboard");
        await clipboard.SetTextAsync(text);
        await clipboard.FlushAsync();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = Failed is { } failed ? new StartFailedWindow(failed)
                : Program.IsDesignRun(desktop.Args ?? []) ? Start(screenshots: false) : StartReal(desktop);
        base.OnFrameworkInitializationCompleted();
        Started = true;
    }

    static readonly CancellationTokenSource stopWatching = new();
    static Task watcher = Task.CompletedTask;
    static volatile bool unseen = true;
    static Dictionary<string, string>? told;   // NewShaders' notified store
    static readonly HashSet<string> autoQueued = [];   // games QueueNewShaders queued, while they wait for idle
    static readonly HashSet<string> newGamesTried = [];   // NewShaders.NewGames' once per session
    static readonly DateTimeOffset session = DateTimeOffset.UtcNow;   // NewShaders.Plan: what an earlier one queued was lost with it

    /// <summary>Linux: an AppImage's menu entry (MenuEntry), which no package adds.</summary>
    static partial void RefreshMenuEntry(ScsKiller core);

    /// <summary>An alpha build's gate until a backer signs in (<see cref="AlphaGate"/>), then the real core.</summary>
    static Window? StartReal(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var args = desktop.Args ?? [];
        if (!AlphaGate.Needed(AppVersion.Current) || AlphaGate.Passed(AppStore.DefaultDir)) return StartCore(desktop, args, null);
        // before the core exists: a refused alpha scans nothing and places nothing
        var account = new Account(AppStore.DefaultDir, keyring: Keyring.System());
        // someone is at the PC: the window shows whatever started it, and a toast's activation expires meanwhile
        args = args.Where(a => a != WindowsStartup.TrayArg && !Activation.DriverUpdated([a]) && !a.StartsWith(InstanceIdentity.ToastRelaunchArg, StringComparison.Ordinal)).ToArray();
        var gate = new GateWindow(account, AppStore.DefaultDir, () =>
        {
            var main = StartCore(desktop, args, account) ?? Main;
            desktop.MainWindow = main;
            main.Show();
        });
        gate.Opened += (_, _) => _ = gate.StartAsync();
        return gate;
    }

    /// <summary>The real core on the data folder, composed as the WinUI app does, without start with Windows.
    /// Null: no window yet (<see cref="Activation.Start"/>). <paramref name="signedIn"/>: the gate's account.</summary>
    static MainWindow? StartCore(IClassicDesktopStyleApplicationLifetime desktop, string[] args, Account? signedIn)
    {
        var real = ScsKiller.CreateDefault();
        (Host.Core, Host.Design, Host.Ui, Host.Icons, Host.Store) = (real, null, new UiThread(), new Icons(), real.Store);
        var applog = new AppLog(real.Store.DataDir);
        real.Log = Codecs.Log = Host.Log = applog;
        applog.Start($"{Host.Platform.Name} {Environment.OSVersion.Version}, {real.Vendor.Gpu.Name}, driver {real.Vendor.Gpu.DriverVersion}");
        if (GpuBackends.DriverNote(real.Vendor) is { } driverNote) real.Log.Report(driverNote);
        var keyring = Keyring.System();
        Host.Account = Account = signedIn ?? new Account(AppStore.DefaultDir, keyring: keyring, log: real.Log);
        // together, before the window: it and AboutVm read the updater once Host.Core is set
        (Updater.Host.Core, Updater.Host.Account, Updater.Host.Quitting) = (real, Account, () => quit.Quitting);
        real.Community = new Community(AppStore.DefaultDir, Account.GetDbTokenAsync) { TokenWith = Account.GetTokenAsync };
        real.DbEntitled = () => Account.SignedIn && (Account.Status == null || Account.HasDb);
        real.Sharing = new Sharing(AppStore.DefaultDir, () => real.Settings.ShareRecordings, keyring: keyring, log: real.Log);   // anonymous: never the Patreon sign-in
        _ = Task.Run(async () => { await Account.SweepAsync(); if (real.Sharing is { } s) await s.SweepAsync(); });   // keyring items an earlier run couldn't delete
        real.Diagnostics = new Diagnostics(() => real.Settings.ShareDiagnostics);   // no token at all
        real.ContentRoutes = RouteFailover.Default;
        real.ChannelAccess = async () => (await Account.GetAccessTokenAsync(), Account.Status?.Ent);
        // the entitlements first: the update check picks the channel they allow
        real.UserFetch = async () => { await Account.RefreshAsync(); await Updater.CheckAsync(); };
        real.ActiveCheck = new ActiveCheck(AppStore.DefaultDir, () => ActiveCheck.Allowed(real.Settings), real.Vendor.Vendor);
        // only the data folder's one instance writes game folders, and not in the unattended --driver-updated start until the window shows
        real.ManageRecorders = Activation.ManagesRecorders && !Activation.DriverUpdated(args);
        real.CleanupHelper = ScsKiller.AppProgram;
        RefreshMenuEntry(real);
        real.CheckPlans = true;
        var hadDb = false;   // signed in (or the membership turned active): check at once, not at the next scan
        Account.Changed += () => { var db = Account.Status?.Ent.Contains("db") == true; if (db && !hadDb) real.StartCommunitySync(); hadDb = db; };
        // signed in, the entitlements are known once a token came
        void Channels() { if (!Account.SignedIn || Account.Status != null) real.UseChannels(Account.Status?.Ent); }
        Account.Changed += Channels;
        Channels();
        watcher = real.WatchGames(stopWatching.Token);
        real.QueueChanged += q => { if (real.EndedNotice(q) is { } n) Host.Ui.TryEnqueue(() => Notifications.Show(Toasts.Ended(q.GameId, n))); };
        real.Notice += (id, n) => Host.Ui.TryEnqueue(() => Notifications.Show(Toasts.Ended(id, n)));

        Main = new MainWindow();
        Main.ApplyTheme(real.Settings.Theme);
        Host.ApplyTheme = Main.ApplyTheme;
        Host.CopyText = CopyText;
        WatchBeforeAfter();
        StartDialogs.WhenShown(Main);
        // read on the watcher's thread, and Avalonia's properties are UI thread only
        real.Unseen = () => unseen;
        Main.PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty || e.Property == Window.WindowStateProperty) unseen = !Main.IsVisible || Main.WindowState == WindowState.Minimized;
        };
        // a game played while the window was away changed its cache size
        var lastSizes = DateTime.MinValue;
        Main.Activated += (_, _) =>
        {
            if (DateTime.UtcNow - lastSizes < TimeSpan.FromSeconds(10)) return;
            lastSizes = DateTime.UtcNow;
            Task.Run(real.RefreshCacheSizes);
        };
        real.GpuChanged += () => Host.Ui.TryEnqueue(() => { Main.ShowGpu(); Activation.GpuChanged(); });
        void WatchNewShaders()
        {
            var check = new Coalesced(Host.Ui, () => QueueNewShaders(real.Store));
            Host.NewShadersChanged = check.Request;
            real.GameChanged += _ => check.Request();
            // a compile starting clears its "waits for idle", one ending lets the games it held back compile
            real.QueueChanged += q => { if (q.Stage is QueueStage.Indexing or QueueStage.Done or QueueStage.Failed or QueueStage.Stopped) check.Request(); };
            check.Request();
        }
        // the unattended --driver-updated start queues nothing until someone opens the window
        if (!Activation.DriverUpdated(args)) WatchNewShaders();
        else
        {
            void Once(object? _, EventArgs __)
            {
                Main.Opened -= Once;
                WatchNewShaders();
            }
            Main.Opened += Once;
        }
        _ = ApplyStartWithWindows(byUser: false);
        Host.StartWithWindowsChanged = () => ApplyStartWithWindows(byUser: true);
        Host.StartsAtSignIn = StartsAtSignIn;
        Updater.Start();
        // a launch, not a toast's relaunch: its activation wouldn't survive the restart
        if (!args.Any(a => a.StartsWith(InstanceIdentity.ToastRelaunchArg, StringComparison.Ordinal))) _ = Updater.ApplyAtStartAsync(args);
        if (Updater.HasResume)   // restarted by "Restart to update": the queue goes on once the games are known
            _ = real.ScanAsync(CancellationToken.None).ContinueWith(_ => Host.Ui.TryEnqueue(Updater.ResumeQueue));
        CreateTray(desktop);
        return Activation.Start(real, args, () => QuitAsync(desktop), toast: signedIn == null) ? Main : null;
    }

    /// <summary>One notification for every compiled game that has pipelines to compile again, the "when idle" compile of the
    /// ones whose setting compiles them automatically (<see cref="NewShaders.Plan"/>), and the withdrawal of the ones that no
    /// longer should.</summary>
    static void QueueNewShaders(AppStore store)
    {
        var core = Host.Core;
        var (withdraw, forget) = NewShaders.Withdrawn(autoQueued, core.Queue, core.Games, core.Settings);
        foreach (var id in forget) autoQueued.Remove(id);
        foreach (var id in withdraw)
        {
            core.Remove(id);
            autoQueued.Remove(id);
        }
        foreach (var s in NewShaders.NewGames(core.Games, core.Queue, core.Settings, core.Compiling, newGamesTried))
        {
            core.EnqueueWhenIdle(s.Game.Id);
            autoQueued.Add(s.Game.Id);
        }
        told ??= store.LoadNotified();
        var (due, compile, notified) = NewShaders.Plan(core.Games, core.Queue, told, core.DriverStaleGames().Select(s => s.Game.Id).ToHashSet(),
            DateTimeOffset.UtcNow, s => NewShaders.ModeOf(s, core.Settings), core.Compiling, forget.ToHashSet(), session);
        if (due.Count == 0 && notified.Count == told.Count && notified.All(e => told.GetValueOrDefault(e.Key) == e.Value)) return;
        store.SaveNotified(told = notified);   // before showing: never twice
        foreach (var s in compile)
        {
            NewShaders.Queue(core, s.Game.Id);
            autoQueued.Add(s.Game.Id);
        }
        Activation.NewShaders(due, compile);
    }

    public static void Quit() => _ = QuitAsync((IClassicDesktopStyleApplicationLifetime)Current!.ApplicationLifetime!);

    /// <summary>The real quit (<see cref="QuitFlow"/>): the window hides and the icon stays while the compile stops.</summary>
    static async Task QuitAsync(IClassicDesktopStyleApplicationLifetime desktop)
    {
        if (!quit.Begin()) return;
        SetExitReason("quit", over: true);
        try
        {
            var core = Host.Core;
            await quit.StopAsync(core, () => { Main.Hide(); UpdateTip(); });
            stopWatching.Cancel();
            await Task.WhenAny(watcher, Task.Delay(1000));   // let a poll's refresh in flight finish
            if (core is ScsKiller { Log: AppLog log }) await log.FlushAsync(TimeSpan.FromSeconds(1));
            await Updater.ApplyOnExitAsync();   // a downloaded update installs once this process has exited
        }
        finally
        {
            DisposeTray();
            ReportWindow.CloseAll();
            Notifications.Unregister();
            desktop.Shutdown();
        }
    }

    public static async Task<bool> ConfirmAsync(string title, object content, string primaryText, ContentDialogButton defaultButton = ContentDialogButton.Close) =>
        await ShowAsync(Confirm(title, content, primaryText, defaultButton)) == ContentDialogResult.Primary;

    public static ContentDialog Confirm(string title, object? content, string primaryText, ContentDialogButton defaultButton = ContentDialogButton.Close) => new()
    {
        Title = title, Content = content, PrimaryButtonText = primaryText, CloseButtonText = "Cancel", DefaultButton = defaultButton,
    };

    /// <summary>Over the main window, or over <paramref name="on"/>'s window.</summary>
    public static Task<ContentDialogResult> ShowAsync(ContentDialog dialog, DialogLayer? on = null) => dialog.ShowAsync(on);

    /// <summary>The sign-in button: starts the browser flow and shows Settings, whose Account card follows it.</summary>
    public static void SignInWithPatreon()
    {
        _ = Account.SignInAsync();
        Main.Navigate(typeof(SettingsPage));
    }

    public static void CompileQueue()
    {
        foreach (var q in Host.Core.Queue.Where(q => ScsKiller.WaitsOnItsOwn(q) && !q.PlanCheck).ToList()) Host.Core.Enqueue(q.GameId);
        Host.Core.StartQueue();
    }
}
