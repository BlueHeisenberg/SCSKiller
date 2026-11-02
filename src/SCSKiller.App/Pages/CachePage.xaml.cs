using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SCSKiller.Core.App;

namespace SCSKiller.App.Pages;

public sealed partial class CachePage : Page
{
    public CacheVm Vm { get; } = new();

    public CachePage()
    {
        InitializeComponent();
        var games = new Coalesced(DispatcherQueue, Vm.GamesChanged);
        void OnGame(Core.GameState _) => games.Request();
        var icons = new Coalesced(DispatcherQueue, Vm.IconsChanged);
        void OnIconFailed() => icons.Request();
        Loaded += (_, _) => { App.Core.GameChanged += OnGame; Icons.Failed += OnIconFailed; };
        Unloaded += (_, _) => { App.Core.GameChanged -= OnGame; Icons.Failed -= OnIconFailed; };
    }

    /// <summary>--screenshots: the lower groups.</summary>
    public void ScrollToEnd() => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);
    public void ScrollToTop() => Scroller.ChangeView(null, 0, null, true);

    void OnRefresh(object _, RoutedEventArgs __) => Vm.Refresh();

    void OnOpenFolder(object sender, RoutedEventArgs _)
    {
        switch ((sender as FrameworkElement)?.Tag)
        {
            case string dir: Open(dir); break;
            case CacheItemRow { Folders: [var dir] }: Open(dir); break;
            case CacheItemRow r:
                var menu = new MenuFlyout();
                foreach (var dir in r.Folders)
                {
                    var item = new MenuFlyoutItem { Text = dir };
                    item.Click += (_, _) => Open(dir);
                    menu.Items.Add(item);
                }
                menu.ShowAt((FrameworkElement)sender);
                break;
        }
    }

    static void Open(string dir)
    {
        if (dir.Length > 0 && Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
    }

    void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CacheItemRow r) _ = Confirm(Loc.T("Cache_DeleteOne", r.Name, r.Size), r.Kind, [r.Item], Loc.T("Cache_Delete"));
    }

    void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CacheGroupRow g) _ = Confirm(Loc.T("Cache_DeleteGroup", g.Title, g.Size), g.Group.Kind, g.Group.Items, Loc.T("Cache_Delete"));
    }

    void OnFreeOldDriver(object _, RoutedEventArgs __) =>
        _ = Confirm(Loc.T("Cache_FreeOldDriver", Vm.FreeText), CacheGroupKind.OldDriver, Vm.OldDriverItems, Loc.T("Cache_Free"));

    async Task Confirm(string title, CacheGroupKind kind, IReadOnlyList<CacheItem> items, string action)
    {
        var text = new TextBlock { Text = CacheVm.Consequence(kind, items), TextWrapping = TextWrapping.Wrap };
        if (await App.ShowAsync(App.Confirm(this, title, text, action)) == ContentDialogResult.Primary) await Vm.DeleteAsync(items);
    }

    async void OnClearEverything(object _, RoutedEventArgs __)
    {
        if (Vm.Inventory is not { } inv) return;
        var (dialog, others) = ClearEverythingDialog(inv);
        if (await App.ShowAsync(dialog) == ContentDialogResult.Primary) await Vm.DeleteAsync(inv.Everything(others.IsChecked == true));
    }

    /// <summary>Also --screenshots: the dialog as it opens.</summary>
    public (ContentDialog Dialog, CheckBox Others) ClearEverythingDialog(CacheInventory inv)
    {
        var warning = new TextBlock { Text = ScsKiller.EverythingWarning(inv, false), TextWrapping = TextWrapping.Wrap };
        var others = new CheckBox { Content = Loc.T("Cache_AlsoDeleteOthers", Loc.T("Cache_OtherAppsLower"), Core.App.Format.Bytes(inv.Bytes(CacheGroupKind.Unattributed))) };
        others.Checked += (_, _) => warning.Text = ScsKiller.EverythingWarning(inv, true);
        others.Unchecked += (_, _) => warning.Text = ScsKiller.EverythingWarning(inv, false);
        var content = new StackPanel { Spacing = 12, Children = { warning, others, new TextBlock { Text = Loc.T("Cache_FilesInUseSkipped"), TextWrapping = TextWrapping.Wrap } } };
        return (App.Confirm(this, Loc.T("Cache_ClearEveryTitle"), content, Loc.T("Cache_ClearEverything")), others);
    }
}
