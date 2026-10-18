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
        void OnQueue(QueueItem q) { if (q.Stage != QueueStage.Warming) refresh.Request(); }   // progress ticks change nothing here
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
        var add = new MenuFlyoutItem { Text = "Add a folder of games…", Icon = new FontIcon { Glyph = "\uE710" } };
        add.Click += OnAddFolder;
        FoldersMenu.Items.Add(add);
        var folders = Fmt.Added?.Folders ?? [];
        if (folders.Count > 0) FoldersMenu.Items.Add(new MenuFlyoutSeparator());
        foreach (var folder in folders)
        {
            var item = new MenuFlyoutItem { Text = folder, Icon = new FontIcon { Glyph = "\uE711" } };
            ToolTipService.SetToolTip(item, "Stop looking for games here");
            item.Click += async (_, _) =>
            {
                if (!await App.ConfirmAsync(this, "Stop looking for games here?",
                        $"{folder}\n\nThe games SCSKiller found only in this folder leave the library. Nothing in the folder changes.", "Stop looking")) return;
                if (Fmt.Added is { } added) await Task.Run(() => added.RemoveFolder(folder));
                Vm.Rescan(force: false);
            };
            FoldersMenu.Items.Add(item);
        }
    }

    // Each folder in it is taken for one game, its exe found as for a store's install
    async void OnAddFolder(object _, RoutedEventArgs __)
    {
        string? path;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Main.AppWindow.Id) { CommitButtonText = "Look for games here" };
            path = (await picker.PickSingleFolderAsync())?.Path;
        }
        catch (Exception ex) { await Message("Couldn't open the folder picker", ex.Message); return; }
        if (path == null || Fmt.Added is not { } added) return;
        try { await Task.Run(() => added.AddFolder(path)); }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            await Message("Couldn't add this folder", ex.Message);
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
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.ComputerFolder, CommitButtonText = "Add game",
            };
            picker.FileTypeFilter.Add(".exe");
            path = (await picker.PickSingleFileAsync())?.Path;
        }
        catch (Exception ex) { await Message("Couldn't open the file picker", ex.Message); return; }
        if (path == null) return;
        ManualAdd added;
        try { added = await Task.Run(() => App.Core.PreviewManualGame(path)); }   // reads the install's files
        catch (Exception ex)   // a rejection's message, or a data folder SCSKiller couldn't read
        {
            await Message("Couldn't add this game", ex.Message);
            return;
        }
        if (!added.Existed)
        {
            if (await GameFolderDialog.ShowAsync(XamlRoot, added.Game, $"Add {added.Game.Name}", "Add game") is not { } folder) return;
            try { await Task.Run(() => App.Core.AddManualGame(path, folder)); }
            catch (Exception ex)
            {
                await Message("Couldn't add this game", ex.Message);
                return;
            }
            Vm.Rescan(force: false);   // lists it under "Added by you" once its engine and anti-cheat are checked
            return;
        }
        if (App.Core.HiddenGames.Any(g => g.Id == added.Game.Id))
        {
            await Message("Hidden from your library", $"You removed {added.Game.Name}. Show hidden brings it back.");
            return;
        }
        var listed = App.Core.Games.Any(s => s.Game.Id == added.Game.Id);
        var where = added.Game.Store == Store.Manual ? "you added it already" : $"SCSKiller found it in {Fmt.StoreName(added.Game)}";
        if (listed && await App.ConfirmAsync(this, "Already in your library", $"{added.Game.Name} is in the list: {where}.", "Open", ContentDialogButton.Primary))
            App.Main.Navigate(typeof(DetailPage), added.Game.Id);
        else if (!listed) await Message("Already in your library", $"{added.Game.Name}: {where}. Refresh the library to see it.");
    }

    Task Message(string title, string text) => App.ShowAsync(new ContentDialog
    {
        XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "OK",
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
        var title = $"Why can't {row.Name} be compiled?";
        if (!ScsKiller.NeedsAesKey(row.State))
            await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = title, Content = new TextBlock { Text = row.Reason, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "Close" });
        else if (await AesKeyDialog.ShowAsync(XamlRoot, row.Id, title, row.Reason)) Vm.Rescan();
    }

    async void OnRecord(object sender, RoutedEventArgs _)
    {
        var row = RowOf(sender);
        if (!await App.ConfirmAsync(this, $"Record {row.Name}?",
                $"SCSKiller adds a small {Fmt.RecorderDll(row.State)} next to the game that writes down every pipeline it creates. " +
                "Play for about 5 minutes, close the game, then add it to the compile queue. You can remove it any time from the game's page.",
                "Add recorder", ContentDialogButton.Primary)) return;
        try { await Task.Run(() => App.Core.InstallRecorder(row.Id)); }   // file IO and the game's re-evaluation
        catch (Exception ex) { await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "Couldn't add the recorder", Content = ex.Message, CloseButtonText = "OK" }); }
        Vm.Refresh();
    }
}
