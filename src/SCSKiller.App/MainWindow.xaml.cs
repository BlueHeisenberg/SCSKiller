using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using SCSKiller.App.Design;
using SCSKiller.App.Pages;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;
using Windows.Graphics;
using Windows.Graphics.Imaging;

namespace SCSKiller.App;

public sealed partial class MainWindow : Window
{
    // Narrower, a page clips: Settings' two columns, the Library's chips and status column.
    const int MinWidth = 1072, MinHeight = 640;

    bool syncingNav;
    AppStore? placement;   // where window.json goes (RestorePlacement); none: the screenshots' window is never saved
    bool maximizeOnShow;
    readonly (NavigationViewItem Item, Type Page)[] pages;

    public MainWindow()
    {
        InitializeComponent();
        pages = [(QueueItem, typeof(QueuePage)), (CacheItem, typeof(CachePage)), (SettingsItem, typeof(SettingsPage)), (AboutItem, typeof(AboutPage))];
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico"));
        Resize(1280);
        SetMinimumSize();
        Root.Loaded += (_, _) => Root.XamlRoot.Changed += (_, _) => SetMinimumSize();   // the window moved to a monitor with another DPI
        ShowGpu();
        Nav.SelectedItem = LibraryItem;
        Updater.Changed += () => DispatcherQueue.TryEnqueue(ShowUpdate);
        ShowUpdate();
        var bell = new Coalesced(DispatcherQueue, ShowBell);   // targeting reads the games, the account and the news
        App.Core.GameChanged += _ => bell.Request();
        App.Account.Changed += bell.Request;
        if (App.Core is ScsKiller k) k.NewsChanged += bell.Request;
        ShowBell();
        AppWindow.Closing += (w, e) =>
        {
            SavePlacement();
            switch (WindowClose.Of(App.Core.Settings, App.Quitting, App.HidesOnClose))
            {
                case CloseAction.Quit:
                    e.Cancel = true;   // QuitAsync exits once the compile and the update handover are done
                    w.Hide();
                    _ = App.QuitAsync();
                    break;
                case CloseAction.Hide:
                    e.Cancel = true;
                    App.HideToTray();
                    break;
            }
        };
        AppWindow.Changed += (w, e) =>
        {
            if (!e.DidVisibilityChange || !w.IsVisible || !maximizeOnShow) return;
            maximizeOnShow = false;
            ((OverlappedPresenter)w.Presenter).Maximize();
        };
    }

    public void ShowGpu()
    {
        GpuName.Text = App.Core.Vendor.Gpu.Name;
        GpuDriver.Text = (App.Core as ScsKiller)?.GpuRestartNote ?? "Driver " + App.Core.Vendor.Gpu.DriverVersion;
    }

    public void Navigate(Type page, object? arg = null) => ContentFrame.Navigate(page, arg);

    public void NavigateToPatreon()
    {
        Navigate(typeof(SettingsPage));
        if (ContentFrame.Content is not SettingsPage settings) return;
        settings.Loaded += Scroll;
        void Scroll(object _, RoutedEventArgs __) { settings.Loaded -= Scroll; settings.ScrollToPatreon(); }
    }

    void ShowUpdate()
    {
        var ready = Updater.Ready;   // once: a check may replace it meanwhile
        UpdateButton.Visibility = ready != null ? Visibility.Visible : Visibility.Collapsed;
        ToolTipService.SetToolTip(UpdateButton, Updater.Problem ?? $"SCSKiller {ready} is ready. Restarting stops a running compile safely " +
            "(the driver saves its cache first) and continues it afterwards." +
            (Updater.InstallsAutomatically ? " Otherwise it installs the next time SCSKiller starts or quits." : ""));
    }

    static (IReadOnlyList<NewsItem> Shown, IReadOnlySet<string> Unread) Notifications() =>
        NewsState.Of(App.Store, App.News.For(new(AppVersion.Current, App.Account.Status?.Ent ?? [], App.Core.Vendor.Vendor, id => App.Core.Games.Any(g => g.Game.Id == id)),
            DateOnly.FromDateTime(DateTime.Now)));

    static HashSet<string> Feed() => App.News.Items.Select(i => i.Id).ToHashSet();

    void ShowBell()
    {
        var unread = Notifications().Unread.Count;
        BellBadge.Value = unread;
        BellBadge.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(BellButton, unread > 0 ? $"Notifications, {unread} unread" : "Notifications");
    }

    void OnBellOpening(object _, object __)
    {
        var (shown, unread) = Notifications();
        FillBell(shown, unread);
        NewsState.MarkRead(App.Store, shown.Select(i => i.Id), Feed());
        ShowBell();
    }

