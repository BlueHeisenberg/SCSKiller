using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SCSKiller.Core;
using Windows.ApplicationModel.DataTransfer;

namespace SCSKiller.App.Pages;

public sealed partial class QueuePage : Page
{
    public QueueVm Vm { get; } = new();

    public QueuePage()
    {
        InitializeComponent();
        PageTitle.Text = I18n.T("QueueTitle");
        BtnStartQueueText.Text = I18n.T("QueueCompileQueueBtn");
        BtnStop.Content = I18n.T("QueueStop");
        ToolTipService.SetToolTip(BtnStop, I18n.T("QueueStopTip"));
        LnkRaiseLimit.Content = I18n.T("QueueRaiseLimit");
        ColGame.Text = I18n.T("QueueColGame");
        ColTime.Text = I18n.T("QueueColTime");
        ColCache.Text = I18n.T("QueueColCache");
        EmptyTitle.Text = I18n.T("QueueEmptyTitle");
        EmptySub.Text = I18n.T("QueueEmptySub");
        LnkOpenLibrary.Content = I18n.T("QueueOpenLibrary");
        TxtFinishedTitle.Text = I18n.T("QueueFinishedTitle");
        LnkClearFinished.Content = I18n.T("QueueClearFinished");
        TxtCompileThreads.Text = I18n.T("QueueCompileThreads");
        RadioPriority.Header = I18n.T("QueuePriorityHeader");
        RadioBelowNormal.Content = I18n.T("QueuePriorityBelowNormal");
        RadioIdle.Content = I18n.T("QueuePriorityIdle");
        var refresh = new Coalesced(DispatcherQueue, Vm.Refresh);
        void OnChanged(QueueItem _) => refresh.Request();
        void OnGame(GameState _) => refresh.Request();   // estimates and names come from the games
        Loaded += (_, _) => { App.Core.QueueChanged += OnChanged; App.Core.GameChanged += OnGame; Icons.Failed += refresh.Request; Vm.Refresh(); };
        Unloaded += (_, _) => { App.Core.QueueChanged -= OnChanged; App.Core.GameChanged -= OnGame; Icons.Failed -= refresh.Request; };
    }

    static QueueRow RowOf(object sender) => (QueueRow)((FrameworkElement)sender).DataContext;

    // Every action refreshes itself: the Core doesn't raise QueueChanged for every change (e.g. Remove).
    void Act(Action action) { action(); Vm.Refresh(); }

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

    // Drag-and-drop: the ListView has already moved the row in Waiting; tell the Core where it landed.
    void OnDragStarting(object _, DragItemsStartingEventArgs __) => Vm.Dragging = true;
    void OnDragCompleted(ListViewBase _, DragItemsCompletedEventArgs e)
    {
        Vm.Dragging = false;
        Act(() =>
        {
            if (e.DropResult == DataPackageOperation.Move && e.Items.FirstOrDefault() is QueueRow row)
                App.Core.MoveInQueue(row.Id, Vm.Waiting.IndexOf(row));
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
