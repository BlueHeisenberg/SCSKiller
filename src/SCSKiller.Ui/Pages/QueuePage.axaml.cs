using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

public sealed partial class QueuePage : UserControl
{
    public QueueVm Vm { get; } = new();
    readonly Coalesced refresh;

    public QueuePage()
    {
        InitializeComponent();
        DataContext = Vm;
        refresh = new(Host.Ui, Refresh);
        WaitingList.AddHandler(PointerPressedEvent, OnRowPressed, RoutingStrategies.Tunnel);
        WaitingList.AddHandler(PointerMovedEvent, OnRowMoved, RoutingStrategies.Tunnel);
        WaitingList.AddHandler(PointerReleasedEvent, OnRowReleased, RoutingStrategies.Tunnel);
        WaitingList.AddHandler(PointerCaptureLostEvent, (_, e) => { if (e.Source == WaitingList) EndDrag(drop: false); });
    }

    void OnChanged(QueueItem _) => refresh.Request();
    void OnGame(GameState _) => refresh.Request();   // estimates and names come from the games

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Host.Core.QueueChanged += OnChanged; Host.Core.GameChanged += OnGame; Host.Icons.Changed += refresh.Request;
        if (Host.Core is ScsKiller k) k.ScanProgressed += refresh.Request;   // a fold's share (Folds)
        Refresh();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        Host.Core.QueueChanged -= OnChanged; Host.Core.GameChanged -= OnGame; Host.Icons.Changed -= refresh.Request;
        if (Host.Core is ScsKiller k) k.ScanProgressed -= refresh.Request;
    }

    static QueueRow RowOf(object? sender) => (QueueRow)((Control)sender!).DataContext!;

    // Every action refreshes itself: the Core doesn't raise QueueChanged for every change (e.g. Remove).
    void Act(Action action) { action(); Refresh(); }

    // Sync rebuilds the rows when the run order changes: the selection is kept by game.
    void Refresh()
    {
        var keep = Selected();
        Vm.Refresh();
        foreach (var row in Vm.Waiting)
            if (keep.Contains(row.Id) && !WaitingList.SelectedItems!.Contains(row)) WaitingList.SelectedItems.Add(row);
    }

    HashSet<string> Selected() => WaitingList.SelectedItems!.Cast<QueueRow>().Select(r => r.Id).ToHashSet();

    /// <summary>Screenshots: a multi-selection.</summary>
    public void Select(params string[] ids)
    {
        foreach (var row in Vm.Waiting.Where(r => ids.Contains(r.Id))) WaitingList.SelectedItems!.Add(row);
    }

    void OnSelection(object? _, SelectionChangedEventArgs __) => RemoveSelected.IsEnabled = WaitingList.SelectedItems!.Count > 0;
    void OnRemoveSelected(object? _, RoutedEventArgs __) => RemoveBulk(Selected());
    void OnClearQueue(object? _, RoutedEventArgs __) => RemoveBulk(null);
    void RemoveBulk(IReadOnlySet<string>? only) => Act(() => { foreach (var id in ScsKiller.BulkRemovable(Host.Core.Queue, only)) Host.Core.Remove(id); });

    void OnStart(object? _, RoutedEventArgs __) => Act(App.CompileQueue);
    void OnPause(object? _, RoutedEventArgs __) => Act(Vm.PauseOrResume);
    void OnStop(object? _, RoutedEventArgs __) => Act(Host.Core.StopQueue);
    void OnRemove(object? sender, RoutedEventArgs e) { e.Handled = true; Act(() => Host.Core.Remove(RowOf(sender).Id)); }
    void OnRemoveCurrent(object? _, RoutedEventArgs __) => Act(() => Host.Core.Remove(Vm.Current.Id));
    void OnClearFinished(object? _, RoutedEventArgs __) => Act(() => { foreach (var r in Vm.Finished.ToList()) Host.Core.Remove(r.Id); });
    void OnTop(object? sender, RoutedEventArgs _) => Act(() => Host.Core.MoveInQueue(RowOf(sender).Id, 0));
    void OnBottom(object? sender, RoutedEventArgs _) => Act(() => Host.Core.MoveInQueue(RowOf(sender).Id, int.MaxValue));
    void OnUp(object? sender, RoutedEventArgs _) => MoveBy(RowOf(sender), -1);
    void OnDown(object? sender, RoutedEventArgs _) => MoveBy(RowOf(sender), 1);
    void MoveBy(QueueRow row, int delta) => Act(() => Host.Core.MoveInQueue(row.Id, Vm.Waiting.IndexOf(row) + delta));

    void OnGameClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source != sender) return;   // the row's own Remove
        var id = RowOf(sender).Id;
        if (Host.Core.Games.Any(g => g.Game.Id == id)) App.Main.Navigate(typeof(DetailPage), id);   // a finished game may be removed since
    }

    void OnSettings(object? _, RoutedEventArgs __) => App.Main.Navigate(typeof(SettingsPage));
    void OnLibrary(object? _, RoutedEventArgs __) => App.Main.Navigate(typeof(LibraryPage));

    // Drag to reorder: the rows dragged (the selection, when the row pressed is in it) go where the line shows, and the
    // Core takes the whole order, top down, so each move leaves the rows above it in place.
    const double DragThreshold = 4;
    (ListBoxItem Item, Point At, bool SelectOnRelease)? press;
    HashSet<string>? dragged;
    int gap;

    void OnRowPressed(object? _, PointerPressedEventArgs e)
    {
        press = null;
        if (!e.GetCurrentPoint(WaitingList).Properties.IsLeftButtonPressed || (e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } item) return;
        // a selected row keeps the selection until the release, so several rows can be dragged
        bool keep = item.IsSelected && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) == 0;
        if (keep) { e.Handled = true; item.Focus(NavigationMethod.Pointer); }
        press = (item, e.GetPosition(WaitingList), keep);
    }

    void OnRowMoved(object? _, PointerEventArgs e)
    {
        if (press is not { } p) return;
        var at = e.GetPosition(WaitingList);
        if (dragged == null)
        {
            if (!e.GetCurrentPoint(WaitingList).Properties.IsLeftButtonPressed) { press = null; return; }
            if (Math.Abs(at.X - p.At.X) < DragThreshold && Math.Abs(at.Y - p.At.Y) < DragThreshold) return;
            var row = (QueueRow)p.Item.DataContext!;
            var selected = Selected();
            dragged = selected.Contains(row.Id) ? selected : [row.Id];
            Vm.Dragging = true;
            e.Pointer.Capture(WaitingList);
        }
        ShowGap(at.Y);
        e.Handled = true;
    }

    void OnRowReleased(object? _, PointerReleasedEventArgs e)
    {
        if (dragged != null)
        {
            e.Handled = true;
            EndDrag(drop: true);
            e.Pointer.Capture(null);
        }
        else if (press is { SelectOnRelease: true } p)
        {
            WaitingList.SelectedItems!.Clear();
            WaitingList.SelectedItems.Add(p.Item.DataContext);
        }
        press = null;
    }

    void ShowGap(double y)
    {
        var rows = WaitingList.GetRealizedContainers().OfType<ListBoxItem>().OrderBy(WaitingList.IndexFromContainer).ToList();
        gap = rows.Count;
        double lineY = rows.Count > 0 ? rows[^1].Bounds.Bottom : 0;
        for (int i = 0; i < rows.Count; i++)
            if (y < rows[i].Bounds.Top + rows[i].Bounds.Height / 2) { (gap, lineY) = (i, rows[i].Bounds.Top); break; }
        DropLine.Margin = new(16, Math.Max(0, lineY - 1), 16, 0);
        DropLine.IsVisible = true;
    }

    void EndDrag(bool drop)
    {
        if (dragged is not { } moved) return;
        dragged = null;
        press = null;
        DropLine.IsVisible = false;
        Vm.Dragging = false;
        Act(() =>
        {
            if (!drop) return;
            var order = Vm.Waiting.Select(r => r.Id).ToList();
            var now = QueueVm.Dropped(order, moved, gap);
            if (now.SequenceEqual(order)) return;
            for (int i = 0; i < now.Count; i++) Host.Core.MoveInQueue(now[i], i);
        });
    }
}
