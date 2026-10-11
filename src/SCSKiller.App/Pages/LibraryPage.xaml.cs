using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using SCSKiller.Core;

namespace SCSKiller.App.Pages;

public sealed partial class LibraryPage : Page
{
    public LibraryVm Vm { get; } = new();

    public LibraryPage()
    {
        InitializeComponent();
        PageTitle.Text = I18n.T("LibTitle");
        BtnRefreshText.Text = I18n.T("LibRefresh");
        ToolTipService.SetToolTip(BtnRefresh, I18n.T("LibRefreshTip"));
        BtnAddGameText.Text = I18n.T("LibAddGame");
        ToolTipService.SetToolTip(BtnAddGame, I18n.T("LibAddGameTip"));
        CardDriverCacheTitle.Text = I18n.T("LibCardDriverCache");
        CardAfterDriverTitle.Text = I18n.T("LibCardAfterDriver");
        CardCompileSpeedTitle.Text = I18n.T("LibCardCompileSpeed");
        BtnChangeCache.Content = I18n.T("LibBtnChange");
        BtnChangeDriver.Content = I18n.T("LibBtnChange");
        BtnChangeSpeed.Content = I18n.T("LibBtnChange");
        Search.PlaceholderText = I18n.T("LibSearchPlaceholder");
        ColGame.Text = I18n.T("LibColGame");
        ColShaders.Text = I18n.T("LibColShaders");
        ColPipelines.Text = I18n.T("LibColPipelines");
        ColCache.Text = I18n.T("LibColCache");
        ColTime.Text = I18n.T("LibColTime");
        ColStatus.Text = I18n.T("LibColStatus");
        TxtScanEmpty.Text = I18n.T("LibSearchingGames");
        ((CollectionViewSource)Resources["GroupedGames"]).Source = Vm.Groups;
        var refresh = new Coalesced(DispatcherQueue, Vm.Refresh);
        void OnChanged(GameState _) => refresh.Request();
        void OnQueue(QueueItem q) { if (q.Stage != QueueStage.Warming) refresh.Request(); }   // progress ticks change nothing here
        Loaded += (_, _) => { App.Core.GameChanged += OnChanged; App.Core.QueueChanged += OnQueue; Icons.Failed += refresh.Request; Vm.Load(); };
        Unloaded += (_, _) => { App.Core.GameChanged -= OnChanged; App.Core.QueueChanged -= OnQueue; Icons.Failed -= refresh.Request; };
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

    static GameRow RowOf(object sender) => (GameRow)((FrameworkElement)sender).DataContext;

    void OnGameClick(object _, ItemClickEventArgs e) => App.Main.Navigate(typeof(DetailPage), ((GameRow)e.ClickedItem).Id);
    void OnDetails(object sender, RoutedEventArgs _) => App.Main.Navigate(typeof(DetailPage), RowOf(sender).Id);
    void OnSettings(object _, RoutedEventArgs __) => App.Main.Navigate(typeof(SettingsPage));
    void OnPlay(object sender, RoutedEventArgs _) => Vm.Play(RowOf(sender));

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
        var text = new TextBlock { Text = row.Reason, TextWrapping = TextWrapping.Wrap };
        var box = new TextBox { PlaceholderText = "0x followed by 64 hex digits", Header = "AES key", Visibility = row.IsEncrypted ? Visibility.Visible : Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, Children = { text, box } };
        if (row.IsEncrypted)
            panel.Children.Insert(1, new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
                "SCSKiller couldn't find this game's key in its exe (protected exes hide it). If you have the key, paste it here: " +
                "it's checked against the game's files and kept on this PC only." });
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Why can't {row.Name} be compiled?", Content = panel, CloseButtonText = "Close",
            PrimaryButtonText = row.IsEncrypted ? "Unlock" : "", DefaultButton = ContentDialogButton.Close,
        };
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        bool ok;
        var key = box.Text.Trim();
        try { ok = await Task.Run(() => App.Core.SetEncryptionKey(row.Id, key)); }   // opens the game's files
        catch (Exception ex) { ok = false; text.Text = ex.Message; }
        if (ok) Vm.Rescan();
        else await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "That key didn't work", Content = "It doesn't open this game's files.", CloseButtonText = "OK" });
    }

    async void OnRecord(object sender, RoutedEventArgs _)
    {
        var row = RowOf(sender);
        if (!await App.ConfirmAsync(this, $"Record {row.Name}?",
                "SCSKiller adds a small d3d12.dll next to the game that writes down every pipeline it creates. " +
                "Play for about 5 minutes, close the game, then add it to the compile queue. You can remove it any time from the game's page.",
                "Add recorder", ContentDialogButton.Primary)) return;
        try { await Task.Run(() => App.Core.InstallRecorder(row.Id)); }   // file IO and the game's re-evaluation
        catch (Exception ex) { await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "Couldn't add the recorder", Content = ex.Message, CloseButtonText = "OK" }); }
        Vm.Refresh();
    }
}
