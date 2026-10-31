using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

public sealed partial class LibraryPage : UserControl
{
    public LibraryVm Vm { get; } = new();
    readonly Coalesced refresh, step;
    bool shiftOnPress;

    public LibraryPage()
    {
        InitializeComponent();
        DataContext = Vm;
        refresh = new(Host.Ui, Vm.Refresh);
        step = new(Host.Ui, Vm.ScanStep);
        // MenuFlyout's presenter keeps the list Items holds when it is first built, before Opening: create it now
        ((MenuFlyout)FoldersButton.Flyout!).Items.Clear();
        // a refresh's Shift: the press's, by mouse or keyboard
        RefreshButton.AddHandler(PointerPressedEvent, (_, e) => shiftOnPress = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel, handledEventsToo: true);
        RefreshButton.AddHandler(KeyDownEvent, (_, e) => shiftOnPress = e.KeyModifiers.HasFlag(KeyModifiers.Shift), RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    void OnChanged(GameState _) => refresh.Request();
    void OnQueue(QueueItem q) { if (!Format.ProgressTick(q)) refresh.Request(); }   // progress ticks change nothing here

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Host.Core.GameChanged += OnChanged; Host.Core.QueueChanged += OnQueue; Host.Icons.Changed += refresh.Request;
        if (Host.Core is ScsKiller k) k.ScanProgressed += step.Request;
        Vm.Load();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        Host.Core.GameChanged -= OnChanged; Host.Core.QueueChanged -= OnQueue; Host.Icons.Changed -= refresh.Request;
        if (Host.Core is ScsKiller k) k.ScanProgressed -= step.Request;
    }

    void OnSearch(object? _, TextChangedEventArgs __) => Vm.Filter = Search.Text ?? "";

    void OnSearchKey(object? _, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || string.IsNullOrEmpty(Search.Text)) return;
        Search.Text = "";
        e.Handled = true;
    }

    /// <summary>--screenshots: a search typed, and the list scrolled to its last section or to a game.</summary>
    public string SearchText { set => Search.Text = value; }
    public void ScrollToEnd() { if (Vm.Games.Count > 0) List.ScrollToEnd(); }
    public void ScrollToTop() => List.Offset = default;
    public void ScrollTo(string id)
    {
        if (RowButtons().FirstOrDefault(b => ((GameRow)b.DataContext!).Id == id) is { } row && row.TranslatePoint(default, (Visual)List.Content!) is { } at)
            List.Offset = new(0, at.Y);
    }

