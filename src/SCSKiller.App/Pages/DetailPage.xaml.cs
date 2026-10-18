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
        var progress = new Coalesced(DispatcherQueue, () => Vm.RefreshProgress());
        void OnChanged(GameState s) { if (s.Game.Id == Vm?.Row.Id) refresh.Request(); }   // a scan raises one per game
        // a warm raises a tick every progress report: only this game's move the page, and only its progress
        void OnQueue(QueueItem q)
        {
            if (q.Stage != QueueStage.Warming) refresh.Request();
            else if (q.GameId == Vm?.Row.Id) progress.Request();
        }
        // Refresh too: a change raised while the page wasn't loaded (e.g. the game exited) would show only at the next one
        Loaded += (_, _) => { App.Core.GameChanged += OnChanged; App.Core.QueueChanged += OnQueue; Icons.Failed += refresh.Request; App.Account.Changed += refresh.Request; Vm.Refresh(); Check(); };
        Unloaded += (_, _) => { App.Core.GameChanged -= OnChanged; App.Core.QueueChanged -= OnQueue; Icons.Failed -= refresh.Request; App.Account.Changed -= refresh.Request; };
    }

    // a game the scan hasn't read yet is read now, its state comes back as GameChanged
    void Check()
    {
        if (App.Core is not ScsKiller k) return;
        var (id, name) = (Vm.Row.Id, Vm.Name);
        _ = Task.Run(() =>
        {
            try { k.CheckGame(id); }
            catch (Exception e) { k.Log?.Report($"checking {name} failed: {e.Message}"); }
        });
    }

    public ContentDialog PartlyWhyDialog() => new()
    {
        XamlRoot = XamlRoot, Title = $"Why is {Vm.Name} partly compiled?", CloseButtonText = "Close",
        Content = new TextBlock { Text = DetailVm.PartlyCompiledWhy, TextWrapping = TextWrapping.Wrap },
    };

    async void OnPartlyWhy(object _, RoutedEventArgs __) => await App.ShowAsync(PartlyWhyDialog());

    async void OnSetAesKey(object _, RoutedEventArgs __)
    {
        var vm = Vm;
        if (await AesKeyDialog.ShowAsync(XamlRoot, vm.Row.Id, $"Set the AES key for {vm.Name}")) vm.RefreshGame();
    }

    static readonly (CompatChoice Choice, string Text)[] ReportChoices =
    [
        (CompatChoice.Gone, "Shader compilation stutter is gone."),
        (CompatChoice.Better, "Shader compilation stutter is better, but some stutter remains."),
        (CompatChoice.NoHelp, "The game compiled, but it did not help with the stutter."),
        (CompatChoice.Worse, "The game runs worse than before compiling."),
        (CompatChoice.NotCompiled, "The game could not be compiled."),
        (CompatChoice.NoStutterBefore, "The game had no shader compilation stutter before compiling either."),
    ];

    /// <summary>Rate this compile's popup for this PC's earlier report of the build (<paramref name="sent"/>), or its thanks
    /// (<paramref name="thanks"/>); built apart so --screenshots renders each state without a click. Only its Send rating
    /// button sends. What a report carries shows until one was sent from this install (<see cref="SCSKiller.Core.Settings.CompatReportSent"/>).</summary>
    public ContentDialog ReportDialog(CompatSent? sent, bool thanks = false)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var dialog = App.Confirm(this, $"Rate this compile for {vm.Name}", null!, "Send rating");
        if (thanks) return Thanked(dialog);
        static TextBlock Text(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap };
        var choices = new RadioButtons();
        foreach (var (_, t) in ReportChoices) choices.Items.Add(new RadioButton { Content = Text(t) });
        CheckBox Box(string t) => new() { Content = Text(t) };
        var (frameGen, upscaler, mods) = (Box("Frame generation was on"), Box("An upscaler was on (DLSS, FSR or XeSS)"),
            Box("Mods were installed (for example ReShade, OptiScaler or RenoDX)"));
        var problem = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["WarnBrush"], Visibility = Visibility.Collapsed };
        var panel = new StackPanel { Spacing = 12, Children = { Text($"Tell other players how {vm.Name} ran after SCSKiller compiled it. The rating is anonymous.") } };
        if (sent != null) panel.Children.Add(new TextBlock { Text = "You already rated this version of the game. Rating again replaces it.", TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        panel.Children.Add(choices);
        panel.Children.Add(new StackPanel { Children = { frameGen, upscaler, mods } });
        if (!App.Core.Settings.CompatReportSent)
            panel.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = "Sent with the rating: your CPU model, GPU model, driver version, Windows version, and what SCSKiller compiled for this game. Nothing else.",
                        TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Secondary"] },
                    new HyperlinkButton { Content = "What's shared", NavigateUri = new Uri(PrivacyReports), Padding = new(0) },
                },
            });
        panel.Children.Add(problem);
        dialog.Content = new ScrollViewer { Content = panel };
        dialog.IsPrimaryButtonEnabled = false;   // until a choice is picked
        choices.SelectionChanged += (_, _) => dialog.IsPrimaryButtonEnabled = choices.SelectedIndex >= 0;
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            args.Cancel = true;   // the popup stays for the answer
            var deferral = args.GetDeferral();
            d.IsPrimaryButtonEnabled = false;
            string? why;
            // read here: the send runs off the UI thread, where the controls can't be read
            var (choice, fg, up, md) = (ReportChoices[choices.SelectedIndex].Choice, frameGen.IsChecked == true, upscaler.IsChecked == true, mods.IsChecked == true);
            try { why = await Task.Run(() => App.Core.SendReportAsync(id, choice, fg, up, md)); }
            catch (Exception ex) { why = Describe(ex); }
            if (why == null)
            {
                Thanked(d);
                try { App.Core.Settings = App.Core.Settings with { CompatReportSent = true }; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // unsaved: the disclosure shows once more
            }
            else
            {
                (problem.Text, problem.Visibility) = (why, Visibility.Visible);
                d.IsPrimaryButtonEnabled = true;
            }
            deferral.Complete();
        };
        return dialog;

        static ContentDialog Thanked(ContentDialog d)
        {
            (d.Content, d.PrimaryButtonText, d.CloseButtonText) = (Text("Thanks, your rating was sent."), "", "Close");
            return d;
        }
    }

    /// <summary>The privacy page's section on compatibility reports.</summary>
    public const string PrivacyReports = "https://scskiller.com/privacy#compatibility-reports";

    /// <summary>The privacy page's section on problem reports.</summary>
    public const string PrivacyProblems = "https://scskiller.com/privacy#problem-reports";

    /// <summary>"Report a problem": the user's words, then the whole report as it will be sent. Nothing is sent before Send.
    /// <paramref name="sentId"/>: the thanks with that report id (screenshots); <paramref name="message"/> fills the box.</summary>
    public ContentDialog ProblemDialog(string? sentId = null, string? message = null)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var store = Fmt.StoreName(vm.Row.State.Game);
        var page = new (string, string)[]
        {
            ("Status", $"{vm.StatusTitle}. {vm.StatusReason}"),
            ("Coverage", vm.CoverageHeadline),
            (vm.TimeLabel, vm.HasTimeValue ? vm.TimeValue : ""),
            (vm.DiskLabel, vm.HasDiskValue ? vm.DiskValue : ""),
            (vm.StuttersLabel, vm.HasStutterTile ? vm.StuttersValue : ""),
            (vm.CompileTitle, vm.HasCompileCard ? vm.CompileText : ""),
            ("Last session", vm.FramesVerdict),
        }.Concat(vm.Details.Select(d => (d.Label, d.Value))).ToList();
        var dialog = App.Confirm(this, $"Report a problem with {vm.Name}", null!, "Send");
        if (sentId != null) return Thanked(dialog, sentId);
        static TextBlock Text(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap };
        var words = new TextBox { Header = "What happened?", Text = message ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxLength = ProblemReport.MaxMessageChars };
        var withLog = new CheckBox { Content = Text("Include the app's log lines about this game"), IsChecked = true };
        var report = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240,
            FontFamily = new FontFamily("Consolas"), FontSize = 12,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(report, ScrollBarVisibility.Auto);
        var problemText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.Resources["WarnBrush"], Visibility = Visibility.Collapsed };
        var built = "";   // what is sent: the TextBox turns line ends into \r
        void Build()
        {
            try { report.Text = built = App.Core.ProblemReport(id, store, page, withLog.IsChecked == true); }
            catch (Exception ex) { report.Text = built = ""; (problemText.Text, problemText.Visibility) = (Describe(ex), Visibility.Visible); }
            Enable();
        }
        void Enable() => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(words.Text) && built.Length > 0;
        dialog.Content = new ScrollViewer
        {
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    words, new StackPanel { Spacing = 8, Children = { Text("Sent with your message"), report } }, withLog,
                    new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = "Sent to the SCSKiller server, kept 90 days and only read to fix the problem.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Secondary"] },
                            new HyperlinkButton { Content = "What's sent", NavigateUri = new Uri(PrivacyProblems), Padding = new(0) },
                        },
                    },
                    problemText,
                },
            },
        };
        Build();
        withLog.Click += (_, _) => Build();
        words.TextChanged += (_, _) => Enable();
        dialog.PrimaryButtonClick += async (d, args) =>
        {
            args.Cancel = true;   // the popup stays for the answer
            var deferral = args.GetDeferral();
            d.IsPrimaryButtonEnabled = false;
            var (text, body) = (words.Text.Trim().ReplaceLineEndings("\n"), built);
            string? sent, why;
            try { (sent, why) = await Task.Run(() => App.Core.SendProblemAsync(text, body)); }
            catch (Exception ex) { (sent, why) = (null, Describe(ex)); }
            if (sent != null) Thanked(d, sent);
            else
            {
                (problemText.Text, problemText.Visibility) = (why ?? "Not sent. Try again later.", Visibility.Visible);
                d.IsPrimaryButtonEnabled = true;
            }
            deferral.Complete();
        };
        return dialog;

        static ContentDialog Thanked(ContentDialog d, string reportId)
        {
            (d.PrimaryButtonText, d.CloseButtonText) = ("", "Close");
            d.Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = "Thanks, your report was sent.", TextWrapping = TextWrapping.Wrap },
                    new TextBlock { Text = $"Report ID {reportId}", IsTextSelectionEnabled = true, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                    new TextBlock { Text = "Mention it on Discord or GitHub so we can find your report.", TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Secondary"] },
                },
            };
            return d;
        }
    }

    async void OnReportProblem(object _, RoutedEventArgs __) => await App.ShowAsync(ProblemDialog());

    async void OnSendReport(object _, RoutedEventArgs __)
    {
        var id = Vm.Row.Id;
        CompatSent? sent = null;
        try { sent = await Task.Run(() => App.Core.ReportSent(id)); }
        catch (Exception) { }   // not known: the popup says nothing about an earlier report
        await App.ShowAsync(ReportDialog(sent));
    }

    void OnDbTeaser(Microsoft.UI.Xaml.Documents.Hyperlink _, Microsoft.UI.Xaml.Documents.HyperlinkClickEventArgs __)
    {
        if (App.Account.SignedIn) App.Main.NavigateToPatreon();
        else App.SignInWithPatreon();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        Vm = new DetailVm((string)e.Parameter);
        Vm.ReadCaches();
        Crumbs.ItemsSource = new[] { "Library", Vm.Name };
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

    // Only the name shown: the game's id, which everything SCSKiller keeps of it is stored under, stays.
    async void OnRename(object _, RoutedEventArgs __)
    {
        var box = new TextBox { Text = Vm.Name, Header = "Name", Description = "Only the name SCSKiller shows changes.", MaxLength = 200 };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot, Title = $"Rename {Vm.Name}", Content = box, PrimaryButtonText = "Rename", CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        box.Loaded += (_, _) => box.SelectAll();
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        try { Fmt.Rename(Vm.Row.Id, box.Text); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "Couldn't rename the game", Content = ex.Message, CloseButtonText = "OK" });
            return;
        }
        Vm.Refresh();
        Crumbs.ItemsSource = new[] { "Library", Vm.Name };
    }

    async void OnRemoveGame(object _, RoutedEventArgs __)
    {
        var (id, manual) = (Vm.Row.Id, Vm.IsManual);
        if (!await App.ConfirmAsync(this, $"Remove {Vm.Name} from the library?",
                manual ? "SCSKiller forgets the game and takes its recorder out of the game's folder. The game's own files stay, and you can add it again any time." : Vm.HideNote,
                "Remove")) return;
        // refused while a compile of it runs, and a store game while it runs or its recorder stays in
        try { await Task.Run(() => { if (manual) App.Core.RemoveManualGame(id); else App.Core.HideGame(id); }); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await App.ShowAsync(new ContentDialog { XamlRoot = XamlRoot, Title = "Couldn't remove the game", Content = ex.Message, CloseButtonText = "OK" });
            return;
        }
        App.Main.Navigate(typeof(LibraryPage));
    }

    void DrawFrames() => Graph.Show(Vm.F is { } f ? FrameData.Of(f) : null);

    /// <summary>Screenshots: the bottom of the page; false when it all fits.</summary>
    public bool ScrollToEnd() => Scroller.ScrollableHeight > 0 && Scroller.ChangeView(null, Scroller.ScrollableHeight, null, true);

    /// <summary>Screenshots: the coverage card's Details and the session's slow frames open.</summary>
    public void ShowDetails() => DetailsExpander.IsExpanded = SlowFramesExpander.IsExpanded = EarlierExpander.IsExpanded = true;

    /// <summary>Screenshots: the compile card with its kept-aside lines; false without them.</summary>
    public bool ScrollToKept() => Vm.HasKept && Scroller.ChangeView(null, KeptPanel.TransformToVisual((UIElement)Scroller.Content).TransformPoint(default).Y - 260, null, true);

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

    async void OnMaxCompile(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (!App.Core.Settings.MaxCompileConfirmed)
        {
            if (!await App.ConfirmAsync(this, $"Max compile {vm.Name}?", DetailVm.MaxCompileWhy, "Max compile")) return;
            App.Core.Settings = App.Core.Settings with { MaxCompileConfirmed = true };
        }
        App.Core.CompileMax(id);
        vm.Refresh();
    }

    // Toggled also fires when the binding sets IsOn: only a user's flip differs from the view model.
    void OnCarefulToggled(object _, RoutedEventArgs __) { if (CarefulSwitch.IsOn != Vm.CarefulOn) SetCareful(CarefulSwitch.IsOn); }

    void OnLeftOutToggled(object _, RoutedEventArgs __)
    {
        var on = LeftOutSwitch.IsOn;
        if (on != Vm.LeftOutOn) Set((vm, p) => vm.LeftOutPending = p, on, id => App.Core.SetGameLeftOutOfAddAll(id, on));
    }

    void SetCareful(bool on) => Set((vm, p) => vm.CarefulPending = p, on, id => App.Core.SetCarefulCompile(id, on));

    async void OnRayTracingChanged(object _, SelectionChangedEventArgs __)
    {
        var (vm, id, i) = (Vm, Vm.Row.Id, RayTracingBox.SelectedIndex);
        if (i < 0 || i == vm.RayTracingIndex) return;
        try
        {
            await Task.Run(() => App.Core.SetGameRayTracing(id, i switch { 1 => true, 2 => false, _ => null }));
            vm.Error = null;
        }
        catch (Exception ex) { vm.Error = Describe(ex); }
        vm.Refresh();
    }

    async void OnNewShadersChanged(object _, SelectionChangedEventArgs __)
    {
        var (vm, id, i) = (Vm, Vm.Row.Id, NewShadersBox.SelectedIndex);
        if (i < 0 || i == vm.NewShadersIndex) return;
        try
        {
            await Task.Run(() => App.Core.SetNewShadersMode(id, i == 0 ? null : (NewShadersMode)(i - 1)));
            vm.Error = null;
        }
        catch (Exception ex) { vm.Error = Describe(ex); }
        vm.Refresh();
    }

    // A binding's own update selects ApiIndex: only a different pick goes to the core.
    void OnApiPicked(object sender, SelectionChangedEventArgs _)
    {
        var i = ((ComboBox)sender).SelectedIndex;
        if (i < 0 || i == Vm.ApiIndex || !Vm.CanPickApi) return;
        DxApi? api = i == 1 ? DxApi.D3D11 : i == 2 ? DxApi.D3D12 : null;
        Set((vm, p) => vm.ApiPending = p, true, id => App.Core.SetApiOverride(id, api));
    }

    void OnGameMaximum(object _, RoutedEventArgs __)
    {
        var on = Vm.MaximumSuggested;
        Set((vm, p) => vm.MaximumPending = p, on, id => App.Core.SetGameMaximum(id, on));
    }

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
            + (vm.Row.State.AntiCheat != AntiCheat.None ? " Anti-cheat game: only the driver cache is cleared." : "");   // VAC: as any game (ScsKiller.GameCaches)
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

    async void OnBeforeAfter(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var ba = vm.Row.State.BeforeAfter;
        string? error = ba?.Stage switch
        {
            null => await BeforeAfterUi.StartAsync(XamlRoot, id),
            _ when BeforeAfterUi.Voided(ba) => await BeforeAfterUi.StartAsync(XamlRoot, id, over: true),
            BeforeAfterStage.BeforeArmed when ba!.ClearPending => await BeforeAfterUi.ContinueAsync(id),
            BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed => await BeforeAfterUi.LaunchAsync(id),
            BeforeAfterStage.AfterReady => await BeforeAfterUi.ContinueAsync(id),
            BeforeAfterStage.Done => await ReportWindow.OpenAsync(id) is null ? "The report isn't there anymore: start over." : null,
            _ => null,
        };
        Shown(vm, error);
    }

    async void OnBaRecordAgain(object _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterUi.RecordAfterAgainAsync(XamlRoot, Vm.Row.Id));
    async void OnBaStartOver(object _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterUi.StartAsync(XamlRoot, Vm.Row.Id, over: true));
    async void OnBaCancel(object _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterUi.CancelAsync(XamlRoot, Vm.Row.Id));
    async void OnBaHowItWorks(object _, RoutedEventArgs __) => await BeforeAfterUi.IntroAsync(XamlRoot, Vm.Name, BeforeAfterUi.Intro.HowItWorks);
    // Continue in Compiling clears and compiles again, first in the queue
    async void OnBaCompileAgain(object _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterUi.ContinueAsync(Vm.Row.Id));

    static void Shown(DetailVm vm, string? error)
    {
        vm.Error = error;
        vm.Refresh();
    }

    async void OnClearRecording(object _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (!await App.ConfirmAsync(this, $"Clear the recording of {vm.Name}?",
                "The recorded pipelines are removed. They're compiled again only if the game creates them again while recording, "
                + "and the compile plan goes back to what the game's files give.\n\nThe recorder itself stays in, and the game's files aren't touched.",
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
        // an eligible anti-cheat game: the switch allows sessions (offline, or a confirmed session in a game with anti-cheat)
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
        if (vm.Risk) { await RecordRisk(vm, id); return; }
        if (!await App.ConfirmAsync(this, $"Start {vm.Name} offline without EasyAntiCheat?",
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = DetailVm.OfflineRisk + " Online play isn't possible in that session." },
                "Record offline session")) return;
        await StartSession(vm, () => App.Core.StartOfflineSession(id, confirmed: true));
    }

    // A fresh check first, so the confirmation names the anti-cheat that is there now; the core refuses if it finds another.
    async Task RecordRisk(DetailVm vm, string id)
    {
        vm.OfflineStarting = true;   // before the check: the button stays off until the dialog is answered
        vm.Refresh();
        AntiCheat shown;
        try { shown = await App.Core.CheckAntiCheatAsync(id); }
        catch (Exception ex)
        {
            (vm.Error, vm.OfflineStarting) = (Describe(ex), false);
            vm.Refresh();
            return;
        }
        var fresh = vm.Row.State with { AntiCheat = shown };   // VAC stays the state's: the check only names a client anti-cheat
        var ok = await App.ConfirmAsync(this, $"Record {vm.Name} at your own risk?",
            new TextBlock { TextWrapping = TextWrapping.Wrap, Text = DetailVm.RiskText(fresh) + " SCSKiller removes its files when the game exits, or after a crash at your next logon." },
            "Record session");
        if (!ok)
        {
            vm.OfflineStarting = false;
            vm.Refresh();
            return;
        }
        await StartSession(vm, () => App.Core.StartRiskSession(id, confirmed: true, shown));
    }

    // The start runs off the UI thread; the task it returns ends when the game exited and its files are out.
    async Task StartSession(DetailVm vm, Func<Task> start)
    {
        vm.OfflineStarting = true;
        vm.Refresh();
        Task session;
        try
        {
            session = await Task.Factory.StartNew(start, CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default);
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
