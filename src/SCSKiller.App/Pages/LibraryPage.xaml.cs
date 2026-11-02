using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.App.Pages;

public sealed partial class LibraryPage : Page
{
    public LibraryVm Vm { get; } = new();

    public LibraryPage()
    {
        InitializeComponent();
        ((CollectionViewSource)Resources["GroupedGames"]).Source = Vm.Groups;
        var refresh = new Coalesced(DispatcherQueue, Vm.Refresh);
        var step = new Coalesced(DispatcherQueue, Vm.ScanStep);
        void OnChanged(GameState _) => refresh.Request();
        void OnQueue(QueueItem q) { if (!Format.ProgressTick(q)) refresh.Request(); }   // progress ticks change nothing here
        Loaded += (_, _) =>
        {
            App.Core.GameChanged += OnChanged; App.Core.QueueChanged += OnQueue; Icons.Failed += refresh.Request;
            if (App.Core is ScsKiller k) k.ScanProgressed += step.Request;
            Vm.Load();
        };
        Unloaded += (_, _) =>
        {
            App.Core.GameChanged -= OnChanged; App.Core.QueueChanged -= OnQueue; Icons.Failed -= refresh.Request;
            if (App.Core is ScsKiller k) k.ScanProgressed -= step.Request;
        };
    }

    void OnSearch(AutoSuggestBox box, AutoSuggestBoxTextChangedEventArgs _) => Vm.Filter = box.Text;

