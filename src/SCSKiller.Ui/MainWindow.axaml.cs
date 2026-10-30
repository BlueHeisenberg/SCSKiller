using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Pages;
using SCSKiller.Ui.Platform;

namespace SCSKiller.Ui;

public sealed partial class MainWindow : Window
{
    /// <summary>The pane opens inline from this width (WinUI's ExpandedModeThresholdWidth); narrower, it opens over the page.</summary>
    const double ExpandedWidth = 1258;
    /// <summary>The drawn minimize, maximize and close buttons: 3 × CaptionButtonWidth.</summary>
    const double CaptionButtonsWidth = 3 * 46;

    readonly (Button Item, Type Page)[] pages;
    readonly Dictionary<Type, Control> cached = [];
    Type? shown;
    readonly List<(Type Page, object? Arg)> history = [];
    int at = -1;
    bool? wide;
    AppStore? placement;   // where window.json goes (RestorePlacement); none: the screenshots' window is never saved
    readonly nint hwnd;   // 0 off Windows and in the screenshots

    public MainWindow()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnMouseNav, RoutingStrategies.Tunnel);
        pages = [(LibraryItem, typeof(LibraryPage)), (QueueItem, typeof(QueuePage)), (CacheItem, typeof(CachePage)), (SettingsItem, typeof(SettingsPage)), (AboutItem, typeof(AboutPage))];
        ShowGpu();
        var bell = new Coalesced(Host.Ui, () => { ShowBell(); Activation.ToastNews(); });   // targeting reads the games, the account and the news
        Host.Core.GameChanged += _ => bell.Request();
        App.Account.Changed += bell.Request;
        if (Host.Core is ScsKiller k) k.NewsChanged += bell.Request;
        ShowBell();
        if (Updater.Host.Core != null) Updater.Changed += () => Host.Ui.TryEnqueue(ShowUpdate);
        ShowUpdate();
        Navigate(typeof(LibraryPage));
        hwnd = TryGetPlatformHandle()?.Handle ?? 0;   // HWND, or Linux's XID
        ShowCaptionInset();
        if (hwnd == 0) return;
        Accent.Follow(Application.Current!);
        TrackPlacement();
        RestorePlacement(Host.Store);
        Closing += (_, _) => SavePlacement();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsExtendedIntoWindowDecorationsProperty) ShowCaptionInset();
    }

    /// <summary>The title row ends at the caption buttons, so the bell sits flush against them. The screenshots leave their room
    /// too. Under the window manager's own title bar (Linux), and on macOS, whose window buttons are at the left, the row
    /// reaches the window's edge.</summary>
    void ShowCaptionInset()
    {
        if (RightHeader is { } header)   // null while InitializeComponent sets the window's properties
            header.Margin = new(0, 0, IsExtendedIntoWindowDecorations && !OperatingSystem.IsMacOS() || hwnd == 0 && Host.Design != null ? CaptionButtonsWidth : 0, 0);
    }

    /// <summary>Puts the window where it was last closed (window.json in <paramref name="store"/>'s folder): its restored
    /// bounds, and maximized once it shows if it was. Never shows it: a --tray start stays in the notification area.</summary>
    void RestorePlacement(AppStore store)
    {
        placement = store;
        if (store.LoadWindow() is not { } saved || RestoreBounds(saved) is not { } client) return;
        (Width, Height) = client;   // Show sizes the window to these
        if (saved.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Saves where the window is (<see cref="RestorePlacement"/>) while it shows: as it closes, hides, quits,
    /// restarts to update or Windows ends the session. Hidden, it keeps what was saved as it hid.</summary>
    public void SavePlacement()
    {
        if (placement == null || !IsVisible || ReadBounds() is not { } bounds) return;
        try { placement.SaveWindow(bounds); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // the next start opens at the default
    }

    public override void Hide()
    {
        SavePlacement();
        base.Hide();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        bool now = e.NewSize.Width >= ExpandedWidth;
        if (now == wide) return;
        wide = now;
        Nav.DisplayMode = now ? SplitViewDisplayMode.CompactInline : SplitViewDisplayMode.CompactOverlay;
        Nav.IsPaneOpen = now;
    }

    public void ShowGpu()
    {
        GpuName.Text = Host.Core.Vendor.Gpu.Name;
        GpuDriver.Text = (Host.Core as ScsKiller)?.GpuRestartNote ?? "Driver " + Host.Core.Vendor.Gpu.DriverVersion;
    }

    /// <summary>The page shown in the content area. The Library is built once and kept, with its scroll position.</summary>
    public Control? Page => ContentFrame.Content as Control;

    public void Navigate(Type page, object? arg = null)
    {
        history.RemoveRange(at + 1, history.Count - at - 1);
        history.Add((page, arg));
        at = history.Count - 1;
        Show(page, arg);
    }

    void Show(Type page, object? arg)
    {
        var content = page == typeof(LibraryPage) ? cached.TryGetValue(page, out var kept) ? kept : cached[page] = new LibraryPage() : (Control)Activator.CreateInstance(page)!;
        (content as INavigable)?.NavigatedTo(arg);
        ContentFrame.Content = content;
        shown = page;
        // a game's page belongs to Library
        var item = pages.FirstOrDefault(p => p.Page == page).Item ?? LibraryItem;
        foreach (var (b, _) in pages) b.Classes.Set("Selected", b == item);
    }

    /// <summary>The mouse's back and forward buttons, unless a dialog is open.</summary>
    void OnMouseNav(object? _, PointerPressedEventArgs e)
    {
        var kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        if (kind is not (PointerUpdateKind.XButton1Pressed or PointerUpdateKind.XButton2Pressed) || DialogLayer.IsVisible) return;
        // a removed game's page can't be rebuilt
        for (var i = history.Count - 1; i >= 0; i--)
            if (i != at && history[i].Page == typeof(DetailPage) && !Host.Core.Games.Any(g => g.Game.Id == (string)history[i].Arg!))
            {
                history.RemoveAt(i);
                if (i < at) at--;
            }
        var to = at + (kind == PointerUpdateKind.XButton1Pressed ? -1 : 1);
        if (to < 0 || to >= history.Count) return;
        e.Handled = true;
        at = to;
        Show(history[at].Page, history[at].Arg);
    }

    /// <summary>Settings, scrolled to the Patreon card.</summary>
    public void NavigateToPatreon()
    {
        Navigate(typeof(SettingsPage));
        if (ContentFrame.Content is not SettingsPage settings) return;
        settings.Loaded += Scroll;
        void Scroll(object? _, RoutedEventArgs __) { settings.Loaded -= Scroll; settings.ScrollToPatreon(); }
    }

    /// <summary>Back to the Library list from a game's page, at its scroll position.</summary>
    public void ShowLibrary() => Navigate(typeof(LibraryPage));

    void OnNavClick(object? sender, RoutedEventArgs _)
    {
        var page = pages.First(p => p.Item == sender).Page;
        if (page != shown) Navigate(page);
        if (Nav.DisplayMode == SplitViewDisplayMode.CompactOverlay) Nav.IsPaneOpen = false;
    }

    void OnPaneToggle(object? _, RoutedEventArgs __) => Nav.IsPaneOpen = !Nav.IsPaneOpen;

    void ShowUpdate()
    {
        if (Updater.Host.Core == null) return;   // the design data runs no updater
        var ready = Updater.Ready;   // once: a check may replace it meanwhile
        var (visible, enabled, label, tip) = Fmt.RestartToUpdate(ready, Updater.Problem, ready != null && Updater.InstallsAutomatically, Updater.Restarting);
        (UpdateButton.IsVisible, UpdateButton.IsEnabled, UpdateText.Text) = (visible, enabled, label);
        ToolTip.SetTip(UpdateButton, tip);
        AutomationProperties.SetName(UpdateButton, label);
    }

    async void OnRestartToUpdate(object? _, RoutedEventArgs __)
    {
        SavePlacement();   // the restart exits without closing the window
        var restart = Updater.RestartAsync();   // exits once Update.exe has the update
        ShowUpdate();   // Restarting is set before RestartAsync first waits
        await restart;
        ShowUpdate();
    }

    /// <summary>Pages and dialogs at once: null follows the system.</summary>
    public void ApplyTheme(AppTheme? theme) => RequestedThemeVariant = theme switch { AppTheme.Light => ThemeVariant.Light, AppTheme.Dark => ThemeVariant.Dark, _ => ThemeVariant.Default };

    internal static (IReadOnlyList<NewsItem> Shown, IReadOnlySet<string> Unread) Notifications() =>
        NewsState.Of(Host.Store, App.News.For(new(AppVersion.Current, App.Account.Status?.Ent ?? [], Host.Core.Vendor.Vendor, id => Host.Core.Games.Any(g => g.Game.Id == id)),
            DateOnly.FromDateTime(DateTime.Now)));

    internal static HashSet<string> Feed() => App.News.Items.Select(i => i.Id).ToHashSet();

    void ShowBell()
    {
        var unread = Notifications().Unread.Count;
        BellCount.Text = unread.ToString();
        BellBadge.IsVisible = unread > 0;
        AutomationProperties.SetName(BellButton, unread > 0 ? $"Notifications, {unread} unread" : "Notifications");
    }

    public void OpenBell() => BellFlyout.ShowAt(BellButton);

    void OnBellOpening(object? _, EventArgs __)
    {
        var (shownNews, unread) = Notifications();
        FillBell(shownNews, unread);
        NewsState.MarkRead(Host.Store, shownNews.Select(i => i.Id), Feed());
        ShowBell();
    }

    /// <summary>--screenshots: the bell's flyout, open.</summary>
    public Flyout Bell => BellFlyout;
    Flyout BellFlyout => (Flyout)BellButton.Flyout!;
    public Button BellAnchor => BellButton;

    void FillBell(IReadOnlyList<NewsItem> shownNews, IReadOnlySet<string> unread)
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var item in shownNews) list.Children.Add(NewsCard(item, unread.Contains(item.Id)));
        BellFlyout.Content = new StackPanel
        {
            Width = 360, Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Notifications", Classes = { "Section" } },
                shownNews.Count == 0 ? new TextBlock { Text = "Nothing new.", Classes = { "Secondary" } } : new ScrollViewer { MaxHeight = 480, Content = list },
            },
        };
    }

    Control NewsCard(NewsItem item, bool unread)
    {
        var date = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        if (unread)
        {
            var dot = new Ellipse { Width = 8, Height = 8, VerticalAlignment = VerticalAlignment.Center };
            dot.Bind(Shape.FillProperty, dot.GetResourceObservable("AccentFillColorDefaultBrush"));
            date.Children.Add(dot);
        }
        date.Children.Add(new TextBlock { Text = News.Day(item.Date)!.Value.ToString("d"), Classes = { "Caption" } });
        var text = new StackPanel { Spacing = 4, Children = { date } };
        text.Children.Add(new TextBlock { Text = item.Title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        text.Children.Add(new TextBlock { Text = item.Body, Classes = { "Secondary" }, TextWrapping = TextWrapping.Wrap });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new(0, 4, 0, 0) };
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
            Classes = { "Subtle" }, Content = new TextBlock { Classes = { "Icon" }, Text = Glyphs.Dismiss, FontSize = 10 },
            Padding = new(6), VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTip.SetTip(dismiss, "Dismiss");
        AutomationProperties.SetName(dismiss, "Dismiss");
        dismiss.Click += (_, _) =>
        {
            NewsState.Dismiss(Host.Store, item.Id, Feed());
            var (now, _) = Notifications();
            FillBell(now, new HashSet<string>());
            ShowBell();
        };
        var row = new Grid { ColumnSpacing = 8, ColumnDefinitions = new("*,Auto"), Children = { text, dismiss } };
        Grid.SetColumn(dismiss, 1);
        return new Border { Classes = { "Card" }, Padding = new(12), Child = row };
    }

    /// <summary>Null when another dialog is open, so the caller can tell "never shown" from Esc or Close.</summary>
    internal Task<ContentDialogResult>? TryShowDialog(ContentDialog d) => DialogLayer.TryShow(d);

    /// <summary>--screenshots: the open dialog alone, without the smoke behind it.</summary>
    internal DialogLayer Dialogs => DialogLayer;
    internal Border DialogSmoke => DialogLayer.Smoke;
}
