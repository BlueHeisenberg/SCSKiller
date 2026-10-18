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
        Loaded += (_, _) => App.Core.GameChanged += OnGame;
        Unloaded += (_, _) => App.Core.GameChanged -= OnGame;
    }

    /// <summary>--screenshots: the lower groups.</summary>
    public void ScrollToEnd() => Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);
    public void ScrollToTop() => Scroller.ChangeView(null, 0, null, true);

    void OnRefresh(object _, RoutedEventArgs __) => Vm.Refresh();

    void OnOpenFolder(object sender, RoutedEventArgs _)
    {
        if ((sender as FrameworkElement)?.Tag is string { Length: > 0 } dir && Directory.Exists(dir))
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
    }

    void OnDeleteItem(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CacheItemRow r) _ = Confirm($"Delete {r.Name} ({r.Size})?", r.Kind, [r.Item]);
    }

    void OnDeleteGroup(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CacheGroupRow g) _ = Confirm($"Delete {g.Title} ({g.Size})?", g.Group.Kind, g.Group.Items);
    }

    void OnFreeOldDriver(object _, RoutedEventArgs __) =>
        _ = Confirm($"{Vm.FreeText} the new driver no longer uses?", CacheGroupKind.OldDriver, Vm.OldDriverItems, "Free");

    async Task Confirm(string title, CacheGroupKind kind, IReadOnlyList<CacheItem> items, string action = "Delete")
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
        var others = new CheckBox { Content = $"Also delete {ScsKiller.OtherApps.ToLowerInvariant()} ({Core.App.Format.Bytes(inv.Bytes(CacheGroupKind.Unattributed))})" };
        others.Checked += (_, _) => warning.Text = ScsKiller.EverythingWarning(inv, true);
        others.Unchecked += (_, _) => warning.Text = ScsKiller.EverythingWarning(inv, false);
        var content = new StackPanel { Spacing = 12, Children = { warning, others, new TextBlock { Text = "Files in use are skipped.", TextWrapping = TextWrapping.Wrap } } };
        return (App.Confirm(this, "Clear every shader cache?", content, "Clear everything"), others);
    }
}
