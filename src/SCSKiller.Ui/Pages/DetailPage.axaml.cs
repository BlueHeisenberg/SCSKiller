using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

public sealed partial class DetailPage : UserControl, INavigable
{
    public DetailVm Vm { get; private set; } = null!;
    readonly Coalesced refresh, progress;

    public DetailPage()
    {
        InitializeComponent();
        refresh = new(Host.Ui, () => Vm.Refresh());
        progress = new(Host.Ui, () => Vm.RefreshProgress());
    }

    void OnChanged(GameState s) { if (s.Game.Id == Vm?.Row.Id) refresh.Request(); }   // a scan raises one per game
    // a warm raises a tick every progress report: only this game's move the page, and only its progress
    void OnQueue(QueueItem q)
    {
        if (!Format.ProgressTick(q)) refresh.Request();
        else if (q.GameId == Vm?.Row.Id) progress.Request();
    }

    // Refresh too: a change raised while the page wasn't loaded (e.g. the game exited) would show only at the next one
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Host.Core.GameChanged += OnChanged; Host.Core.QueueChanged += OnQueue; Host.Icons.Changed += refresh.Request;
        if (Host.Core is ScsKiller k) k.ScanProgressed += progress.Request;   // a D3DSCache fold's share (FoldLine)
        if (Host.Account is { } account) account.Changed += refresh.Request;
        Vm.Refresh();
        Check();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        Host.Core.GameChanged -= OnChanged; Host.Core.QueueChanged -= OnQueue; Host.Icons.Changed -= refresh.Request;
        if (Host.Core is ScsKiller k) k.ScanProgressed -= progress.Request;
        if (Host.Account is { } account) account.Changed -= refresh.Request;
    }

    public void NavigatedTo(object? arg)
    {
        Vm = new DetailVm((string)arg!);
        Vm.ReadCaches();
        CrumbName.Text = Vm.Name;
        Vm.PropertyChanged += (_, _) => Shown();
        DataContext = Vm;
        Shown();
    }

    // what bindings can't set: the frame graph and the session bar's segments
    void Shown()
    {
        Graph.Show(Vm.F is { } f ? FrameData.Of(f) with { Neutral = Vm.FramesOnly } : null);
        var columns = SessionBar.ColumnDefinitions;
        columns[0].Width = new(Vm.LibraryWidth, GridUnitType.Star);
        columns[1].Width = new(Vm.HitsWidth, GridUnitType.Star);
        columns[2].Width = new(Vm.CompilesWidth, GridUnitType.Star);
        columns[2].MinWidth = Vm.CompilesMinWidth;
    }

    // a game the scan hasn't read yet is read now, its state comes back as GameChanged
    void Check()
    {
        if (Host.Core is not ScsKiller k) return;
        var (id, name) = (Vm.Row.Id, Vm.Name);
        _ = Task.Run(() =>
        {
            try { k.CheckGame(id); }
            catch (Exception e) { k.Log?.Report($"checking {name} failed: {e.Message}"); }
        });
    }

    static TextBlock Text(string t) => new() { Text = t, TextWrapping = TextWrapping.Wrap };

    static TextBlock Warn(TextBlock t)
    {
        t.Bind(TextBlock.ForegroundProperty, t.GetResourceObservable("WarnBrush"));
        return t;
    }

    public ContentDialog PartlyWhyDialog() => new() { Title = Loc.T("Detail_WhyPartlyCompiled", Vm.Name), CloseButtonText = Loc.T("Common_Close"), Content = Vm.PartlyCompiledWhy };

    async void OnPartlyWhy(object? _, RoutedEventArgs __) => await App.ShowAsync(PartlyWhyDialog());

    async void OnSetAesKey(object? _, RoutedEventArgs __)
    {
        var vm = Vm;
        if (await AesKeyDialog.ShowAsync(vm.Row.Id, Loc.T("Detail_SetAesKeyFor", vm.Name))) vm.RefreshGame();
    }

    async void OnChangeAesKey(object? _, RoutedEventArgs __)
    {
        var vm = Vm;
        if (await AesKeyDialog.ShowAsync(vm.Row.Id, Loc.T("Detail_ChangeAesKeyFor", vm.Name))) vm.RefreshGame();
    }

    async void OnRemoveAesKey(object? _, RoutedEventArgs __)
    {
        var vm = Vm;
        var dialog = new ContentDialog
        {
            Title = Loc.T("Detail_RemoveAesKeyFor", vm.Name), PrimaryButtonText = Loc.T("Common_Remove"), CloseButtonText = Loc.T("Common_Cancel"),
            Content = new TextBlock { Text = DetailVm.RemoveAesKeyNote, TextWrapping = TextWrapping.Wrap },
        };
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        Host.Core.RemoveEncryptionKey(vm.Row.Id);
        vm.RefreshGame();
    }

    async void OnSetGameKey(object? _, RoutedEventArgs __)
    {
        var vm = Vm;
        if (await AesKeyDialog.ShowAsync(vm.Row.Id, Loc.T("Detail_SetGameKeyFor", vm.Name), aes: false)) vm.RefreshGame();
    }

    void OnRefreshGame(object? _, RoutedEventArgs __) => Vm.RefreshGame();
    async void OnKeepShaders(object? _, RoutedEventArgs __) => await Vm.KeepShadersAsync();
    async void OnCopyLaunchLine(object? _, RoutedEventArgs __) => await Vm.CopyLaunchLine();
    void OnPlay(object? _, RoutedEventArgs __) => Vm.Play();

    static readonly (CompatChoice Choice, string Text)[] ReportChoices =
    [
        (CompatChoice.Gone, Loc.T("Compat_Gone")),
        (CompatChoice.Better, Loc.T("Compat_Better")),
        (CompatChoice.NoHelp, Loc.T("Compat_NoHelp")),
        (CompatChoice.Worse, Loc.T("Compat_Worse")),
        (CompatChoice.NotCompiled, Loc.T("Compat_NotCompiled")),
        (CompatChoice.NoStutterBefore, Loc.T("Compat_NoStutterBefore")),
    ];

    /// <summary>Rate this compile's popup for this PC's earlier report of the build (<paramref name="sent"/>), or its thanks
    /// (<paramref name="thanks"/>); built apart so --screenshots renders each state without a click. Only its Send rating
    /// button sends. What a report carries shows until one was sent from this install (<see cref="Settings.CompatReportSent"/>).</summary>
    public ContentDialog ReportDialog(CompatSent? sent, bool thanks = false)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var dialog = App.Confirm(Loc.T("Detail_RateFor", vm.Name), null, Loc.T("Detail_SendRating"));
        if (thanks) return Thanked(dialog);
        var group = $"rating-{Guid.NewGuid():N}";
        // WinUI's RadioButtons: 32 px rows 8 apart, 4 px below its empty header (measured)
        var choices = new StackPanel { Spacing = 8, Margin = new(0, 4, 0, 0) };
        foreach (var (_, t) in ReportChoices) choices.Children.Add(new RadioButton { Content = Text(t), GroupName = group });
        int Picked() => choices.Children.Cast<RadioButton>().ToList().FindIndex(r => r.IsChecked == true);
        static CheckBox Box(string t) => new() { Content = Text(t) };
        var (frameGen, upscaler, mods) = (Box(Loc.T("Detail_RateFrameGen")), Box(Loc.T("Detail_RateUpscaler")),
            Box(Loc.T("Detail_RateMods")));
        var problem = Warn(new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false });
        var panel = new StackPanel { Spacing = 12, Children = { Text(Loc.T("Detail_RateIntro", vm.Name)) } };
        if (sent != null) panel.Children.Add(new TextBlock { Text = Loc.T("Detail_AlreadyRated"), TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(choices);
        panel.Children.Add(new StackPanel { Children = { frameGen, upscaler, mods } });
        if (!Host.Core.Settings.CompatReportSent)
            panel.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = CompatReport.WhatIsSent, TextWrapping = TextWrapping.Wrap, Classes = { "Secondary" } },
                    new HyperlinkButton { Content = Loc.T("Settings_WhatsShared"), NavigateUri = new Uri(PrivacyReports), Padding = new(0) },
                },
            });
        panel.Children.Add(problem);
        dialog.Content = panel;
        dialog.IsPrimaryButtonEnabled = false;   // until a choice is picked
        foreach (var choice in choices.Children.Cast<RadioButton>()) choice.IsCheckedChanged += (_, _) => dialog.IsPrimaryButtonEnabled = Picked() >= 0;
        dialog.PrimaryButtonClick = async () =>
        {
            dialog.IsPrimaryButtonEnabled = false;
            string? why;
            // read here: the send runs off the UI thread, where the controls can't be read
            var (choice, fg, up, md) = (ReportChoices[Picked()].Choice, frameGen.IsChecked == true, upscaler.IsChecked == true, mods.IsChecked == true);
            try { why = await Task.Run(() => Host.Core.SendReportAsync(id, choice, fg, up, md)); }
            catch (Exception ex) { why = Describe(ex); }
            if (why == null)
            {
                Thanked(dialog);
                try { Host.Core.Settings = Host.Core.Settings with { CompatReportSent = true }; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // unsaved: the disclosure shows once more
            }
            else
            {
                (problem.Text, problem.IsVisible) = (why, true);
                dialog.IsPrimaryButtonEnabled = true;
            }
            return false;   // the popup stays for the answer
        };
        return dialog;

        static ContentDialog Thanked(ContentDialog d)
        {
            (d.Content, d.PrimaryButtonText, d.CloseButtonText) = (Text(Loc.T("Detail_RatingThanks")), "", Loc.T("Common_Close"));
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
            (Loc.T("Detail_RbStatus"), $"{vm.StatusTitle}. {vm.StatusReason}"),
            (Loc.T("Detail_RbCoverage"), vm.CoverageHeadline),
            (vm.TimeLabel, vm.HasTimeValue ? vm.TimeValue : ""),
            (vm.DiskLabel, vm.HasDiskValue ? vm.DiskValue : ""),
            (vm.StuttersLabel, vm.HasStutterTile ? vm.StuttersValue : ""),
            (vm.CompileTitle, vm.HasCompileCard ? vm.CompileText : ""),
            (Loc.T("Detail_RbLastSession"), vm.FramesVerdict),
        }.Concat(vm.Details.Select(d => (d.Label, d.Value))).ToList();
        var dialog = App.Confirm(Loc.T("Detail_ReportProblemFor", vm.Name), null, Loc.T("Common_Send"));
        if (sentId != null) return Thanked(dialog, sentId);
        var words = new TextBox { Text = message ?? "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxLength = ProblemReport.MaxMessageChars };
        var withLog = new CheckBox { Content = Text(Loc.T("Detail_IncludeLog")), IsChecked = true };
        var report = new TextBox
        {
            IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 240,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, DejaVu Sans Mono, monospace"), FontSize = 12,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(report, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var problemText = Warn(new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false });
        var built = "";   // what is sent
        void Build()
        {
            try { report.Text = built = Host.Core.ProblemReport(id, store, page, withLog.IsChecked == true); }
            catch (Exception ex) { report.Text = built = ""; (problemText.Text, problemText.IsVisible) = (Describe(ex), true); }
            Enable();
        }
        void Enable() => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(words.Text) && built.Length > 0;
        dialog.Content = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new StackPanel { Spacing = 8, Children = { new TextBlock { Text = Loc.T("Detail_WhatHappened") }, words } },
                new StackPanel { Spacing = 8, Children = { Text(Loc.T("Detail_SentWith")), report } }, withLog,
                new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock { Text = Loc.T("Detail_ReportSentNote"), TextWrapping = TextWrapping.Wrap, Classes = { "Secondary" } },
                        new HyperlinkButton { Content = Loc.T("Detail_WhatsSent"), NavigateUri = new Uri(PrivacyProblems), Padding = new(0) },
                    },
                },
                problemText,
            },
        };
        Build();
        withLog.Click += (_, _) => Build();
        words.TextChanged += (_, _) => Enable();
        dialog.PrimaryButtonClick = async () =>
        {
            dialog.IsPrimaryButtonEnabled = false;
            var (text, body) = ((words.Text ?? "").Trim().ReplaceLineEndings("\n"), built);
            string? sent, why;
            try { (sent, why) = await Task.Run(() => Host.Core.SendProblemAsync(id, text, body)); }
            catch (Exception ex) { (sent, why) = (null, Describe(ex)); }
            if (sent != null) Thanked(dialog, sent);
            else
            {
                (problemText.Text, problemText.IsVisible) = (why ?? Loc.T("Detail_NotSentRetry"), true);
                dialog.IsPrimaryButtonEnabled = true;
            }
            return false;   // the popup stays for the answer
        };
        return dialog;

        static ContentDialog Thanked(ContentDialog d, string reportId)
        {
            (d.PrimaryButtonText, d.CloseButtonText) = ("", Loc.T("Common_Close"));
            d.Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = Loc.T("Detail_ReportThanks"), TextWrapping = TextWrapping.Wrap },
                    new SelectableTextBlock { Text = Loc.T("Detail_ReportId", reportId), FontWeight = FontWeight.SemiBold },
                    new TextBlock { Text = Loc.T("Detail_ReportMention"), TextWrapping = TextWrapping.Wrap, Classes = { "Secondary" } },
                },
            };
            return d;
        }
    }

    async void OnReportProblem(object? _, RoutedEventArgs __) => await App.ShowAsync(ProblemDialog());

    async void OnSendReport(object? _, RoutedEventArgs __)
    {
        var id = Vm.Row.Id;
        CompatSent? sent = null;
        try { sent = await Task.Run(() => Host.Core.ReportSent(id)); }
        catch (Exception) { }   // not known: the popup says nothing about an earlier report
        await App.ShowAsync(ReportDialog(sent));
    }

    void OnDbTeaser(object? _, Run __)
    {
        if (Host.Account?.SignedIn == true) App.Main.NavigateToPatreon();
        else App.SignInWithPatreon();
    }

    void OnStutterLink(object? _, Run __)
    {
        if (Vm.StutterUri is { } uri) _ = TopLevel.GetTopLevel(this)?.Launcher.LaunchUriAsync(uri);
    }

    async void OnOpenExeFolder(object? _, RoutedEventArgs __)
    {
        try { if (ShowExe.Open(Vm.Row.State.Game.ExePath)) return; }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException) { }
        await App.ShowAsync(new ContentDialog { Title = Loc.T("Common_CouldntOpenFolder"), Content = Loc.T("Common_FolderNotThere"), CloseButtonText = Loc.T("Common_Ok") });
    }

    async void OnGameFolder(object? _, RoutedEventArgs __)
    {
        var game = Vm.Row.State.Game;
        if (await GameFolderDialog.ShowAsync(game, Loc.T("Detail_FolderOf", game.Name), Loc.T("Common_Save")) is not { } folder) return;
        try { await Task.Run(() => Host.Core.AddManualGame(game.ExePath, folder)); }   // checks the folder again, then the game
        catch (Exception ex)
        {
            await App.ShowAsync(new ContentDialog { Title = Loc.T("Common_CouldntSetFolder"), Content = ex.Message, CloseButtonText = Loc.T("Common_Ok") });
            return;
        }
        Vm.Refresh();
    }

    async void OnGameExe(object? _, RoutedEventArgs __)
    {
        string? path;
        try
        {
            var picked = await App.Main.StorageProvider.OpenFilePickerAsync(new() { Title = Loc.T("Detail_UseThisExe"), FileTypeFilter = [new(".exe") { Patterns = ["*.exe"] }] });
            path = picked.FirstOrDefault()?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            await App.ShowAsync(new ContentDialog { Title = Loc.T("Common_CouldntOpenPicker"), Content = ex.Message, CloseButtonText = Loc.T("Common_Ok") });
            return;
        }
        if (path != null) await SetExe(path);
    }

    async void OnAutoExe(object? _, RoutedEventArgs __) => await SetExe(null);

    async Task SetExe(string? path)
    {
        var id = Vm.Row.Id;
        try { await Task.Run(() => Host.Core.SetGameExe(id, path)); }   // reads the game again
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await App.ShowAsync(new ContentDialog { Title = Loc.T("Common_CouldntChangeExe"), Content = ex.Message, CloseButtonText = Loc.T("Common_Ok") });
            return;
        }
        Vm.Refresh();
    }

    // Only the name shown: the game's id, which everything SCSKiller keeps of it is stored under, stays.
    async void OnRename(object? _, RoutedEventArgs __)
    {
        var box = new TextBox { Text = Vm.Name, MaxLength = 200 };
        var dialog = new ContentDialog
        {
            Title = Loc.T("Detail_RenameFor", Vm.Name), PrimaryButtonText = Loc.T("Detail_RenameBtn"), CloseButtonText = Loc.T("Common_Cancel"), DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 8,
                Children = { new TextBlock { Text = Loc.T("Detail_NameLabel") }, box, new TextBlock { Text = Loc.T("Detail_NameDesc"), Classes = { "Secondary" }, TextWrapping = TextWrapping.Wrap } },
            },
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = (box.Text ?? "").Trim().Length > 0;
        box.AttachedToVisualTree += (_, _) => box.SelectAll();
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return;
        try { Fmt.Rename(Vm.Row.Id, box.Text ?? ""); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await App.ShowAsync(new ContentDialog { Title = Loc.T("Common_CouldntRename"), Content = ex.Message, CloseButtonText = Loc.T("Common_Ok") });
            return;
        }
        Vm.Refresh();
        CrumbName.Text = Vm.Name;
    }

    async void OnRemoveGame(object? _, RoutedEventArgs __)
    {
        var (id, manual) = (Vm.Row.Id, Vm.IsManual);
        if (!await App.ConfirmAsync(Loc.T("Detail_RemoveFromLibraryFor", Vm.Name),
                manual ? Loc.T("Detail_ForgetConfirm") : Vm.HideNote,
                Loc.T("Common_Remove"))) return;
        // refused while a compile of it runs, and a store game while it runs or its recorder stays in
        try { await Task.Run(() => { if (manual) Host.Core.RemoveManualGame(id); else Host.Core.HideGame(id); }); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await App.ShowAsync(new ContentDialog { Title = Loc.T("Common_CouldntRemoveGame"), Content = ex.Message, CloseButtonText = Loc.T("Common_Ok") });
            return;
        }
        App.Main.Navigate(typeof(LibraryPage));
    }

    /// <summary>Screenshots: the bottom of the page; false when it all fits.</summary>
    public bool ScrollToEnd()
    {
        if (Scroller.Extent.Height <= Scroller.Viewport.Height) return false;
        Scroller.ScrollToEnd();
        return true;
    }

    /// <summary>Screenshots: the coverage card's Details and the session's slow frames open.</summary>
    public void ShowDetails() => DetailsExpander.IsExpanded = SlowFramesExpander.IsExpanded = EarlierExpander.IsExpanded = true;

    /// <summary>Screenshots: the frame times card at the top; false without one.</summary>
    public bool ScrollToFrames()
    {
        if (!Vm.HasFrames || FramesCard.TranslatePoint(default, Scroller) is not { } at) return false;
        Scroller.Offset = new(0, Math.Max(0, Scroller.Offset.Y + at.Y - 12));
        return true;
    }

    /// <summary>Screenshots: the traversal stutter fix's choice in the middle; false without it.</summary>
    public void ScrollToShared()
    {
        if (SharedSwitch.TranslatePoint(default, Scroller) is { } at) Scroller.Offset = new(0, Math.Max(0, Scroller.Offset.Y + at.Y - 300));
    }

    public bool ScrollToTraversal()
    {
        if (!Vm.ShowTraversal || TraversalBox.TranslatePoint(default, Scroller) is not { } at) return false;
        Scroller.Offset = new(0, Math.Max(0, Scroller.Offset.Y + at.Y - 400));
        return true;
    }

    void OnCrumb(object? _, RoutedEventArgs __) => App.Main.ShowLibrary();

    async void OnCompile(object? _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (vm.OffersCareful)
            try { await Task.Run(() => Host.Core.SetCarefulCompile(id, true)); }
            catch (Exception ex) { vm.Error = ex.Message; vm.Refresh(); return; }
        Host.Core.Compile(id);
        vm.Refresh();
    }

    async void OnMaxCompile(object? _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (!Host.Core.Settings.MaxCompileConfirmed)
        {
            if (!await App.ConfirmAsync(Loc.T("Detail_MaxCompileFor", vm.Name), DetailVm.MaxCompileWhy, Loc.T("Detail_MaxCompile"))) return;
            Host.Core.Settings = Host.Core.Settings with { MaxCompileConfirmed = true };
        }
        Host.Core.CompileMax(id);
        vm.Refresh();
    }

    // IsCheckedChanged also fires when the binding sets the switch: only a user's flip differs from the view model.
    void OnCarefulToggled(object? _, RoutedEventArgs __) { if (CarefulSwitch.IsChecked == true != Vm.CarefulOn) SetCareful(CarefulSwitch.IsChecked == true); }

    void OnLeftOutToggled(object? _, RoutedEventArgs __)
    {
        var on = LeftOutSwitch.IsChecked == true;
        if (on != Vm.LeftOutOn) Set((vm, p) => vm.LeftOutPending = p, on, id => Host.Core.SetGameLeftOutOfAddAll(id, on));
    }

    void OnSharedToggled(object? _, RoutedEventArgs __)
    {
        var on = SharedSwitch.IsChecked == true;
        if (on != Vm.SharedOn) Set((vm, p) => vm.SharedPending = p, on, id => Host.Core.SetGameUseShared(id, on));
    }

    void OnSharedFollow(object? _, RoutedEventArgs __) => Set((vm, p) => vm.SharedPending = p, Vm.SharedOn, id => Host.Core.SetGameUseShared(id, null));

    void SetCareful(bool on) => Set((vm, p) => vm.CarefulPending = p, on, id => Host.Core.SetCarefulCompile(id, on));

    async void OnCacheCapChanged(object? _, SelectionChangedEventArgs __)
    {
        var (vm, id, i) = (Vm, Vm.Row.Id, CacheCapBox.SelectedIndex);
        if (i < 0 || i == vm.CacheCapIndex) return;
        try
        {
            await Task.Run(() => Host.Core.SetGameCacheCap(id, i == 0 ? null : ScsKiller.CacheCapsGB[i - 1]));
            vm.Error = null;
        }
        catch (Exception ex) { vm.Error = Describe(ex); }
        vm.Refresh();
    }

    async void OnRayTracingChanged(object? _, SelectionChangedEventArgs __)
    {
        var (vm, id, i) = (Vm, Vm.Row.Id, RayTracingBox.SelectedIndex);
        if (i < 0 || i == vm.RayTracingIndex) return;
        try
        {
            await Task.Run(() => Host.Core.SetGameRayTracing(id, i switch { 1 => true, 2 => false, _ => null }));
            vm.Error = null;
        }
        catch (Exception ex) { vm.Error = Describe(ex); }
        vm.Refresh();
    }

    async void OnTraversalChanged(object? _, SelectionChangedEventArgs __)
    {
        var (vm, id, i) = (Vm, Vm.Row.Id, TraversalBox.SelectedIndex);
        if (i < 0 || i == vm.TraversalIndex) return;
        try
        {
            await Task.Run(() => Host.Core.SetGameTraversal(id, TraversalFix.OverrideAt(i)));
            vm.Error = null;
        }
        catch (Exception ex) { vm.Error = Describe(ex); }
        vm.Refresh();
    }

    async void OnNewShadersChanged(object? _, SelectionChangedEventArgs __)
    {
        var (vm, id, i) = (Vm, Vm.Row.Id, NewShadersBox.SelectedIndex);
        if (i < 0 || i == vm.NewShadersIndex) return;
        try
        {
            await Task.Run(() => Host.Core.SetNewShadersMode(id, i == 0 ? null : (NewShadersMode)(i - 1)));
            vm.Error = null;
        }
        catch (Exception ex) { vm.Error = Describe(ex); }
        vm.Refresh();
    }

    // A binding's own update selects ApiIndex: only a different pick goes to the core.
    void OnApiPicked(object? _, SelectionChangedEventArgs __)
    {
        var i = ApiBox.SelectedIndex;
        if (i < 0 || i == Vm.ApiIndex || !Vm.CanPickApi) return;
        var api = Vm.ApiAt(i);
        Set((vm, p) => vm.ApiPending = p, true, id => Host.Core.SetApiOverride(id, api));
    }

    void OnGameMaximum(object? _, RoutedEventArgs __)
    {
        var on = Vm.MaximumSuggested;
        Set((vm, p) => vm.MaximumPending = p, on, id => Host.Core.SetGameMaximum(id, on));
    }

    /// <summary>Never blank: an exception without a message shows its type, and its inner one's.</summary>
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

    async void OnClearCache(object? _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var mesa = MesaLinux.Is(Host.Core.Vendor.Caps);
        var note = Loc.T("Detail_ClearCacheNote")
            + (mesa ? Loc.T("Detail_ClearCacheMesa") : "")
            + (vm.Row.State.AntiCheat != AntiCheat.None ? Loc.T("Detail_ClearCacheAntiCheat") : "");   // VAC: as any game (ScsKiller.GameCaches)
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.T("Detail_MeasuringCaches") + "\n\n" + note };
        var ownText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var alsoOwn = new CheckBox { Content = ownText, IsVisible = false };
        _ = Measure();
        if (await App.ShowAsync(App.Confirm(Loc.T("Detail_ClearCacheFor", vm.Name), new StackPanel { Spacing = 12, Children = { text, alsoOwn } }, Loc.T("Detail_ClearCacheBtn")))
            != ContentDialogResult.Primary) return;
        var withOwn = alsoOwn.IsChecked == true;
        try { vm.Error = await Task.Run(() => Host.Core.ClearGameCache(id, withOwn)) ? null : Loc.T("Detail_NothingClearedCache"); }
        catch (Exception ex) { vm.Error = ex.Message; }
        vm.ReadCaches();
        vm.Refresh();

        // the sizes fill in while the dialog is open: reading the Windows cache folders can take seconds; Clear reads them again
        async Task Measure()
        {
            try
            {
                var all = await Task.Run(() => Host.Core.GameCaches(id, gamePrecache: true));
                static bool Own(CachePart p) => p.Name is ScsKiller.PipelinePart or ScsKiller.PrecachePart;
                var (parts, own) = (all.Where(p => !Own(p)).ToList(), all.Where(Own).ToList());
                text.Text = (parts.Count > 0 ? string.Join(" · ", parts.Select(p => $"{p.Name} {Format.Bytes(p.Bytes)}")) + ".\n\n" : Loc.T("Detail_NoCacheFiles") + ".\n\n") + note;
                ownText.Text = Loc.T("Detail_AlsoOwnCache", Format.Bytes(own.Sum(p => p.Bytes)));
                alsoOwn.IsVisible = own.Count > 0;
            }
            catch (Exception ex) { text.Text = Describe(ex) + "\n\n" + note; }
        }
    }

    async void OnBeforeAfter(object? _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        var ba = vm.Row.State.BeforeAfter;
        string? error = ba?.Stage switch
        {
            null => await BeforeAfterDialogs.StartAsync(id),
            _ when BeforeAfterUi.Voided(ba) => await BeforeAfterDialogs.StartAsync(id, over: true),
            BeforeAfterStage.BeforeArmed when ba!.ClearPending => await BeforeAfterUi.ContinueAsync(id),
            BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed => await BeforeAfterUi.LaunchAsync(id),
            BeforeAfterStage.AfterReady => await BeforeAfterUi.ContinueAsync(id),
            BeforeAfterStage.Done => await ReportWindow.OpenAsync(id) is null ? Loc.T("Ba_ReportGone") : null,
            _ => null,
        };
        Shown(vm, error);
    }

    async void OnBaRecordAgain(object? _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterDialogs.RecordAfterAgainAsync(Vm.Row.Id));
    async void OnBaStartOver(object? _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterDialogs.StartAsync(Vm.Row.Id, over: true));
    async void OnBaCancel(object? _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterDialogs.CancelAsync(Vm.Row.Id));
    async void OnBaHowItWorks(object? _, RoutedEventArgs __) => await BeforeAfterDialogs.IntroAsync(Vm.Name, BeforeAfterDialogs.Intro.HowItWorks);
    // Continue in Compiling clears and compiles again, first in the queue
    async void OnBaCompileAgain(object? _, RoutedEventArgs __) => Shown(Vm, await BeforeAfterUi.ContinueAsync(Vm.Row.Id));

    static void Shown(DetailVm vm, string? error)
    {
        vm.Error = error;
        vm.Refresh();
    }

    async void OnClearRecording(object? _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (!await App.ConfirmAsync(Loc.T("Detail_ClearRecordingFor", vm.Name),
                Loc.T("Detail_ClearRecordingBody"),
                Loc.T("Detail_ClearRecording"))) return;
        vm.ClearingRecording = true;
        vm.Refresh();
        try { vm.Error = await Task.Run(() => Host.Core.ClearRecording(id)) ? null : Loc.T("Detail_NothingClearedRecording"); }
        catch (Exception ex) { vm.Error = ex.Message; }
        vm.ClearingRecording = false;
        vm.Refresh();
    }

    void OnRecordToggled(object? _, RoutedEventArgs __)
    {
        var on = RecordSwitch.IsChecked == true;   // read here: the change runs off the UI thread, where the switch can't be read
        if (on == Vm.RecordOn) return;
        // an eligible anti-cheat game: the switch allows sessions (offline, or a confirmed session in a game with anti-cheat)
        if (Vm.ShowOffline) Set((vm, p) => vm.OfflinePending = p, on, id => Host.Core.SetOfflineRecording(id, on));
        else SetRecord(on);
    }

    // On only after the standard anti-cheat wording is confirmed, once: the core then records every launch as a session
    async void OnAlwaysToggled(object? _, RoutedEventArgs __)
    {
        var on = AlwaysSwitch.IsChecked == true;
        if (on == Vm.AlwaysOn) return;
        if (!on)
        {
            Set((vm, p) => vm.AlwaysPending = p, false, id => Host.Core.SetAlwaysRecord(id, false, confirmed: false, AntiCheat.None));
            return;
        }
        var (vm, id) = (Vm, Vm.Row.Id);
        vm.AlwaysPending = true;   // the switch stays on, and off for a second toggle, until the dialog is answered
        vm.Refresh();
        AntiCheat shown;
        try { shown = await Host.Core.CheckAntiCheatAsync(id); }
        catch (Exception ex)
        {
            (vm.Error, vm.AlwaysPending) = (Describe(ex), null);
            vm.Refresh();
            return;
        }
        var fresh = vm.Row.State with { AntiCheat = shown };   // VAC stays the state's: the check only names a client anti-cheat
        var ok = await App.ConfirmAsync(Loc.T("Detail_AlwaysRecordFor", vm.Name),
            Text(DetailVm.RiskText(fresh) + (ScsKiller.VulkanPickWarning(fresh) is { } pick ? "\n\n" + pick : "")), Loc.T("Detail_AlwaysRecord"));
        vm.AlwaysPending = null;
        if (!ok)
        {
            vm.Refresh();   // the switch shows off again
            return;
        }
        Set((v, p) => v.AlwaysPending = p, true, i => Host.Core.SetAlwaysRecord(i, true, confirmed: true, shown));
    }

    void OnRecordTip(object? _, RoutedEventArgs __) => SetRecord(true);

    void OnUseDefault(object? _, RoutedEventArgs __) => SetRecord(null);

    void SetRecord(bool? on) => Set((vm, p) => vm.RecordPending = p, on ?? Host.Core.Settings.RecordAllGames,
        id => Host.Core.SetRecorderOverride(id, on switch { true => RecorderOverride.On, false => RecorderOverride.Off, null => RecorderOverride.Default }));

    void OnAlongsideToggled(object? _, RoutedEventArgs __) { if (AlongsideSwitch.IsChecked == true != Vm.AlongsideOn) SetAlongside(AlongsideSwitch.IsChecked == true); }

    void SetAlongside(bool on) => Set((vm, p) => vm.AlongsidePending = p, on, id => Host.Core.SetRecordAlongsideMod(id, on));

    // Every launch is confirmed here: the core starts a session only with confirmed: true
    async void OnRecordOffline(object? _, RoutedEventArgs __)
    {
        var (vm, id) = (Vm, Vm.Row.Id);
        if (vm.Risk) { await RecordRisk(vm, id); return; }
        if (!await App.ConfirmAsync(Loc.T("Detail_StartOfflineFor", vm.Name), Text(DetailVm.OfflineRisk + " " + Loc.T("Detail_OfflineNoOnline")),
                Loc.T("Detail_RecordOfflineBtn"))) return;
        await StartSession(vm, () => Host.Core.StartOfflineSession(id, confirmed: true));
    }

    // A fresh check first, so the confirmation names the anti-cheat that is there now; the core refuses if it finds another.
    async Task RecordRisk(DetailVm vm, string id)
    {
        vm.OfflineStarting = true;   // before the check: the button stays off until the dialog is answered
        vm.Refresh();
        AntiCheat shown;
        try { shown = await Host.Core.CheckAntiCheatAsync(id); }
        catch (Exception ex)
        {
            (vm.Error, vm.OfflineStarting) = (Describe(ex), false);
            vm.Refresh();
            return;
        }
        var fresh = vm.Row.State with { AntiCheat = shown };   // VAC stays the state's: the check only names a client anti-cheat
        var ok = await App.ShowAsync(RiskDialog(vm, fresh)) == ContentDialogResult.Primary;
        if (!ok)
        {
            vm.OfflineStarting = false;
            vm.Refresh();
            return;
        }
        await StartSession(vm, () => Host.Core.StartRiskSession(id, confirmed: true, shown));
    }

    public ContentDialog RiskDialog(DetailVm vm, GameState fresh) => App.Confirm(Loc.T("Detail_RecordRiskFor", vm.Name),
        Text(DetailVm.RiskText(fresh) + Loc.T("Detail_RiskConfirmTail")
             + (ScsKiller.VulkanPickWarning(fresh) is { } pick ? "\n\n" + pick : "")), Loc.T("Detail_RecordSessionBtn"));

    // The start runs off the UI thread; the task it returns ends when the game exited and its files are out.
    static async Task StartSession(DetailVm vm, Func<Task> start)
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