    IEnumerable<Button> RowButtons() => List.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("Row") && b.DataContext is GameRow);

    static GameRow RowOf(object? sender) => (GameRow)((Control)sender!).DataContext!;

    // a row's own buttons raise Click through the row: only a click on the row itself opens the game
    void OnGameClick(object? sender, RoutedEventArgs e) { if (e.Source == sender) App.Main.Navigate(typeof(DetailPage), RowOf(sender).Id); }
    void OnDetails(object? sender, RoutedEventArgs e) { e.Handled = true; App.Main.Navigate(typeof(DetailPage), RowOf(sender).Id); }
    void OnSettings(object? _, RoutedEventArgs __) => App.Main.Navigate(typeof(SettingsPage));
    void OnCache(object? _, RoutedEventArgs __) => App.Main.Navigate(typeof(CachePage));
    void OnPlay(object? sender, RoutedEventArgs e) { e.Handled = true; Vm.Play(RowOf(sender)); }
    void OnShowGame(object? sender, RoutedEventArgs _) => Vm.ShowInLibrary((HiddenRow)((Control)sender!).DataContext!);

    // Either click fetches the server's data whatever its age; Shift+click also forces re-detection, which is otherwise
    // redone only when an exe, build or SCSKiller changes.
    void OnRefresh(object? _, RoutedEventArgs __)
    {
        Vm.Rescan(force: shiftOnPress, userRequested: true);
        shiftOnPress = false;
    }

    // Adding only queues the game: nothing compiles until Compile queue.
    void OnAdd(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        Host.Core.Enqueue(RowOf(sender).Id);
        Vm.Refresh();
    }

    void OnDismissGpuNotice(object? _, RoutedEventArgs __) => Vm.DismissGpuNotice();

    void OnSortName(object? _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.NameAscending, LibrarySort.NameDescending);
    void OnSortPipelines(object? _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.PipelinesDescending, LibrarySort.PipelinesAscending);
    void OnSortCache(object? _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.CacheDescending, LibrarySort.CacheAscending);
    void OnSortStatus(object? _, RoutedEventArgs __) => Vm.SortBy(LibrarySort.Status, LibrarySort.Status);

    // Built as it opens: the folders may have changed since.
    void OnFoldersOpening(object? _, EventArgs __)
    {
        var menu = (MenuFlyout)FoldersButton.Flyout!;
        menu.Items.Clear();
        var add = new MenuItem { Header = "Add a folder of games…", Icon = Glyph(Glyphs.Add) };
        add.Click += OnAddFolder;
        menu.Items.Add(add);
        var leave = new MenuItem { Header = "Leave out a folder…", Icon = Glyph(Glyphs.Blocked) };
        ToolTip.SetTip(leave, "Games installed in it are not listed or read");
        leave.Click += OnLeaveOutFolder;
        menu.Items.Add(leave);
        var folders = Fmt.Added?.Folders ?? [];
        if (folders.Count > 0) menu.Items.Add(new Separator());
        foreach (var folder in folders)
        {
            // a header's "_" marks an access key: doubled, it shows
            var item = new MenuItem { Header = folder.Replace("_", "__"), Icon = Glyph(Glyphs.Dismiss) };
            ToolTip.SetTip(item, "Stop looking for games here");
            item.Click += async (_, _) =>
            {
                if (!await App.ConfirmAsync("Stop looking for games here?",
                        $"{folder}\n\nThe games SCSKiller found only in this folder leave the library. Nothing in the folder changes.", "Stop looking")) return;
                if (Fmt.Added is { } added) await Task.Run(() => added.RemoveFolder(folder));
                Vm.Rescan(force: false);
            };
            menu.Items.Add(item);
        }
        var leftOut = Host.Core.Settings.LeftOutFolders ?? [];
        if (leftOut.Count > 0) menu.Items.Add(new Separator());
        foreach (var folder in leftOut)
        {
            var item = new MenuItem { Header = $"Left out: {folder.Replace("_", "__")}", Icon = Glyph(Glyphs.Refresh) };
            ToolTip.SetTip(item, "Look for games here again");
            item.Click += async (_, _) =>
            {
                await Task.Run(() => Host.Core.ScanFolderAgain(folder));
                Vm.Rescan(force: false);
            };
            menu.Items.Add(item);
        }
    }

    async void OnLeaveOutFolder(object? _, RoutedEventArgs __)
    {
        string? path;
        try { path = (await App.Main.StorageProvider.OpenFolderPickerAsync(new() { Title = "Leave out this folder" })).FirstOrDefault()?.TryGetLocalPath(); }
        catch (Exception ex) { await Message("Couldn't open the folder picker", ex.Message); return; }
        if (path == null) return;
        try { await Task.Run(() => Host.Core.LeaveOutFolder(path)); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Message("Couldn't leave out this folder", ex.Message);
            return;
        }
        Vm.Rescan(force: false);
    }

    static TextBlock Glyph(string glyph) => new() { Classes = { "Icon" }, Text = glyph };

    // Each folder in it is taken for one game, its exe found as for a store's install
    async void OnAddFolder(object? _, RoutedEventArgs __)
    {
        string? path;
        try { path = (await App.Main.StorageProvider.OpenFolderPickerAsync(new() { Title = "Look for games here" })).FirstOrDefault()?.TryGetLocalPath(); }
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
    async void OnAddGame(object? _, RoutedEventArgs __)
    {
        string? path;
        try
        {
            var picked = await App.Main.StorageProvider.OpenFilePickerAsync(new() { Title = "Add game", FileTypeFilter = [new(".exe") { Patterns = ["*.exe"] }] });
            path = picked.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception ex) { await Message("Couldn't open the file picker", ex.Message); return; }
        if (path == null) return;
        ManualAdd added;
        try { added = await Task.Run(() => Host.Core.PreviewManualGame(path)); }   // reads the install's files
        catch (Exception ex)   // a rejection's message, or a data folder SCSKiller couldn't read
        {
            await Message("Couldn't add this game", ex.Message);
            return;
        }
        if (!added.Existed)
        {
            if (await GameFolderDialog.ShowAsync(added.Game, $"Add {added.Game.Name}", "Add game") is not { } folder) return;
            try { await Task.Run(() => Host.Core.AddManualGame(path, folder)); }
            catch (Exception ex)
            {
                await Message("Couldn't add this game", ex.Message);
                return;
            }
            Vm.Rescan(force: false);   // lists it under "Added by you" once its engine and anti-cheat are checked
            return;
        }
        if (Host.Core.HiddenGames.Any(g => g.Id == added.Game.Id))
        {
            await Message("Hidden from your library", $"You removed {added.Game.Name}. Show hidden brings it back.");
            return;
        }
        var listed = Host.Core.Games.Any(s => s.Game.Id == added.Game.Id);
        var where = added.Game.Store == Store.Manual ? "you added it already" : $"SCSKiller found it in {Fmt.StoreName(added.Game)}";
        if (listed && await App.ConfirmAsync("Already in your library", $"{added.Game.Name} is in the list: {where}.", "Open", ContentDialogButton.Primary))
            App.Main.Navigate(typeof(DetailPage), added.Game.Id);
        else if (!listed) await Message("Already in your library", $"{added.Game.Name}: {where}. Refresh the library to see it.");
    }

    static Task Message(string title, string text) => App.ShowAsync(new ContentDialog { Title = title, Content = text, CloseButtonText = "OK" });

    void OnAddAll(object? _, RoutedEventArgs __) => Vm.AddAllReady();
    void OnAddRecommended(object? _, RoutedEventArgs __) => Vm.AddAllRecommended();

    void OnCompileQueue(object? _, RoutedEventArgs __)
    {
        App.CompileQueue();
        App.Main.Navigate(typeof(QueuePage));
    }

    // Unsupported: say why; an encrypted game can be unlocked with its AES key (stored on this PC only).
    async void OnWhy(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = RowOf(sender);
        var title = $"Why can't {row.Name} be compiled?";
        if (!ScsKiller.NeedsAesKey(row.State))
            await App.ShowAsync(new ContentDialog { Title = title, Content = row.Reason, CloseButtonText = "Close" });
        else if (await AesKeyDialog.ShowAsync(row.Id, title, row.Reason)) Vm.Rescan();
    }

    async void OnRecord(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        var row = RowOf(sender);
        if (!await App.ConfirmAsync($"Record {row.Name}?",
                $"SCSKiller adds a small {Fmt.RecorderFiles(row.State)} next to the game that writes down every pipeline it creates. " +
                "Play for about 5 minutes, close the game, then add it to the compile queue. You can remove it any time from the game's page.",
                "Add recorder", ContentDialogButton.Primary)) return;
        try { await Task.Run(() => Host.Core.InstallRecorder(row.Id)); }   // file IO and the game's re-evaluation
        catch (Exception ex) { await App.ShowAsync(new ContentDialog { Title = "Couldn't add the recorder", Content = ex.Message, CloseButtonText = "OK" }); }
        Vm.Refresh();
    }
}
