using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.App.Pages;

public sealed partial class DetailPage : Page
{
    public DetailVm Vm { get; private set; } = null!;

   public DetailPage()
    {
        InitializeComponent();
        var refresh = new Coalesced(DispatcherQueue, () => Vm.Refresh());
        void OnChanged(GameState s) { if (s.Game.Id == Vm?.Row.Id) refresh.Request(); }
        void OnQueue(QueueItem q) { if (q.Stage != QueueStage.Warming) refresh.Request(); }

        Loaded += (_, _) => 
        { 
            App.Core.GameChanged += OnChanged; 
            App.Core.QueueChanged += OnQueue; 
            Icons.Failed += refresh.Request; 
            App.Account.Changed += refresh.Request; 
            Vm.Refresh(); 
            TraducirTextosFijos();
        };

        Unloaded += (_, _) => 
        { 
            App.Core.GameChanged -= OnChanged; 
            App.Core.QueueChanged -= OnQueue; 
            Icons.Failed -= refresh.Request; 
            App.Account.Changed -= refresh.Request; 
        };

        void TraducirTextosFijos()
    {
        // Recorre todos los TextBlock hijos dentro de la página para traducir los que quedaron fijos en XAML
        foreach (var tb in FindChildren<Microsoft.UI.Xaml.Controls.TextBlock>(this))
        {
            switch (tb.Text)
            {
                case "shader compile":
                    tb.Text = "compilación de shaders";
                    break;
                case "other hitch":
                    tb.Text = "otro tirón";
                    break;
                case "loading, quitting":
                    tb.Text = "cargando, saliendo";
                    break;
                case "loading, compiling shaders":
                    tb.Text = "cargando, compilando shaders";
                    break;
                case "Shader coverage":
                    tb.Text = "Cobertura de shaders";
                    break;
                case "Where the shaders come from":
                    tb.Text = "Origen de los shaders";
                    break;
                case "Shaders in the game":
                    tb.Text = "Shaders en el juego";
                    break;
                case "Frame times last time you played":
                    tb.Text = "Tiempos de fotograma de la última sesión";
                    break;
                case "Slow frames":
                    tb.Text = "Fotogramas lentos";
                    break;
                case "Details":
                    tb.Text = "Detalles";
                    break;
                case "Record while I play":
                    tb.Text = "Grabar mientras juego";
                    break;
                case "Ready in the game's own library":
                    tb.Text = "Listo en la propia biblioteca del juego";
                    break;
                case "Ready in the driver cache":
                    tb.Text = "Listo en la caché del controlador";
                    break;
                case "Compiled during play":
                    tb.Text = "Compilado durante la partida";
                    break;
                case "Careful compile":
                    tb.Text = "Compilación cautelosa";
                    break;
            }
        }
    }

    static System.Collections.Generic.IEnumerable<T> FindChildren<T>(Microsoft.UI.Xaml.DependencyObject parent) where T : Microsoft.UI.Xaml.DependencyObject
    {
        int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T typedChild)
                yield return typedChild;

            foreach (var descendant in FindChildren<T>(child))
                yield return descendant;
        }
    }
    }

    public ContentDialog PartlyWhyDialog() => new()
    {
        XamlRoot = XamlRoot, Title = $"Why is {Vm.Name} partly compiled?", CloseButtonText = "Close",
        Content = new TextBlock { Text = DetailVm.PartlyCompiledWhy, TextWrapping = TextWrapping.Wrap },
    };

    async void OnPartlyWhy(object _, RoutedEventArgs __) => await App.ShowAsync(PartlyWhyDialog());

    void OnDbTeaser(Microsoft.UI.Xaml.Documents.Hyperlink _, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs __)
    {
        if (App.Account.SignedIn) App.Main.NavigateToPatreon();
        else App.SignInWithPatreon();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Vm = new DetailVm((string)e.Parameter);
        Vm.ReadCaches();
        Crumbs.ItemsSource = new[] { I18n.T("NavLibrary"), Vm.Name };
        Vm.PropertyChanged += (_, _) => DrawFrames();
        Bindings.Update();
        DrawFrames();
    }

    async void OnGameFolder(object _, RoutedEventArgs __)
    {
        var game = Vm.Row.State.Game;
        if (await GameFolderDialog.ShowAsync(XamlRoot, game, $"{game.Name}'s folder", "Save") is not { } folder) return;
        try { await Task.Run(() => App.Core.AddManualGame(game.ExePath, folder)); }   // checks the folder again, then the game
        catch (Exception ex)
        {
            await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "Couldn't set the game folder", Content = ex.Message, CloseButtonText = "OK" });
            return;
        }
        Vm.Refresh();
    }

    async void OnRemoveGame(object _, RoutedEventArgs __)
    {
        var id = Vm.Row.Id;
        if (!await App.ConfirmAsync(this, $"Remove {Vm.Name} from the library?",
                "SCSKiller forgets the game and takes its recorder out of the game's folder. The game's own files stay, and you can add it again any time.",
                "Remove")) return;
        try { await Task.Run(() => App.Core.RemoveManualGame(id)); }   // refused while a compile of it runs
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "Couldn't remove the game", Content = ex.Message, CloseButtonText = "OK" });
            return;
        }
        App.Main.Navigate(typeof(LibraryPage));
    }

    void OnFrameGraphSize(object _, SizeChangedEventArgs __) => DrawFrames();

    FrameReport? _drawn;
    double _drawnWidth;

    /// <summary>The last session's frame times: the longest frame of each pixel column (a spike is never averaged away),
    /// loading and quitting shaded (a load that compiled shaders in the warning colour), each hitch of play a dot with its
    /// time, length and cause on hover. The y axis stops at 250 ms.</summary>
    void DrawFrames()
    {
        double w = FrameGraph.ActualWidth, h = FrameGraph.ActualHeight;
        if (Vm.F is not { Peaks.Count: > 0 } f || w < 10 || h < 10 || (f == _drawn && w == _drawnWidth)) return;
        (_drawn, _drawnWidth) = (f, w);
        double total = Math.Max(1, f.Duration.TotalMilliseconds);
        int cols = (int)w;
        var col = new double[cols];
        for (int i = 0; i < f.Peaks.Count; i++) col[i * cols / f.Peaks.Count] = Math.Max(col[i * cols / f.Peaks.Count], f.Peaks[i]);
        var play = f.Hitches.Where(x => x.Cause is HitchCause.Shader or HitchCause.Other).Select(x => x.Ms).DefaultIfEmpty(FrameLog.HitchMs).Max();
        double top = Math.Clamp(play * 1.15, 60, 250);
        double X(TimeSpan t) => t.TotalMilliseconds / total * w;
        double Y(double ms) => h - 1 - Math.Min(ms, top) / top * (h - 6);

        var line = new PointCollection();
        for (int c = 0; c < cols; c++) line.Add(new Windows.Foundation.Point(c + 0.5, Y(col[c])));
        GraphTimes.Points = line;
        (GraphLine50.X1, GraphLine50.X2, GraphLine50.Y1, GraphLine50.Y2) = (0, w, Y(50), Y(50));
        GraphLabel50.Margin = new Thickness(2, Y(50) - 16, 0, 0);

        var loading = new GeometryGroup();
        loading.Children.Add(new RectangleGeometry { Rect = new(0, 0, X(f.Startup), h) });
        var (shader, other, compiling) = (new GeometryGroup(), new GeometryGroup(), new GeometryGroup());
        GraphTips.Children.Clear();
        foreach (var x in f.Hitches)
        {
            if (x.Cause is HitchCause.Loading or HitchCause.Quitting or HitchCause.LoadingShaders)
            {
                (x.Cause == HitchCause.LoadingShaders ? compiling : loading).Children.Add(new RectangleGeometry { Rect = new(X(x.At), 0, Math.Max(2, X(TimeSpan.FromMilliseconds(x.Ms))), h) });
                continue;
            }
            var at = new Windows.Foundation.Point(X(x.At), Y(x.Ms));
            (x.Cause == HitchCause.Shader ? shader : other).Children.Add(new EllipseGeometry { Center = at, RadiusX = 3.5, RadiusY = 3.5 });
            var tip = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 14, Height = 14, Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            ToolTipService.SetToolTip(tip, DetailVm.HitchTip(x));
            Canvas.SetLeft(tip, at.X - 7);
            Canvas.SetTop(tip, at.Y - 7);
            GraphTips.Children.Add(tip);
        }
        (GraphLoading.Data, GraphCompiling.Data, GraphShader.Data, GraphOther.Data) = (loading, compiling, shader, other);
    }

    /// <summary>Screenshots: the bottom of the page; false when it all fits.</summary>
    public bool ScrollToEnd() => Scroller.ScrollableHeight > 0 && Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);

    /// <summary>Screenshots: the coverage card's Details and the session's slow frames open.</summary>
    public void ShowDetails() => DetailsExpander.IsExpanded = SlowFramesExpander.IsExpanded = true;

    /// <summary>Screenshots: the frame times card at the top; false without one.</summary>
    public bool ScrollToFrames() => Vm.HasFrames && Scroller.ChangeView(null, FramesCard.TransformToVisual((UIElement)Scroller.Content).TransformPoint(default).Y - 12, null, true);

    void OnCrumb(BreadcrumbBar _, BreadcrumbBarItemClickedEventArgs e)
    {
        if (e.Index == 0) App.Main.ShowLibrary();
    }

    async void OnCompile(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (vm.OffersCareful)
            try { await Task.Run(() => App.Core.SetCarefulCompile(id, true)); }
            catch (Exception ex) { vm.Error = ex.Message; vm.Refresh(); return; }
        App.Core.Compile(id);
        vm.Refresh();
    }

    // Toggled also fires when the binding sets IsOn: only a user's flip differs from the view model.
    void OnCarefulToggled(object _, RoutedEventArgs __) { if (CarefulSwitch.IsOn != Vm.CarefulOn) SetCareful(CarefulSwitch.IsOn); }

    void SetCareful(bool on) => Set((vm, p) => vm.CarefulPending = p, on, id => App.Core.SetCarefulCompile(id, on));

    /// <summary>Never blank: an exception without a message (some WinRT ones) shows its type, and its inner one's.</summary>
    static string Describe(Exception e) => !string.IsNullOrWhiteSpace(e.Message) ? e.Message
        : e.GetType().Name + (e.InnerException is { } inner ? ": " + Describe(inner) : $" (0x{e.HResult:X8})");

    // The core's switches do file I/O: off the UI thread, the switch disabled meanwhile (pending = the state asked for).
    async void Set(Action<DetailVm, bool?> setPending, bool pending, Action<string> change)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        setPending(vm, pending);
        vm.Refresh();
        try
        {
            await Task.Run(() => change(id));
            vm.Error = null;
        }
        catch (Exception ex) { vm.Error = Describe(ex); }
        setPending(vm, null);
        vm.Refresh();   // the switch shows the real state, even after a failure
    }

    async void OnClearCache(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var note = "The game will compile shaders during play again until you re-warm it."
            + (!vm.NoAntiCheat ? " Anti-cheat game: only the driver cache is cleared." : "");
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Measuring the cache files…\n\n" + note };
        var ownText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var alsoOwn = new CheckBox { Content = ownText, Visibility = Visibility.Collapsed };
        _ = Measure();
        if (await App.ShowAsync(App.Confirm(this, $"Clear the shader cache of {vm.Name}?", new StackPanel { Spacing = 12, Children = { text, alsoOwn } }, "Clear cache"))
            != ContentDialogResult.Primary) return;
        var withOwn = alsoOwn.IsChecked == true;
        try { vm.Error = await Task.Run(() => App.Core.ClearGameCache(id, withOwn)) ? null : "Nothing was cleared: no shader-cache files of this game were found."; }
        catch (Exception ex) { vm.Error = ex.Message; }
        vm.ReadCaches();
        vm.Refresh();

        // the sizes fill in while the dialog is open: reading the Windows cache folders can take seconds; Clear reads them again
        async Task Measure()
        {
            try
            {
                var all = await Task.Run(() => App.Core.GameCaches(id, gamePrecache: true));
                static bool Own(CachePart p) => p.Name is Core.App.ScsKiller.PipelinePart or Core.App.ScsKiller.PrecachePart;
                var (parts, own) = (all.Where(p => !Own(p)).ToList(), all.Where(Own).ToList());
                text.Text = (parts.Count > 0 ? string.Join(" · ", parts.Select(p => $"{p.Name} {Format.Bytes(p.Bytes)}")) : "No cache files on disk now") + ".\n\n" + note;
                ownText.Text = $"Also delete the game's own shader cache ({Format.Bytes(own.Sum(p => p.Bytes))}). It rebuilds it at its next start, which then takes longer.";
                alsoOwn.Visibility = own.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex) { text.Text = Describe(ex) + "\n\n" + note; }
        }
    }

    async void OnClearRecording(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (!await App.ConfirmAsync(this, $"Clear the recording of {vm.Name}?",
                "The recorded pipelines are removed. They're compiled again only if the game creates them again while recording, "
                + "and the compile plan goes back to what the game's files give.\n\nThe recorder itself stays in; the game's files aren't touched.",
                "Clear recording")) return;
        vm.ClearingRecording = true;
        vm.Refresh();
        try { vm.Error = await Task.Run(() => App.Core.ClearRecording(id)) ? null : "Nothing was cleared: no recording of this game was found."; }
        catch (Exception ex) { vm.Error = ex.Message; }
        vm.ClearingRecording = false;
        vm.Refresh();
    }

    void OnRecordToggled(object _, RoutedEventArgs __)
    {
        var on = RecordSwitch.IsOn;   // read here: the change runs off the UI thread, where the switch can't be read
        if (on == Vm.RecordOn) return;
        // an eligible EasyAntiCheat game: the switch allows offline sessions
        if (Vm.ShowOffline) Set((vm, p) => vm.OfflinePending = p, on, id => App.Core.SetOfflineRecording(id, on));
        else SetRecord(on);
    }

    void OnRecordTip(object _, RoutedEventArgs __) => SetRecord(true);

    void OnUseDefault(object _, RoutedEventArgs __) => SetRecord(null);

    void SetRecord(bool? on) => Set((vm, p) => vm.RecordPending = p, on ?? App.Core.Settings.RecordAllGames,
        id => App.Core.SetRecorderOverride(id, on switch { true => RecorderOverride.On, false => RecorderOverride.Off, null => RecorderOverride.Default }));

    void OnAlongsideToggled(object _, RoutedEventArgs __) { if (AlongsideSwitch.IsOn != Vm.AlongsideOn) SetAlongside(AlongsideSwitch.IsOn); }

    void SetAlongside(bool on) => Set((vm, p) => vm.AlongsidePending = p, on, id => App.Core.SetRecordAlongsideMod(id, on));

    // Every launch is confirmed here: the core starts a session only with confirmed: true
    async void OnRecordOffline(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (!await App.ConfirmAsync(this, $"Start {vm.Name} offline without EasyAntiCheat?",
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = DetailVm.OfflineRisk + " Online play isn't possible in that session." },
                "Record offline session")) return;
        vm.OfflineStarting = true;
        vm.Refresh();
        Task session;
        try
        {
            session = await Task.Factory.StartNew(() => App.Core.StartOfflineSession(id, confirmed: true), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
            vm.Error = null;
        }
        catch (Exception ex) { (vm.Error, session) = (Describe(ex), Task.CompletedTask); }
        vm.OfflineStarting = false;
        vm.Refresh();
        try { await session; }
        catch (Exception ex) { vm.Error = Describe(ex); }
        vm.Refresh();
    }
}