    void FillBell(IReadOnlyList<NewsItem> shown, IReadOnlySet<string> unread)
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var item in shown) list.Children.Add(NewsCard(item, unread.Contains(item.Id)));
        BellFlyout.Content = new StackPanel
        {
            Width = 360, Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Notifications", Style = Res<Style>("Section") },
                shown.Count == 0 ? new TextBlock { Text = "Nothing new.", Style = Res<Style>("Secondary") } : new ScrollViewer { MaxHeight = 480, Content = list },
            },
        };
    }

    UIElement NewsCard(NewsItem item, bool unread)
    {
        var date = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (unread) date.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center, Fill = Res<Brush>("AccentFillColorDefaultBrush") });
        date.Children.Add(new TextBlock { Text = News.Day(item.Date)!.Value.ToString("d"), Style = Res<Style>("Caption") });
        var text = new StackPanel { Spacing = 4, Children = { date } };
        text.Children.Add(new TextBlock { Text = item.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = item.Body, Style = Res<Style>("Secondary"), TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        if (item.Link != null) actions.Children.Add(new HyperlinkButton { Content = "Open", NavigateUri = new Uri(item.Link) });
        if (item.Game != null)
        {
            var go = new Button { Content = "Go to game" };
            go.Click += (_, _) => { BellFlyout.Hide(); Navigate(typeof(DetailPage), item.Game); };
            actions.Children.Add(go);
        }
        if (actions.Children.Count > 0) text.Children.Add(actions);

        var dismiss = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 10 }, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Padding = new Thickness(6), VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(dismiss, "Dismiss");
        AutomationProperties.SetName(dismiss, "Dismiss");
        dismiss.Click += (_, _) =>
        {
            NewsState.Dismiss(App.Store, item.Id, Feed());
            var (shown, _) = Notifications();
            FillBell(shown, new HashSet<string>());
            ShowBell();
        };
        var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Children = { text, dismiss } };
        Grid.SetColumn(dismiss, 1);
        return new Border { Style = Res<Style>("Card"), Padding = new Thickness(12), Child = row };
    }

    static T Res<T>(string key) => (T)Application.Current.Resources[key];

    async void OnRestartToUpdate(object _, RoutedEventArgs __)
    {
        UpdateButton.IsEnabled = false;
        UpdateText.Text = "Finishing the compile…";
        SavePlacement();   // the restart exits without closing the window
        if (await Updater.RestartAsync()) return;   // exits
        (UpdateButton.IsEnabled, UpdateText.Text) = (true, "Restart to update");
        ShowUpdate();
    }

    void OnNavSelection(NavigationView _, NavigationViewSelectionChangedEventArgs e)
    {
        if (syncingNav) return;
        var page = pages.FirstOrDefault(p => p.Item == e.SelectedItemContainer).Page ?? typeof(LibraryPage);
        // a game's page belongs to Library: the window's first load selects Library after a toast opened a game
        if (ContentFrame.SourcePageType != page && !(page == typeof(LibraryPage) && ContentFrame.SourcePageType == typeof(DetailPage)))
            ContentFrame.Navigate(page);
    }

    // On a game's page Library stays selected, so clicking it raises no SelectionChanged.
    void OnNavInvoked(NavigationView _, NavigationViewItemInvokedEventArgs e)
    {
        if (e.InvokedItemContainer == LibraryItem && ContentFrame.SourcePageType == typeof(DetailPage)) ShowLibrary();
    }

    /// <summary>Back to the Library list from a game's page, at its scroll position when it is the page behind.</summary>
    public void ShowLibrary()
    {
        if (ContentFrame.CanGoBack && ContentFrame.BackStack[^1].SourcePageType == typeof(LibraryPage)) ContentFrame.GoBack();
        else Navigate(typeof(LibraryPage));
    }

    void OnNavigated(object _, NavigationEventArgs e)
    {
        syncingNav = true;
        Nav.SelectedItem = pages.FirstOrDefault(p => p.Page == e.SourcePageType).Item ?? LibraryItem;
        syncingNav = false;
    }

    void OnPaneToggle(TitleBar _, object __) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    double Scale => GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;

    void Resize(int width) => AppWindow.Resize(new SizeInt32((int)(width * Scale), (int)(880 * Scale)));

    void SetMinimumSize()
    {
        var presenter = (OverlappedPresenter)AppWindow.Presenter;
        (presenter.PreferredMinimumWidth, presenter.PreferredMinimumHeight) = ((int)(MinWidth * Scale), (int)(MinHeight * Scale));
    }

    /// <summary>Puts the window where it was last closed (window.json in <paramref name="store"/>'s folder): its restored
    /// bounds, and maximized once it shows if it was. Never shows it: a --tray start stays in the notification area. Bounds
    /// on a monitor that's gone are moved onto one by Windows (SetWindowPlacement).</summary>
    public void RestorePlacement(AppStore store)
    {
        placement = store;
        if (store.LoadWindow() is not { } saved) return;
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var wp = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>(), ShowCmd = SwHide,
                                       Normal = new(saved.Left, saved.Top, saved.Left + saved.Width, saved.Top + saved.Height) };
        var dpi = GetDpiForWindow(hwnd);
        SetWindowPlacement(hwnd, ref wp);
        // moved onto a monitor of another scale: WM_DPICHANGED resized it on the way, and the minimums were the old monitor's.
        // It is there now, so with this monitor's minimums the bounds stick.
        if (GetDpiForWindow(hwnd) != dpi)
        {
            SetMinimumSize();
            SetWindowPlacement(hwnd, ref wp);
        }
        maximizeOnShow = saved.Maximized;
    }

    /// <summary>Saves where the window is (<see cref="RestorePlacement"/>) while it shows: as it closes, quits, restarts to
    /// update or Windows ends the session. Hidden, it keeps what was saved as it hid.</summary>
    public void SavePlacement()
    {
        if (placement == null || !AppWindow.IsVisible) return;
        var wp = new WindowPlacement { Length = Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(WinRT.Interop.WindowNative.GetWindowHandle(this), ref wp)) return;
        var r = wp.Normal;   // the restored bounds, also while maximized or minimized
        try { placement.SaveWindow(new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, WindowBounds.WasMaximized(wp.ShowCmd, wp.Flags))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the next start opens at the default
    }

    /// <summary>Shows the window off-screen, never activated and not in the task switcher: the user may be gaming.</summary>
    /// <summary>Pages and the caption buttons at once; a dialog takes it as it opens (<see cref="App.ShowAsync"/>).</summary>
    public void ApplyTheme(AppTheme? theme)
    {
        Root.RequestedTheme = theme switch { AppTheme.Light => ElementTheme.Light, AppTheme.Dark => ElementTheme.Dark, _ => ElementTheme.Default };
        AppWindow.TitleBar.PreferredTheme = theme switch { AppTheme.Light => TitleBarTheme.Light, AppTheme.Dark => TitleBarTheme.Dark, _ => TitleBarTheme.UseDefaultAppMode };
    }

    public ElementTheme Theme => Root.RequestedTheme;

    public void ShowOffscreen()
    {
        AppWindow.IsShownInSwitchers = false;
        AppWindow.Move(new PointInt32(-20000, -20000));
        AppWindow.Show(false);
        Bindings.Update();   // a window's x:Bind starts at its first activation, which never comes
    }

    /// <summary>--screenshots: show off-screen (<see cref="ShowOffscreen"/>), capture each page in dark and light, exit.</summary>
    public async Task TakeScreenshotsAsync(string dir, FakeScsKiller fake)
    {
        Directory.CreateDirectory(dir);
        ShowOffscreen();
        ShotBackground.Visibility = Visibility.Visible;
        ElementTheme[] themes = [ElementTheme.Dark, ElementTheme.Light];

        // <name>-<theme><part>.png
        async Task Shot(string name, int delay = 1000, string part = "")
        {
            await Task.Delay(delay);
            await SavePngAsync(Root, Path.Combine(dir, $"{name}-{Root.RequestedTheme.ToString().ToLowerInvariant()}{part}.png"));
        }

        // AMD's kept-aside caches: the game page's states, Settings and its Manage list. SCSKILLER_SHOTS=amd-profiles takes only these.
        async Task AmdProfiles()
        {
            var gpu = fake.Vendor;
            fake.Vendor = new FakeScsKiller.FakeAmdVendor();
            fake.RenameDriver("610.88", fake.Vendor.Gpu.DriverVersion);
            ShowGpu();
            const string gb = "steam:2909400", restored = "epic:3300000", locked = "xbox:Sample.AtomicHeart", ac = "gaijin:warthunder";
            var acWas = fake.Games.First(g => g.Game.Id == ac).AntiCheat;
            fake.SetKept(gb, new(2_870_000_000, 1_650_000_000));
            fake.SetKept(restored, new(2_360_000_000, 0, DateTimeOffset.Now.AddHours(-1)));
            fake.SetKept(locked, new(4_300_000_000, 0, DateTimeOffset.Now.AddHours(-3), 1, "the Radeon overlay"));
            fake.SetKept(ac, new(1_930_000_000, 1_240_000_000), AntiCheat.EasyAntiCheat);
            foreach (var theme in themes)
            {
                Root.RequestedTheme = theme;
                foreach (var (name, id, late) in new[] { ("kept", gb, false), ("restored", restored, false), ("late", restored, true), ("locked", locked, false), ("anticheat", ac, false) })
                {
                    if (id == restored) fake.SetKept(restored, new(2_360_000_000, 0, DateTimeOffset.Now.AddHours(-1), Late: late));
                    Navigate(typeof(DetailPage), id);
                    await Task.Delay(1000);
                    ((DetailPage)ContentFrame.Content).ScrollToKept();
                    await Shot("amd-kept-" + name, 400);
                }
                Navigate(typeof(SettingsPage));
                await Shot("amd-kept-settings");
                var manage = ((SettingsPage)ContentFrame.Content).ManageKeptDialog();
                manage.RequestedTheme = Root.RequestedTheme;
                _ = App.ShowAsync(manage);
                await Task.Delay(800);
                await SavePngAsync(manage, Path.Combine(dir, $"amd-kept-manage-{Root.RequestedTheme.ToString().ToLowerInvariant()}.png"));
                manage.Hide();
                await Task.Delay(300);
            }
            foreach (var id in new[] { gb, restored, locked }) fake.SetKept(id, null);
            fake.SetKept(ac, null, acWas);
            fake.RenameDriver(fake.Vendor.Gpu.DriverVersion, "610.88");
            fake.Vendor = gpu;
            ShowGpu();
            Root.RequestedTheme = ElementTheme.Dark;
        }
        // "Report a problem" on the first game: the dialog with a message typed, then its thanks. SCSKILLER_SHOTS=problem takes only these.
        async Task Problem()
        {
            Navigate(typeof(DetailPage), fake.Games[0].Game.Id);
            foreach (var theme in themes)
            {
                Root.RequestedTheme = theme;
                await Task.Delay(800);
                foreach (var (name, id) in new[] { ("problem", (string?)null), ("problem-thanks", "3f9c2e1a") })
                {
                    var d = ((DetailPage)ContentFrame.Content).ProblemDialog(id, "The game still stutters in the first town after a full compile.");
                    d.RequestedTheme = theme;
                    _ = App.ShowAsync(d);
                    await Task.Delay(800);
                    await SavePngAsync(d, Path.Combine(dir, $"{name}-{theme.ToString().ToLowerInvariant()}.png"));
                    d.Hide();
                    await Task.Delay(300);
                }
            }
            Root.RequestedTheme = ElementTheme.Dark;
        }
        switch (Environment.GetEnvironmentVariable("SCSKILLER_SHOTS"))
        {
            case "amd-profiles": await AmdProfiles(); Application.Current.Exit(); return;
            case "problem": await Problem(); Application.Current.Exit(); return;
        }
        await AmdProfiles();
        await Problem();

        // A scan in progress: first run (nothing listed yet), its games listed before they're read, then a rescan.
        fake.HideGames = true;
        fake.HoldScan = new();
        fake.ScanProgress = "Scanning Steam… 120 games found";
        Navigate(typeof(LibraryPage));   // its first load scans
        foreach (var name in new[] { "library-scanning-empty", "library-after-update-empty", "library-first-scan", "library-scanning", "library-after-update" })
        {
            if (name == "library-first-scan")
            {
                fake.ScanProgress = "Checking each game… 4 of 120";
                fake.Checking = true;
                fake.ShowGames();
            }
            else if (name == "library-scanning")
            {
                fake.ScanProgress = "Checking each game… 40 of 120";
                fake.Checking = false;
                fake.ShowGames();
            }
            fake.AfterUpdateLine = name.Contains("after-update") ? ScsKiller.AfterUpdateText : null;
            foreach (var theme in themes)
            {
                Root.RequestedTheme = theme;
                await Shot(name);
            }
        }
        fake.AfterUpdateLine = null;
        fake.HoldScan.SetResult();
        fake.HoldScan = null;
        fake.ScanProgress = null;
        await Task.Delay(1000);
        (fake.Checking, fake.NotChecked) = (true, true);   // HDD mode: the games past the first three never read
        fake.ShowGames();
        foreach (var theme in themes)
        {
            Root.RequestedTheme = theme;
            await Shot("library-hdd");
        }
        (fake.Checking, fake.NotChecked) = (false, false);
        fake.ShowGames();

        Root.RequestedTheme = ElementTheme.Dark;
        Navigate(typeof(QueuePage));
        await Shot("queue-empty");
        // After an update: plan checks of the compiled games, one line in the header, no rows (they stay for the queue shots).
        fake.CheckPlans(fake.Games[0].Game.Id, "xbox:Sample.AtomicHeart", "epic:3300000");
        foreach (var theme in themes)
        {
            Root.RequestedTheme = theme;
            await Shot("queue-checks");
        }
        Root.RequestedTheme = ElementTheme.Dark;

        // Queue: one done (Orcs Must Die! 3), one running at 54.8% (Stellar Blade Demo), three waiting; Ghostrunner stays unqueued.
        string ff7 = fake.Games[0].Game.Id;
        fake.Enqueue("steam:1522820");
        fake.StartQueue();
        fake.JumpTo(1);
        await Task.Delay(1200);   // the next tick finishes it and the empty queue stops
        foreach (var id in new[] { "steam:3489700", "steam:1627720", "steam:990080", "steam:1817230" }) fake.Enqueue(id);
        fake.StartQueue();
        fake.JumpTo(0.548);
        fake.MoveInQueue("steam:990080", 0);    // Hogwarts Legacy to the top
        fake.MoveInQueue("steam:1627720", 2);   // Lies of P to the end: Hogwarts Legacy, Hi-Fi RUSH, Lies of P

        foreach (var theme in themes)
        {
            Root.RequestedTheme = theme;
            foreach (var (name, page, arg) in new (string, Type, object?)[]
                     { ("library", typeof(LibraryPage), null), ("detail", typeof(DetailPage), ff7),
                       ("detail-new", typeof(DetailPage), "steam:1139900"),   // never compiled: estimated cache, Clear cache disabled
                       ("detail-stutter", typeof(DetailPage), "steam:990080"),   // stutters without a compile, with a source link
                       ("detail-kept-cache", typeof(DetailPage), "steam:999999901"),   // compiled, then marked not supported: Clear it
                       ("detail-maximum", typeof(DetailPage), "steam:3600000"),   // Maximum mode suggested for this game
                       ("detail-partial", typeof(DetailPage), "steam:990080"), // Hogwarts Legacy: a partial plan, the recording hint
                       ("detail-compiling", typeof(DetailPage), "steam:3489700"),   // mid-compile: the queue's progress on the game page
                       ("detail-report-off", typeof(DetailPage), "xbox:Sample.AtomicHeart"),   // Rate this compile off (not played since), "did not help"
                       ("detail-similar-warning", typeof(DetailPage), "epic:3300000"),   // similar systems: ran worse
                       ("queue", typeof(QueuePage), null), ("cache", typeof(CachePage), null), ("settings", typeof(SettingsPage), null) })
            {
                Navigate(page, arg);
                await Shot(name);
            }
            if (ContentFrame.Content is SettingsPage settings) settings.ScrollToEnd();   // the lower half: NVIDIA Auto Shader Compilation
            await Shot("settings-end", 400);
            if (ContentFrame.Content is SettingsPage newShaders) newShaders.ScrollToNewShaders();
            await Shot("settings-new-shaders", 400);
            Navigate(typeof(CachePage));
            await Task.Delay(1000);
            if (ContentFrame.Content is CachePage cache)
            {
                cache.ScrollToEnd();   // the lower groups
                await Shot("cache-end", 400);
                var (clear, _) = cache.ClearEverythingDialog(cache.Vm.Inventory!);
                clear.RequestedTheme = Root.RequestedTheme;
                _ = App.ShowAsync(clear);
                await Task.Delay(800);
                await SavePngAsync(clear, Path.Combine(dir, $"cache-clear-everything-{Root.RequestedTheme.ToString().ToLowerInvariant()}.png"));
                clear.Hide();
                await Task.Delay(300);
                await cache.Vm.DeleteAsync(cache.Vm.Inventory!.Everything(withOthers: false));   // the sample deletes nothing
                cache.ScrollToTop();
                await Shot("cache-deleted", 400);
            }

            var news = App.WhatsNewDialog(Root.XamlRoot, WhatsNew.Section(WhatsNew.Changelog, "1.2.4")!);
            news.RequestedTheme = Root.RequestedTheme;
            _ = App.ShowAsync(news);
            await Task.Delay(800);
            await SavePngAsync(news, Path.Combine(dir, $"whatsnew-{Root.RequestedTheme.ToString().ToLowerInvariant()}.png"));
            news.Hide();
            await Task.Delay(300);

            var check = App.ActiveCheckDialog(Root.XamlRoot);
            check.RequestedTheme = Root.RequestedTheme;
            _ = App.ShowAsync(check);
            await Task.Delay(800);
            await SavePngAsync(check, Path.Combine(dir, $"daily-check-{Root.RequestedTheme.ToString().ToLowerInvariant()}.png"));
            check.Hide();
            await Task.Delay(300);

            // Rate this compile's popup on FF7 (on, "removed or reduced"): this install's first, a later one, after an earlier report of the build, thanked
            Navigate(typeof(DetailPage), ff7);
            await Task.Delay(800);
            foreach (var (name, sent, thanks, once) in new[] { ("report", (CompatSent?)null, false, false), ("report-later", null, false, true),
                         ("report-sent", new CompatSent(CompatChoice.Better, DateTimeOffset.Now.AddDays(-4)), false, true), ("report-thanks", null, true, true) })
            {
                fake.Settings = fake.Settings with { CompatReportSent = once };
                var reportDialog = ((DetailPage)ContentFrame.Content).ReportDialog(sent, thanks);
                reportDialog.RequestedTheme = Root.RequestedTheme;
                _ = App.ShowAsync(reportDialog);
                await Task.Delay(800);
                await SavePngAsync(reportDialog, Path.Combine(dir, $"{name}-{Root.RequestedTheme.ToString().ToLowerInvariant()}.png"));
                reportDialog.Hide();
                await Task.Delay(300);
            }
            fake.Settings = fake.Settings with { CompatReportSent = false };

            Navigate(typeof(QueuePage));
            await Task.Delay(1000);
            ((QueuePage)ContentFrame.Content).Select("steam:990080", "steam:1627720");   // Hogwarts Legacy and Lies of P
            await Shot("queue-selected", 400);

            // The Patreon card: signed out, signed in with "db" (the share prompt), signed out again (the download checkbox
            // turns off live, on the same page), signed in without "db".
            Navigate(typeof(SettingsPage));
            foreach (var (name, ent) in new (string, string[]?)[] { ("signedout", null), ("db", ["db", "beta"]), ("signedout-again", null), ("nodb", ["beta"]) })
            {
                if (ent == null) await App.Account.SignOutAsync();
                else { FakeAccount.Ent = ent; await App.Account.SignInAsync(); }
                await Task.Delay(1000);
                ((SettingsPage)ContentFrame.Content).ScrollToPatreon();
                await Shot("settings-account-" + name, 400);
            }
            await App.Account.SignOutAsync();
        }

        // Game details at the default width and the minimum one (the pane is compact), top and bottom of the page:
        // a partial stale plan with many tags, warmed with a session, never compiled, needs a recording, anti-cheat, fully covered, compiled without its ray tracing.
        foreach (var (width, size) in new[] { (1280, "wide"), (MinWidth, "min") })
        {
            Resize(width);
            foreach (var theme in themes)
            {
                Root.RequestedTheme = theme;
                if (width == MinWidth)
                {
                    foreach (var (name, page) in new[] { ("library-min", typeof(LibraryPage)), ("queue-min", typeof(QueuePage)), ("settings-min", typeof(SettingsPage)) })
                    {
                        Navigate(page);
                        await Shot(name);
                    }
                    ((SettingsPage)ContentFrame.Content).ScrollToEnd();
                    await Shot("settings-min", 400, "-end");
                }
                foreach (var (name, id) in new[] { ("hogwarts", "steam:990080"), ("ff7", ff7), ("ghostrunner", "steam:1139900"),
                                                   ("palworld", "xbox:1623730"), ("eldenring", "steam:1245620"),
                                                   ("atomicheart", "xbox:Sample.AtomicHeart"), ("darwin", "epic:3300000"), ("townfall", "steam:3600000"),
                                                   ("added", "manual:5f1c0e9a2b7d4c30"), ("tekken", "steam:1778820"), ("racer", "steam:999999901"), ("unreached", "xbox:Sample.HollowCircuit"), ("bothapis", "gaijin:warthunder"),
                                                   ("guess-both", "xbox:CoffeeStainStudios.DeepRockGalactic"),
                                                   ("guess-version", "steam:3900010"), ("guess-dx", "steam:3900020"), ("rebound", "steam:1292630"), ("no-launch", "hoyoplay:nap_global"),
                                                   ("ran-other-exe", "ubisoft:13504"), ("game-changed", "steam:3900030"), ("never-loaded", "steam:3900040"), ("other-exe", "steam:3900050"),
                                                   ("foreign", "steam:3900060"), ("foreign-on", "steam:3900060"), ("foreign-off", "steam:3900060"),
                                                   ("guess-picked", "xbox:CoffeeStainStudios.DeepRockGalactic"), ("guess-overruled", "steam:3900020"), ("sure-picked", "steam:1139900") })
                {
                    if (name is "guess-both" or "guess-dx" or "ghostrunner") { fake.ProveApi(id, null); fake.SetApiOverride(id, null); }
                    if (name == "guess-picked") fake.SetApiOverride(id, DxApi.D3D12);
                    if (name == "foreign") fake.MakeForeign(id);
                    if (name == "foreign-on") fake.SetRecorderOverride(id, RecorderOverride.On);   // what the switch's one click calls
                    if (name == "foreign-off") fake.SetRecorderOverride(id, RecorderOverride.Off);
                    if (name == "sure-picked") fake.SetApiOverride(id, DxApi.D3D11);
                    if (name == "guess-overruled") { fake.SetApiOverride(id, DxApi.D3D11); fake.ProveApi(id, "D3D12"); }
                    Navigate(typeof(DetailPage), id);
                    string file = $"d-{name}-{size}";
                    await Shot(file);
                    if (ContentFrame.Content is DetailPage d && d.ScrollToEnd()) await Shot(file, 400, "-end");
                    if (name == "tekken" && ContentFrame.Content is DetailPage partly)   // the Why? dialog, rendered on its own
                    {
                        var why = partly.PartlyWhyDialog();
                        why.RequestedTheme = Root.RequestedTheme;
                        _ = App.ShowAsync(why);
                        await Task.Delay(800);
                        await SavePngAsync(why, Path.Combine(dir, $"d-tekken-why-{size}-{Root.RequestedTheme.ToString().ToLowerInvariant()}.png"));
                        why.Hide();
                        await Task.Delay(300);
                    }
                    if (name == "ff7" && ContentFrame.Content is DetailPage open)   // every count, under Details
                    {
                        open.ShowDetails();
                        await Task.Delay(600);
                        open.ScrollToEnd();
                        await Shot(file, 400, "-details");
                        if (open.ScrollToFrames()) await Shot(file, 400, "-frames");
                    }
                }
            }
        }
        foreach (var id in new[] { "xbox:CoffeeStainStudios.DeepRockGalactic", "steam:3900020", "steam:1139900" }) { fake.ProveApi(id, null); fake.SetApiOverride(id, null); }   // the library shots: as detected again
        Resize(1280);

        // Library search ("life": one game on Steam, one on EA) and the store sections at the end of the list.
        foreach (var theme in themes)
        {
            Root.RequestedTheme = theme;
            Navigate(typeof(LibraryPage));
            var library = (LibraryPage)ContentFrame.Content;
            // library-groups: Palworld's icon (a real exe) after the detail pages showed it at 56 px: it must not come back empty
            foreach (var (name, search) in new[] { ("library-back", ""), ("library-search", "life"), ("library-nomatch", "zelda"), ("library-rt", "darwin"), ("library-partly", "tekken"), ("library-unsupported", "racer"), ("library-unreached", "hollow"), ("library-bothapis", "thunder"), ("library-guessed", "guessed"), ("library-offline", "elden"), ("library-nothing-recorded", "valhalla"), ("library-other-exe", "ashen"), ("library-groups", "") })
            {
                library.SearchText = search;
                await Task.Delay(400);
                if (name == "library-groups") library.ScrollToEnd();
                await Shot(name, 800);
            }
            library.Vm.ShowHidden = true;   // the sample's hidden game under the search
            await Shot("library-hidden", 800);
            library.Vm.ShowHidden = false;
            // largest cache first, then only the compiled games, then back to the default order
            library.SearchText = "";
            library.Vm.SortBy(LibrarySort.CacheDescending, LibrarySort.CacheAscending);
            await Shot("library-sort-cache", 800);
            library.Vm.ShowIndex = (int)LibraryShow.Compiled;
            await Shot("library-show-compiled", 800);
            library.Vm.ShowIndex = (int)LibraryShow.All;
            library.Vm.SortBy(LibrarySort.Status, LibrarySort.Status);
            // About: as it opens, then with the first licence and the trademarks open
            Navigate(typeof(AboutPage));
            await Shot("about");
            foreach (var (name, licence) in new[] { ("about-trademarks", false), ("about-licence", true) })
            {
                ((AboutPage)ContentFrame.Content).ShowExpanded(licence);
                await Shot(name, 800);
            }
        }

        // The Library's last sections at the minimum width: it kept the scroll position of library-groups.
        Resize(MinWidth);
        foreach (var theme in themes)
        {
            Root.RequestedTheme = theme;
            Navigate(typeof(LibraryPage));
            await Shot("library-min", part: "-end");
        }

        // A compile over 16 GB queued: its warning on NVIDIA past the 16 GB limit ("queue" has the cache close to it), then on AMD.
        Resize(1280);
        var sampleGpu = fake.Vendor;
        fake.Enqueue("steam:1285190");
        foreach (var (name, gpu) in new[] { ("queue-large", (GpuInfo?)null), ("queue-large-amd", new GpuInfo(GpuVendor.Amd, "AMD Radeon", "25.10.1", 0, 16UL << 30)) })
        {
            if (gpu != null) { fake.Vendor = new UnsupportedVendor(gpu); ShowGpu(); }
            foreach (var theme in themes)
            {
                Root.RequestedTheme = theme;
                Navigate(typeof(QueuePage));
                await Shot(name);
            }
        }

        // The theme setting over the shots' own: pages, a flyout and a dialog follow it at once
        foreach (var setting in new[] { AppTheme.Light, AppTheme.Dark })
        {
            fake.Settings = fake.Settings with { Theme = setting };
            ApplyTheme(setting);
            var tag = setting.ToString().ToLowerInvariant();
            foreach (var (name, page) in new[] { ("settings", typeof(SettingsPage)), ("library", typeof(LibraryPage)) })
            {
                Navigate(page);
                await Task.Delay(1000);
                await SavePngAsync(Root, Path.Combine(dir, $"theme-{tag}-{name}.png"));
            }
            BellFlyout.ShowAt(BellButton);
            await Task.Delay(800);
            var check = new List<string>();
            // a flyout's popup never renders to a bitmap off-screen: its theme is written down instead
            foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(Root.XamlRoot))
                if (popup.Child is FrameworkElement shown) check.Add($"flyout {shown.ActualTheme}");
            BellFlyout.Hide();
            var dialog = new ContentDialog
            {
                XamlRoot = Root.XamlRoot, Title = "Rename Sample", Content = new TextBox { Header = "Name", Text = "Sample" }, PrimaryButtonText = "Rename", CloseButtonText = "Cancel",
            };
            _ = App.ShowAsync(dialog);
            await Task.Delay(800);
            check.Add($"dialog {dialog.ActualTheme}");
            if (dialog.ActualWidth > 0) await SavePngAsync(dialog, Path.Combine(dir, $"theme-{tag}-dialog.png"));
            dialog.Hide();
            await Task.Delay(300);
            File.WriteAllLines(Path.Combine(dir, $"theme-{tag}.txt"), check);
        }
        fake.Settings = fake.Settings with { Theme = null };
        ApplyTheme(null);

        // An Intel GPU: the Library says it can't compile there.
        fake.Vendor = new UnsupportedVendor(new GpuInfo(GpuVendor.Intel, "Intel Graphics", "32.0.101.6881", 0, 128UL << 20));
        ShowGpu();
        foreach (var theme in themes)
        {
            Root.RequestedTheme = theme;
            Navigate(typeof(LibraryPage));
            ((LibraryPage)ContentFrame.Content).Vm.Refresh();
            await Shot("library-intel");
        }

        // Before and after: the game page's states, the intro, the Library's chips, the report window and its image.
        Resize(1280);
        fake.Vendor = sampleGpu;
        ShowGpu();
        const string lop = "steam:1139900";   // Ghostrunner: not queued
        foreach (var theme in themes)
        {
            Root.RequestedTheme = theme;
            foreach (var (name, info) in new (string, BeforeAfterInfo?)[]
                     {
                         ("none", null), ("armed", new(BeforeAfterStage.BeforeArmed, 5, null, 0)),
                         ("clear-pending", new(BeforeAfterStage.BeforeArmed, 5, "Couldn't clear the cache: a file is in use", 0, ClearPending: true)), ("playing", new(BeforeAfterStage.BeforePlaying, 5, null, 78)),
                         ("compiling", new(BeforeAfterStage.Compiling, 5, null, 0)),
                         ("compile-stopped", new(BeforeAfterStage.Compiling, 5, "The compile was stopped. Compile again to go on.", 0)),
                         ("after-ready", new(BeforeAfterStage.AfterReady, 5, null, 0)),
                         ("after-short", new(BeforeAfterStage.AfterArmed, 5, "The after recording was 2 min of play. Play at least 3, the game compiles again first.", 0)),
                         ("timesup", new(BeforeAfterStage.AfterPlaying, 5, null, 305)), ("done", new(BeforeAfterStage.Done, 5, null, 0)),
                         ("voided", new(BeforeAfterStage.AfterReady, 5, ScsKiller.DriverVoidNote, 0)),
                     })
            {
                fake.SetBeforeAfter(lop, info);
                Navigate(typeof(DetailPage), lop);
                await Shot("ba-" + name);
            }
        }
        Resize(MinWidth);
        Root.RequestedTheme = ElementTheme.Dark;
        foreach (var (name, info) in new (string, BeforeAfterInfo)[]
                 {
                     ("playing", new(BeforeAfterStage.BeforePlaying, 10, null, 78)), ("armed", new(BeforeAfterStage.BeforeArmed, 5, null, 0)),
                     ("done", new(BeforeAfterStage.Done, 5, null, 0)), ("compile-stopped", new(BeforeAfterStage.Compiling, 5, "The compile was stopped. Compile again to go on.", 0)),
                 })
        {
            fake.SetBeforeAfter(lop, info);
            Navigate(typeof(DetailPage), lop);
            await Shot($"ba-{name}-min");   // the action row wraps
        }
        Resize(1280);
        // no pair, and one can't start: the button greyed, the reason under it
        fake.SetBeforeAfter(lop, null);
        foreach (var (name, why) in new[]
                 {
                     ("recorder-off", "Turn the recorder on for this game first"), ("recorder-cant", $"The recorder can't run in this game: {ScsKiller.SkipForeignDll}"),
                     ("api", "Only for DirectX 12 and DirectX 11 games"), ("anti-cheat", "Not for games with anti-cheat"),
                     ("no-stutter", "Not for games without shader compilation stutter"), ("refused", ScsKiller.NeverSawNote),
                     ("running", "Ghostrunner is running"), ("compiling", "Wait for the compile to finish"), ("shared", "Its driver cache is shared with Hi-Fi RUSH"),
                 })
        {
            fake.BaBlocked[lop] = why;
            Navigate(typeof(DetailPage), lop);
            await Shot("ba-blocked-" + name);
        }
        fake.BaBlocked.Remove(lop);
        Navigate(typeof(DetailPage), "steam:3200000");   // Unsupported: the status reason
        await Shot("ba-blocked-status");

        // the intro: first time (the steps), then once seen (folded behind "How it works")
        fake.SetBeforeAfter(lop, null);
        Navigate(typeof(DetailPage), lop);
        await Task.Delay(800);
        foreach (var (name, seen) in new[] { ("ba-intro", false), ("ba-intro-seen", true) })
        {
            fake.Settings = fake.Settings with { BeforeAfterIntroSeen = seen };
            _ = BeforeAfterUi.IntroAsync(Root.XamlRoot, "Ghostrunner", BeforeAfterUi.Intro.Start);
            await Task.Delay(1000);
            if (BeforeAfterUi.Showing is { } dialog)
            {
                await SavePngAsync(dialog, Path.Combine(dir, $"{name}-dark.png"));
                dialog.Hide();
            }
            await Task.Delay(400);
        }

        fake.SetBeforeAfter("steam:1627720", new(BeforeAfterStage.AfterReady, 5, null, 0));
        fake.SetBeforeAfter("steam:1139900", new(BeforeAfterStage.Compiling, 5, null, 0));
        fake.SetBeforeAfter("steam:3489700", new(BeforeAfterStage.AfterPlaying, 5, null, 100));
        Navigate(typeof(LibraryPage));
        await Task.Delay(600);
        ((LibraryPage)ContentFrame.Content).ScrollTo("steam:3489700");
        await Shot("ba-library");

        fake.SetBeforeAfter(lop, new(BeforeAfterStage.Done, 5, null, 0));
        if (await ReportWindow.OpenAsync(lop, offscreen: true) is { } report)
        {
            foreach (var theme in themes)
            {
                report.ShotRoot.RequestedTheme = theme;
                report.ScrollToTop();
                await Task.Delay(1000);
                var t = theme.ToString().ToLowerInvariant();
                await SavePngAsync(report.ShotRoot, Path.Combine(dir, $"ba-report-{t}.png"));
                report.ScrollToEnd();
                await Task.Delay(600);
                await SavePngAsync(report.ShotRoot, Path.Combine(dir, $"ba-report-{t}-end.png"));
            }
            using (var png = await report.PngAsync())
            using (var file = File.Create(Path.Combine(dir, "ba-card.png")))
                await png.AsStreamForRead().CopyToAsync(file);
            report.Close();
        }
        Application.Current.Exit();
    }

    // the acrylic behind a window isn't in a bitmap of its content: the base colour of each theme instead

    static async Task SavePngAsync(UIElement element, string path)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(element);
        var pixels = await rtb.GetPixelsAsync();
        using var file = File.Create(path);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, file.AsRandomAccessStream());
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels.ToArray());
        await encoder.FlushAsync();
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint hwnd);
    [DllImport("user32.dll")] static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement wp);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(nint hwnd, ref WindowPlacement wp);

    const int SwHide = 0;

    [StructLayout(LayoutKind.Sequential)] record struct NativeRect(int Left, int Top, int Right, int Bottom);
    [StructLayout(LayoutKind.Sequential)] struct WindowPlacement { public int Length, Flags, ShowCmd; public PointInt32 Min, Max; public NativeRect Normal; }
}
