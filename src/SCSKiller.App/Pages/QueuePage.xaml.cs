using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SCSKiller.Core;
using SCSKiller.Core.App;
using Windows.ApplicationModel.DataTransfer;

namespace SCSKiller.App.Pages;

public sealed partial class QueuePage : Page
{
    public QueueVm Vm { get; } = new();

    public QueuePage()
    {
        InitializeComponent();
        var refresh = new Coalesced(DispatcherQueue, Refresh);
        void OnChanged(QueueItem _) => refresh.Request();
        void OnGame(GameState _) => refresh.Request();   // estimates and names come from the games
        Loaded += (_, _) => { App.Core.QueueChanged += OnChanged; App.Core.GameChanged += OnGame; Icons.Failed += refresh.Request; Refresh(); };
        Unloaded += (_, _) => { App.Core.QueueChanged -= OnChanged; App.Core.GameChanged -= OnGame; Icons.Failed -= refresh.Request; };
    }

    static QueueRow RowOf(object sender) => (QueueRow)((FrameworkElement)sender).DataContext;

    // Every action refreshes itself: the Core doesn't raise QueueChanged for every change (e.g. Remove).
    void Act(Action action) { action(); Refresh(); }

    // Sync rebuilds the rows when the run order changes: the selection is kept by game.
    void Refresh()
    {
        var keep = Selected();
        Vm.Refresh();
        foreach (var row in Vm.Waiting)
            if (keep.Contains(row.Id) && !WaitingList.SelectedItems.Contains(row)) WaitingList.SelectedItems.Add(row);
    }

    HashSet<string> Selected() => WaitingList.SelectedItems.Cast<QueueRow>().Select(r => r.Id).ToHashSet();

    /// <summary>Screenshots: a multi-selection.</summary>
    public void Select(params string[] ids)
    {
        foreach (var row in Vm.Waiting.Where(r => ids.Contains(r.Id))) WaitingList.SelectedItems.Add(row);
    }

    void OnSelection(object _, SelectionChangedEventArgs __) => RemoveSelected.IsEnabled = WaitingList.SelectedItems.Count > 0;
    void OnRemoveSelected(object _, RoutedEventArgs __) => RemoveBulk(Selected());
    void OnClearQueue(object _, RoutedEventArgs __) => RemoveBulk(null);
    void RemoveBulk(IReadOnlySet<string>? only) => Act(() => { foreach (var id in ScsKiller.BulkRemovable(App.Core.Queue, only)) App.Core.Remove(id); });

    void OnStart(object _, RoutedEventArgs __) => Act(App.CompileQueue);
    void OnPause(object _, RoutedEventArgs __) => Act(Vm.PauseOrResume);
    void OnStop(object _, RoutedEventArgs __) => Act(App.Core.StopQueue);
    void OnRemove(object sender, RoutedEventArgs _) => Act(() => App.Core.Remove(RowOf(sender).Id));
    void OnRemoveCurrent(object _, RoutedEventArgs __) => Act(() => App.Core.Remove(Vm.Current.Id));
    void OnClearFinished(object _, RoutedEventArgs __) => Act(() => { foreach (var r in Vm.Finished.ToList()) App.Core.Remove(r.Id); });
    void OnTop(object sender, RoutedEventArgs _) => Act(() => App.Core.MoveInQueue(RowOf(sender).Id, 0));
    void OnUp(object sender, RoutedEventArgs _) => MoveBy(RowOf(sender), -1);
    void OnDown(object sender, RoutedEventArgs _) => MoveBy(RowOf(sender), 1);
    void MoveBy(QueueRow row, int delta) => Act(() => App.Core.MoveInQueue(row.Id, Vm.Waiting.IndexOf(row) + delta));

    // Drag-and-drop: the ListView has already moved the rows in Waiting (several when several are selected); the Core
    // takes the whole order, top down, so each move leaves the rows above it in place.
    void OnDragStarting(object _, DragItemsStartingEventArgs __) => Vm.Dragging = true;
    void OnDragCompleted(ListViewBase _, DragItemsCompletedEventArgs e)
    {
        Vm.Dragging = false;
        Act(() =>
        {
            if (e.DropResult != DataPackageOperation.Move) return;
            var order = Vm.Waiting.ToList();
            for (int i = 0; i < order.Count; i++) App.Core.MoveInQueue(order[i].Id, i);
        });
    }

    void OnGameClick(object _, ItemClickEventArgs e)
    {
        var id = ((QueueRow)e.ClickedItem).Id;
        if (App.Core.Games.Any(g => g.Game.Id == id)) App.Main.Navigate(typeof(DetailPage), id);   // a finished game may be removed since
    }

    void OnSettings(object _, RoutedEventArgs __) => App.Main.Navigate(typeof(SettingsPage));
    void OnLibrary(object _, RoutedEventArgs __) => App.Main.Navigate(typeof(LibraryPage));
}
