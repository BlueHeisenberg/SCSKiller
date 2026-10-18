using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SCSKiller.Core;
using SCSKiller.Core.Games;

namespace SCSKiller.App;

/// <summary>The before and after recording's dialogs and actions, shared by the game page and the report window, and the
/// app-wide watch: a sound once a run's play time is reached, the report window when a pair turns Done.</summary>
static class BeforeAfterUi
{
    public enum Intro { Start, StartOver, HowItWorks }

    /// <summary>Screenshots: the intro dialog while it's open.</summary>
    public static ContentDialog? Showing { get; private set; }

    /// <summary>The intro (doc 6.5): the steps (folded behind "How it works" once seen), the play time and the sound.
    /// Null = cancelled, or <see cref="Intro.HowItWorks"/>. <paramref name="frames11"/>: the pair puts a frames-only
    /// d3d11.dll into the game (<see cref="Core.App.ScsKiller.Frames11"/>).</summary>
    public static async Task<(int Minutes, bool Sound, bool Overlay)?> IntroAsync(XamlRoot root, string game, Intro mode, bool frames11 = false)
    {
        var s = App.Core.Settings;
        bool full = mode == Intro.HowItWorks || !s.BeforeAfterIntroSeen;
        int minutes = 5;
        TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
        var (step1, step3) = (Line(""), Line(""));
        void Steps() => (step1.Text, step3.Text) = (
            $"1. SCSKiller deletes this game's shader caches (the driver's, Windows' and the game's own) and starts the game. Load a save you can load again and play. The {minutes} minutes count from the game's first frame, loading and pauses included.",
            $"3. Load the same save and play the same {minutes} minutes.");
        Steps();
        var steps = new StackPanel
        {
            Spacing = 8, Visibility = full ? Visibility.Visible : Visibility.Collapsed,
            Children = { step1, Line("2. Close the game. SCSKiller deletes the caches again and compiles the game's shaders."), step3,
                Line("4. You get a before and after report you can save and share.") },
        };
        var how = new HyperlinkButton { Content = "How it works", Padding = new Thickness(0), Visibility = full ? Visibility.Collapsed : Visibility.Visible };
        how.Click += (_, _) => (steps.Visibility, how.Visibility) = (Visibility.Visible, Visibility.Collapsed);

        var time = new RadioButtons { Header = "Play time", MaxColumns = 2, Items = { "5 minutes", "10 minutes" }, SelectedIndex = 0 };
        time.SelectionChanged += (_, _) => { minutes = time.SelectedIndex == 1 ? 10 : 5; Steps(); };
        var sound = new CheckBox { Content = "Play a sound when the time is up", IsChecked = s.BeforeAfterSound };
        var overlay = new CheckBox { Content = "Show the countdown in the game", IsChecked = s.BeforeAfterOverlay };
        ToolTipService.SetToolTip(overlay, "A small panel at the top right of the game: the time left, your frame times and, on DirectX 12, each shader compilation stutter. SCSKiller counts down here either way. It isn't shown with FSR or XeSS frame generation on.");
        var checks = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { sound, overlay } };
        var choices = new Grid { ColumnSpacing = 24, ColumnDefinitions = { new(), new() { Width = GridLength.Auto } }, Children = { time, checks } };
        Grid.SetColumn(checks, 1);
        if (mode == Intro.HowItWorks) choices.Visibility = Visibility.Collapsed;

