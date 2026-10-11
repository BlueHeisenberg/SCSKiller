using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
        AppTitleBar.Subtitle = I18n.T("AppSubtitle");
        UpdateText.Text = I18n.T("UpdateRestart");
        LibraryItem.Content = I18n.T("NavLibrary");
        QueueItem.Content = I18n.T("NavQueue");
        SettingsItem.Content = I18n.T("NavSettings");
        AboutItem.Content = I18n.T("NavAbout");
        pages = [(QueueItem, typeof(QueuePage)), (SettingsItem, typeof(SettingsPage)), (AboutItem, typeof(AboutPage))];
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
            (App.Core.Settings.InstallUpdatesAutomatically ? " Otherwise it installs the next time SCSKiller starts or quits." : ""));
    }

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

        // A scan in progress: first run (nothing listed yet), then with the games listed (a rescan).
        fake.HideGames = true;
        fake.HoldScan = new();
        Navigate(typeof(LibraryPage));   // its first load scans
        foreach (var (name, show) in new[] { ("library-scanning-empty", false), ("library-scanning", true) })
        {
            if (show) fake.ShowGames();
            foreach (var theme in themes)
            {
                Root.RequestedTheme = theme;
                await Shot(name);
            }
        }
        fake.HoldScan.SetResult();
        fake.HoldScan = null;
        await Task.Delay(1000);

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
                       ("detail-stutter", typeof(DetailPage), "steam:990080"),   // known to stutter, with a source link
                       ("detail-partial", typeof(DetailPage), "steam:990080"), // Hogwarts Legacy: a partial plan, the recording hint
                       ("queue", typeof(QueuePage), null), ("settings", typeof(SettingsPage), null) })
            {
                Navigate(page, arg);
                await Shot(name);
            }
            if (ContentFrame.Content is SettingsPage settings) settings.ScrollToEnd();   // the lower half: NVIDIA Auto Shader Compilation
            await Shot("settings-end", 400);

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
                                                   ("added", "manual:5f1c0e9a2b7d4c30"), ("tekken", "steam:1778820"), ("racer", "steam:4078430"), ("unreached", "xbox:Sample.HollowCircuit"), ("bothapis", "gaijin:warthunder"),
                                                   ("guess-both", "xbox:CoffeeStainStudios.DeepRockGalactic"),
                                                   ("guess-version", "steam:3900010"), ("guess-dx", "steam:3900020"), ("rebound", "steam:1292630"),
                                                   ("ran-other-exe", "ubisoft:13504"), ("game-changed", "steam:3900030"), ("never-loaded", "steam:3900040") })
                {
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
        Resize(1280);

        // Library search ("life": one game on Steam, one on EA) and the store sections at the end of the list.
        foreach (var theme in themes)
        {
            Root.RequestedTheme = theme;
            Navigate(typeof(LibraryPage));
            var library = (LibraryPage)ContentFrame.Content;
            // library-groups: Palworld's icon (a real exe) after the detail pages showed it at 56 px: it must not come back empty
            foreach (var (name, search) in new[] { ("library-back", ""), ("library-search", "life"), ("library-nomatch", "zelda"), ("library-rt", "darwin"), ("library-partly", "tekken"), ("library-unsupported", "racer"), ("library-unreached", "hollow"), ("library-bothapis", "thunder"), ("library-guessed", "guessed"), ("library-offline", "elden"), ("library-nothing-recorded", "valhalla"), ("library-groups", "") })
            {
                library.SearchText = search;
                await Task.Delay(400);
                if (name == "library-groups") library.ScrollToEnd();
                await Shot(name, 800);
            }
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
        Application.Current.Exit();
    }

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