    void OnSearchKey(object _, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Escape || Search.Text.Length == 0) return;
        Search.Text = "";
        e.Handled = true;
    }

    /// <summary>--screenshots: a search typed, and the list scrolled to its last section.</summary>
    public string SearchText { set => Search.Text = value; }
    public void ScrollToEnd() { if (Vm.Games.Count > 0) List.ScrollIntoView(Vm.Groups[^1][^1]); }
    public void ScrollTo(string id) { if (Vm.Games.FirstOrDefault(r => r.Id == id) is { } row) List.ScrollIntoView(row, ScrollIntoViewAlignment.Leading); }

    static GameRow RowOf(object sender) => (GameRow)((FrameworkElement)sender).DataContext;

    void OnGameClick(object _, ItemClickEventArgs e) => App.Main.Navigate(typeof(DetailPage), ((GameRow)e.ClickedItem).Id);
    void OnDetails(object sender, RoutedEventArgs _) => App.Main.Navigate(typeof(DetailPage), RowOf(sender).Id);
    void OnSettings(object _, RoutedEventArgs __) => App.Main.Navigate(typeof(SettingsPage));
    void OnCache(object _, RoutedEventArgs __) => App.Main.Navigate(typeof(CachePage));
    void OnPlay(object sender, RoutedEventArgs _) => Vm.Play(RowOf(sender));
    void OnShowGame(object sender, RoutedEventArgs _) => Vm.ShowInLibrary((HiddenRow)((FrameworkElement)sender).DataContext);

    // Either click fetches the server's data whatever its age; Shift+click also forces re-detection, which is otherwise
    // redone only when an exe, build or SCSKiller changes.
    void OnRefresh(object _, RoutedEventArgs __) =>
        Vm.Rescan(force: Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down), userRequested: true);

    // Adding only queues the game: nothing compiles until Compile queue.
    void OnAdd(object sender, RoutedEventArgs _)
    {
        App.Core.Enqueue(RowOf(sender).Id);
        Vm.Refresh();
    }

    void OnDismissGpuNotice(InfoBar _, object __) => Vm.DismissGpuNotice();

    void OnSortName(object _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.NameAscending, LibrarySort.NameDescending);
    void OnSortPipelines(object _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.PipelinesDescending, LibrarySort.PipelinesAscending);
    void OnSortCache(object _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.CacheDescending, LibrarySort.CacheAscending);
    void OnSortStatus(object _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.Status, LibrarySort.Status);

    // Built as it opens: the folders may have changed since.
    void OnFoldersOpening(object _, object __)
    {
        FoldersMenu.Items.Clear();
        var add = new MenuFlyoutItem { Text = Loc.T("Library_AddFolderMenu"), Icon = new FontIcon { Glyph = "\uE710" } };
        add.Click += OnAddFolder;
        FoldersMenu.Items.Add(add);
        var leave = new MenuFlyoutItem { Text = Loc.T("Library_LeaveOutFolderMenu"), Icon = new FontIcon { Glyph = "" } };
        ToolTipService.SetToolTip(leave, Loc.T("Library_LeaveOutTooltip"));
        leave.Click += OnLeaveOutFolder;
        FoldersMenu.Items.Add(leave);
        var folders = Fmt.Added?.Folders ?? [];
        if (folders.Count > 0) FoldersMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var folder in folders)
        {
            var item = new MenuFlyoutItem { Text = folder, Icon = new FontIcon { Glyph = "\uE711" } };
            ToolTipService.SetToolTip(item, Loc.T("Library_StopLookingTooltip"));
            item.Click += async (_, _) =>
            {
                if (!await App.ConfirmAsync(this, Loc.T("Library_StopLookingConfirm"),
                        Loc.T("Library_StopLookingBody", folder), Loc.T("Library_StopLooking"))) return;
                if (Fmt.Added is { } added) await Task.Run(() => added.RemoveFolder(folder));
                Vm.Rescan(force: false);
            };
            FoldersMenu.Items.Add(item);
        }
        var leftOut = App.Core.Settings.LeftOutFolders ?? [];
        if (leftOut.Count > 0) FoldersMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var folder in leftOut)
        {
            var item = new MenuFlyoutItem { Text = Loc.T("Library_LeftOutPrefix", folder), Icon = new FontIcon { Glyph = "" } };
            ToolTipService.SetToolTip(item, Loc.T("Library_LookAgainTooltip"));
            item.Click += async (_, _) =>
            {
                await Task.Run(() => App.Core.ScanFolderAgain(folder));
                Vm.Rescan(force: false);
            };
            FoldersMenu.Items.Add(item);
        }
    }

    async void OnLeaveOutFolder(object _, RoutedEventArgs __)
    {
        string? path;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Main.AppWindow.Id) { CommitButtonText = Loc.T("Library_LeaveOutPicker") };
            path = (await picker.PickSingleFolderAsync())?.Path;
        }
        catch (Exception ex) { await Message(Loc.T("Common_CouldntOpenFolderPicker"), ex.Message); return; }
        if (path == null) return;
        try { await Task.Run(() => App.Core.LeaveOutFolder(path)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Message(Loc.T("Library_CouldntLeaveOut"), ex.Message);
            return;
        }
        Vm.Rescan(force: false);
    }

    // Each folder in it is taken for one game, its exe found as for a store's install
    async void OnAddFolder(object _, RoutedEventArgs __)
    {
        string? path;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Main.AppWindow.Id) { CommitButtonText = Loc.T("Library_LookHerePicker") };
            path = (await picker.PickSingleFolderAsync())?.Path;
        }
        catch (Exception ex) { await Message(Loc.T("Common_CouldntOpenFolderPicker"), ex.Message); return; }
        if (path == null || Fmt.Added is not { } added) return;
        try { await Task.Run(() => added.AddFolder(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await Message(Loc.T("Library_CouldntAddFolder"), ex.Message);
            return;
        }
        Vm.Rescan(force: false);
    }
    // A launcher stub is resolved to the game's own exe, shown with the game folder to confirm; an exe of a listed game
    // opens that game instead.
    async void OnAddGame(object _, RoutedEventArgs __)
    {
        string? path;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(App.Main.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder, CommitButtonText = Loc.T("Library_AddGamePicker"),
            };
            picker.FileTypeFilter.Add(".exe");
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception ex) { await Message(Loc.T("Common_CouldntOpenFilePicker"), ex.Message); return; }
        if (path == null) return;
        ManualAdd added;
        try { added = await Task.Run(() => App.Core.PreviewManualGame(path)); }   // reads the install's files
        catch (Exception ex)   // a rejection's message, or a data folder SCSKiller couldn't read
        {
            await Message(Loc.T("Library_CouldntAddGame"), ex.Message);
            return;
        }
        if (!added.Existed)
        {
            if (await GameFolderDialog.ShowAsync(XamlRoot, added.Game, Loc.T("Library_AddGameTitle", added.Game.Name), Loc.T("Library_AddGamePicker")) is not { } folder) return;
            try { await Task.Run(() => App.Core.AddManualGame(path, folder)); }
            catch (Exception ex)
            {
                await Message(Loc.T("Library_CouldntAddGame"), ex.Message);
                return;
            }
            Vm.Rescan(force: false);   // lists it under "Added by you" once its engine and anti-cheat are checked
            return;
        }
        if (App.Core.HiddenGames.Any(g => g.Id == added.Game.Id))
        {
            await Message(Loc.T("Library_HiddenTitle"), Loc.T("Library_HiddenBody", added.Game.Name));
            return;
        }
        var listed = App.Core.Games.Any(s => s.Game.Id == added.Game.Id);
        var where = added.Game.Store == Store.Manual ? Loc.T("Library_WhereManual") : Loc.T("Library_WhereFound", Fmt.StoreName(added.Game));
        if (listed && await App.ConfirmAsync(this, Loc.T("Library_AlreadyTitle"), Loc.T("Library_AlreadyInList", added.Game.Name, where), Loc.T("Common_Open"), ContentDialogButton.Primary))
            App.Main.Navigate(typeof(DetailPage), added.Game.Id);
        else if (!listed) await Message(Loc.T("Library_AlreadyTitle"), Loc.T("Library_AlreadyBody", added.Game.Name, where));
    }

    Task Message(string title, string text) => App.ShowAsync(new ContentDialog
    {
        XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = Loc.T("Common_Ok"),
    });

    void OnAddAll(object _, RoutedEventArgs __) => Vm.AddAllReady();
    void OnAddRecommended(object _, RoutedEventArgs __) => Vm.AddAllRecommended();

    void OnCompileQueue(object _, RoutedEventArgs __)
    {
        App.CompileQueue();
        App.Main.Navigate(typeof(QueuePage));
    }

    // Unsupported: say why; an encrypted game can be unlocked with its AES key (stored on this PC only).
    async void OnWhy(object sender, RoutedEventArgs _)
    {
        var row = RowOf(sender);
        var title = Loc.T("Library_WhyNotTitle", row.Name);
        if (!ScsKiller.NeedsAesKey(row.State))
            await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = row.Reason, TextWrapping = TextWrapping.Wrap }, CloseButtonText = Loc.T("Common_Close") });
        else if (await AesKeyDialog.ShowAsync(XamlRoot, row.Id, title, row.Reason)) Vm.Rescan();
    }

    async void OnRecord(object sender, RoutedEventArgs _)
    {
        var row = RowOf(sender);
        if (!await App.ConfirmAsync(this, Loc.T("Library_RecordTitle", row.Name),
                Loc.T("Library_RecordBody", Fmt.RecorderFiles(row.State)),
                Loc.T("Library_AddRecorder"), ContentDialogButton.Primary)) return;
        try { await Task.Run(() => App.Core.InstallRecorder(row.Id)); }   // file IO and the game's re-evaluation
        catch (Exception ex) { await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = Loc.T("Library_CouldntAddRecorder"), Content = ex.Message, CloseButtonText = Loc.T("Common_Ok") }); }
        Vm.Refresh();
    }
}
