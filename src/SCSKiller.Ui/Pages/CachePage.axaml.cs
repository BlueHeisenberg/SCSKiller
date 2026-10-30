using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

public sealed partial class CachePage : UserControl
{
    public CacheVm Vm { get; } = new();
    readonly Coalesced games;
    readonly Coalesced icons;

    public CachePage()
    {
        InitializeComponent();
        DataContext = Vm;
        games = new(Host.Ui, Vm.GamesChanged);
        icons = new(Host.Ui, Vm.IconsChanged);
    }

    void OnGame(GameState _) => games.Request();

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Host.Core.GameChanged += OnGame;
        Host.Icons.Changed += icons.Request;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        Host.Core.GameChanged -= OnGame;
        Host.Icons.Changed -= icons.Request;
    }

    /// <summary>--screenshots: the lower groups.</summary>
    public void ScrollToEnd() => Scroller.ScrollToEnd();
    public void ScrollToTop() => Scroller.ScrollToHome();

    void OnRefresh(object? _, RoutedEventArgs __) => Vm.Refresh();

    void OnOpenFolder(object? sender, RoutedEventArgs _)
    {
        if (sender is not Control c) return;
        if (c.Tag is string dir) Open(dir);
        else if (c.DataContext is CacheItemRow { Folders: [var only] }) Open(only);
        else if (c.DataContext is CacheItemRow r)
        {
            var menu = new MenuFlyout();
            foreach (var d in r.Folders)
            {
                var item = new MenuItem { Header = d };
                item.Click += (_, _) => Open(d);
                menu.Items.Add(item);
            }
            menu.ShowAt(c);
        }
    }

    static void Open(string dir)
    {
        if (dir.Length > 0 && Directory.Exists(dir)) Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true })?.Dispose();
    }

    void OnDeleteItem(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CacheItemRow r) _ = Confirm($"Delete {r.Name} ({r.Size})?", r.Kind, [r.Item]);
    }

    void OnDeleteGroup(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is CacheGroupRow g) _ = Confirm($"Delete {g.Title} ({g.Size})?", g.Group.Kind, g.Group.Items);
    }

    void OnFreeOldDriver(object? _, RoutedEventArgs __) =>
        _ = Confirm($"{Vm.FreeText} the new driver no longer uses?", CacheGroupKind.OldDriver, Vm.OldDriverItems, "Free");

    async Task Confirm(string title, CacheGroupKind kind, IReadOnlyList<CacheItem> items, string action = "Delete")
    {
        var text = new TextBlock { Text = CacheVm.Consequence(kind, items), TextWrapping = TextWrapping.Wrap };
        if (await App.ShowAsync(App.Confirm(title, text, action)) == ContentDialogResult.Primary) await Vm.DeleteAsync(items);
    }

    async void OnClearEverything(object? _, RoutedEventArgs __)
    {
        if (Vm.Inventory is not { } inv) return;
        var (dialog, others) = ClearEverythingDialog(inv);
        if (await App.ShowAsync(dialog) == ContentDialogResult.Primary) await Vm.DeleteAsync(inv.Everything(others.IsChecked == true));
    }

    /// <summary>Also --screenshots: the dialog as it opens.</summary>
    public static (ContentDialog Dialog, CheckBox Others) ClearEverythingDialog(CacheInventory inv)
    {
        var warning = new TextBlock { Text = ScsKiller.EverythingWarning(inv, false), TextWrapping = TextWrapping.Wrap };
        var others = new CheckBox { Content = $"Also delete {ScsKiller.OtherApps.ToLowerInvariant()} ({Format.Bytes(inv.Bytes(CacheGroupKind.Unattributed))})" };
        others.IsCheckedChanged += (_, _) => warning.Text = ScsKiller.EverythingWarning(inv, others.IsChecked == true);
        var content = new StackPanel { Spacing = 12, Children = { warning, others, new TextBlock { Text = "Files in use are skipped.", TextWrapping = TextWrapping.Wrap } } };
        return (App.Confirm("Clear every shader cache?", content, "Clear everything"), others);
    }
}