        TextBlock Bullet(string text, bool shown = true) => new()
        {
            Text = "• " + text, TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Secondary"],
            Visibility = shown ? Visibility.Visible : Visibility.Collapsed,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = mode == Intro.StartOver ? $"Start over with {game}?" : "Before and after recording",
            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Spacing = 16, MaxWidth = 560,
                    Children =
                    {
                        new InfoBar
                        {
                            IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning,
                            Message = "This also deletes the game's own shader cache, which isn't backed up and can be 2 GB. The game rebuilds it, which can take minutes.",
                        },
                        steps, how, choices,
                        new StackPanel
                        {
                            Spacing = 6,
                            Children =
                            {
                                Bullet("Don't change graphics settings between the two runs."),
                                Bullet("Your current compile for this game is deleted, it's compiled again in step 2."),
                                Bullet("A small d3d11.dll goes next to the game for this recording only, then it's removed.", frames11),
                                Bullet("Frame times stay on this PC. With sharing on, the usual anonymous hashes are sent."),
                            },
                        },
                    },
                },
            },
            CloseButtonText = mode == Intro.HowItWorks ? "Close" : "Cancel",
            PrimaryButtonText = mode == Intro.HowItWorks ? "" : "Start the before run",
            DefaultButton = mode == Intro.HowItWorks ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        Showing = dialog;
        var result = await App.ShowAsync(dialog);
        Showing = null;
        if (mode == Intro.HowItWorks || result != ContentDialogResult.Primary) return null;
        App.Core.Settings = App.Core.Settings with { BeforeAfterIntroSeen = true, BeforeAfterSound = sound.IsChecked == true, BeforeAfterOverlay = overlay.IsChecked == true };
        return (minutes, sound.IsChecked == true, overlay.IsChecked == true);
    }

    /// <summary>The intro, then the before run: the caches cleared (off the UI thread, it deletes files) and the game
    /// started through its store. The error to show, or null (also when cancelled).</summary>
    public static async Task<string?> StartAsync(XamlRoot root, string id, bool over = false)
    {
        var g = Game(id);
        bool frames11 = Core.App.ScsKiller.Frames11(g.Engine, App.Core.Vendor.Caps);
        if (await IntroAsync(root, g.Game.Name, over ? Intro.StartOver : Intro.Start, frames11) is not { } choice) return null;
        try
        {
            await Task.Run(() =>
            {
                if (over) App.Core.EndBeforeAfter(id, compile: false);
                App.Core.StartBeforeAfter(id, choice.Minutes, choice.Overlay);
            });
        }
        catch (Exception e) { return Describe(e); }
        return await LaunchAsync(id);
    }

    /// <summary>Starts the game for an armed run once its recorder is in and armed (off the UI thread: it may install it); a
    /// store SCSKiller can't start: the user starts it, the watcher sees it.</summary>
    public static async Task<string?> LaunchAsync(string id)
    {
        try { await Task.Run(() => App.Core.BeforeAfterRecorderReady(id)); }
        catch (Exception e) { return Describe(e); }
        return Game(id) is var g && Launchable(g) ? Fmt.Play(g) : null;
    }

    /// <summary>Play (the library's, the game page's): an armed run's launch goes through <see cref="LaunchAsync"/>'s check.</summary>
    public static async Task<string?> PlayAsync(GameState g)
    {
        await Task.Run(() => App.Core.PutBackKept(g.Game.Id));
        return g.BeforeAfter is { Stage: BeforeAfterStage.BeforeArmed or BeforeAfterStage.AfterArmed, ClearPending: false } ? await LaunchAsync(g.Game.Id) : Fmt.Play(g);
    }

    public static bool Launchable(GameState g) => g.AntiCheat == AntiCheat.None && !Core.App.ScsKiller.IsUnchecked(g) && StoreLaunch.Supported(g.Game);   // unchecked: its anti-cheat isn't known

    /// <summary>AfterReady's Continue: arms the after run and starts the game, or (the game ran outside the flow) compiles first.
    /// BeforeArmed with a clear that failed: clears again and, cleared, starts the game.</summary>
    public static async Task<string?> ContinueAsync(string id)
    {
        try { await Task.Run(() => App.Core.ContinueBeforeAfter(id)); }
        catch (Exception e) { return Describe(e); }
        return Game(id).BeforeAfter is { Stage: BeforeAfterStage.AfterArmed } or { Stage: BeforeAfterStage.BeforeArmed, ClearPending: false } ? await LaunchAsync(id) : null;
    }

    public static async Task<string?> RecordAfterAgainAsync(XamlRoot root, string id)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = "Record the after run again?",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "SCSKiller clears the caches and compiles again first, so the new run isn't helped by the last one. Then play the same save again." },
            PrimaryButtonText = "Record again", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return null;
        try { await Task.Run(() => App.Core.RecordAfterAgain(id)); return null; }
        catch (Exception e) { return Describe(e); }
    }

    /// <summary>Cancel in progress: the caches were cleared, so "Compile now" is the default.</summary>
    public static async Task<string?> CancelAsync(XamlRoot root, string id)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = "Cancel the before and after recording?",
            Content = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Its shader caches were cleared, so the game stutters until it's compiled again." },
            PrimaryButtonText = "Compile now", SecondaryButtonText = "Don't compile", CloseButtonText = "Keep going", DefaultButton = ContentDialogButton.Primary,
        };
        var result = await App.ShowAsync(dialog);
        if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary)) return null;
        try { await Task.Run(() => App.Core.EndBeforeAfter(id, compile: result == ContentDialogResult.Primary)); return null; }
        catch (Exception e) { return Describe(e); }
    }

    /// <summary>A driver or game update since the before run: only "Start over" (or ending it) is left.</summary>
    public static bool Voided(BeforeAfterInfo? i) => i?.Problem is Core.App.ScsKiller.DriverVoidNote or Core.App.ScsKiller.BuildVoidNote;

    static GameState Game(string id) => App.Core.Games.First(g => g.Game.Id == id);

    public static string Describe(Exception e) => !string.IsNullOrWhiteSpace(e.Message) ? e.Message : e.GetType().Name;

    // the stage last seen per game, and whether its run's sound played
    static readonly Dictionary<string, (BeforeAfterStage? Stage, bool Sounded)> seen = [];

    public static void Watch()
    {
        foreach (var g in App.Core.Games) seen[g.Game.Id] = (g.BeforeAfter?.Stage, false);
        App.Core.GameChanged += g => App.Main.DispatcherQueue.TryEnqueue(() => OnChanged(g));
    }

    static void OnChanged(GameState g)
    {
        var (id, now) = (g.Game.Id, g.BeforeAfter);
        bool had = seen.TryGetValue(id, out var was);
        bool sounded = had && was.Stage == now?.Stage && was.Sounded;
        if (now is { Stage: BeforeAfterStage.BeforePlaying or BeforeAfterStage.AfterPlaying } p && !sounded && p.PlayedSeconds >= p.Minutes * 60)
        {
            sounded = true;
            if (App.Core.Settings.BeforeAfterSound) MessageBeep(MB_ICONASTERISK);   // the system's Asterisk sound, async, plays over a game
        }
        seen[id] = (now?.Stage, sounded);
        if (had && was.Stage is not (null or BeforeAfterStage.Done) && now?.Stage == BeforeAfterStage.Done) _ = ReportWindow.OpenAsync(id);
    }

    const uint MB_ICONASTERISK = 0x40;
    [DllImport("user32.dll")] static extern bool MessageBeep(uint type);
}
