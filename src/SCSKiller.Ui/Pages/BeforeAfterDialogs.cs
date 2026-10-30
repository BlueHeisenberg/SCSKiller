using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

/// <summary>The before and after recording's dialogs, which the game page opens; the actions behind them are
/// <see cref="BeforeAfterUi"/>'s.</summary>
static class BeforeAfterDialogs
{
    public enum Intro { Start, StartOver, HowItWorks }

    /// <summary>Screenshots: the intro dialog while it's open.</summary>
    public static ContentDialog? Showing { get; private set; }

    /// <summary>The intro (doc 6.5): the steps (folded behind "How it works" once seen), the play time and the sound.
    /// Null = cancelled, or <see cref="Intro.HowItWorks"/>. <paramref name="frames11"/>: the pair puts a frames-only
    /// d3d11.dll into the game (<see cref="ScsKiller.Frames11"/>). <paramref name="on"/>: the window it shows over, the
    /// main one by default. <paramref name="max"/>: Max compile is offered for the after run.</summary>
    public static async Task<(int Minutes, bool Sound, bool Overlay, bool Max)?> IntroAsync(string game, Intro mode, bool frames11 = false, DialogLayer? on = null, bool max = false)
    {
        var s = Host.Core.Settings;
        bool full = mode == Intro.HowItWorks || !s.BeforeAfterIntroSeen;
        int minutes = 5;
        static TextBlock Line(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };
        var (step1, step3) = (Line(""), Line(""));
        void Steps() => (step1.Text, step3.Text) = (
            $"1. SCSKiller deletes this game's shader caches ({(Host.Platform.SystemShaderCache ? "the driver's, Windows' and" : "the driver's and")} the game's own) and starts the game. Load a save you can load again and play. The {minutes} minutes count from the game's first frame, loading and pauses included.",
            $"3. Load the same save and play the same {minutes} minutes.");
        Steps();
        var steps = new StackPanel
        {
            Spacing = 8, IsVisible = full,
            Children = { step1, Line("2. Close the game. SCSKiller deletes the caches again and compiles the game's shaders."), step3,
                Line("4. You get a before and after report you can save and share.") },
        };
        var how = new HyperlinkButton { Content = "How it works", Padding = new(0), IsVisible = !full };
        how.Click += (_, _) => (steps.IsVisible, how.IsVisible) = (true, false);

        var group = $"play-time-{Guid.NewGuid():N}";
        var (five, ten) = (new RadioButton { Content = "5 minutes", GroupName = group, IsChecked = true }, new RadioButton { Content = "10 minutes", GroupName = group });
        ten.IsCheckedChanged += (_, _) => { minutes = ten.IsChecked == true ? 10 : 5; Steps(); };
        var time = new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = "Play time" }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { five, ten } } },
        };
        var sound = new CheckBox { Content = "Play a sound when the time is up", IsChecked = s.BeforeAfterSound };
        var overlay = new CheckBox { Content = "Show the countdown in the game", IsChecked = s.BeforeAfterOverlay };
        ToolTip.SetTip(overlay, "A small panel at the top right of the game: the time left, your frame times and, on DirectX 12 and Vulkan, each shader compilation stutter. SCSKiller counts down here either way. It isn't shown with FSR or XeSS frame generation on.");
        var maxRun = new CheckBox { Content = "Max compile for the after run", IsChecked = s.BeforeAfterMax, IsVisible = max };
        ToolTip.SetTip(maxRun, DetailVm.MaxCompileWhy);
        var checks = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { sound, overlay, maxRun } };
        var choices = new Grid { ColumnSpacing = 24, ColumnDefinitions = new("*,Auto"), Children = { time, checks }, IsVisible = mode != Intro.HowItWorks };
        Grid.SetColumn(checks, 1);

        static TextBlock Bullet(string text, bool shown = true) => new() { Text = "• " + text, TextWrapping = TextWrapping.Wrap, Classes = { "Secondary" }, IsVisible = shown };
        var dialog = new ContentDialog
        {
            Title = mode == Intro.StartOver ? $"Start over with {game}?" : "Before and after recording",
            Content = new StackPanel
            {
                Spacing = 16, MaxWidth = 560,
                Children =
                {
                    new InfoBar
                    {
                        IsClosable = false, Severity = InfoBarSeverity.Warning,
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
            CloseButtonText = mode == Intro.HowItWorks ? "Close" : "Cancel",
            PrimaryButtonText = mode == Intro.HowItWorks ? "" : "Start the before run",
            DefaultButton = mode == Intro.HowItWorks ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        Showing = dialog;
        var result = await App.ShowAsync(dialog, on);
        Showing = null;
        if (mode == Intro.HowItWorks || result != ContentDialogResult.Primary) return null;
        Host.Core.Settings = Host.Core.Settings with { BeforeAfterIntroSeen = true, BeforeAfterSound = sound.IsChecked == true, BeforeAfterOverlay = overlay.IsChecked == true, BeforeAfterMax = max ? maxRun.IsChecked == true : s.BeforeAfterMax };
        return (minutes, sound.IsChecked == true, overlay.IsChecked == true, max && maxRun.IsChecked == true);
    }

    /// <summary>The intro, then the before run: the caches cleared and the game started through its store. The error to
    /// show, or null (also when cancelled).</summary>
    public static async Task<string?> StartAsync(string id, bool over = false, DialogLayer? on = null)
    {
        var g = Host.Core.Games.First(x => x.Game.Id == id);
        bool frames11 = ScsKiller.Frames11(g.Engine, Host.Core.Vendor.Caps);
        bool max = ScsKiller.MaxCompileAdds(Host.Core.Vendor.Caps, g.Engine);
        if (await IntroAsync(g.Game.Name, over ? Intro.StartOver : Intro.Start, frames11, on, max) is not { } choice) return null;
        return await BeforeAfterUi.BeginAsync(id, choice.Minutes, choice.Overlay, over, choice.Max);
    }

    public static async Task<string?> RecordAfterAgainAsync(string id, DialogLayer? on = null)
    {
        var dialog = new ContentDialog
        {
            Title = "Record the after run again?",
            Content = "SCSKiller clears the caches and compiles again first, so the new run isn't helped by the last one. Then play the same save again.",
            PrimaryButtonText = "Record again", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary,
        };
        if (await App.ShowAsync(dialog, on) != ContentDialogResult.Primary) return null;
        return await BeforeAfterUi.RecordAfterAgainAsync(id);
    }

    /// <summary>Cancel in progress: the caches were cleared, so "Compile now" is the default.</summary>
    public static async Task<string?> CancelAsync(string id)
    {
        var dialog = new ContentDialog
        {
            Title = "Cancel the before and after recording?",
            Content = "Its shader caches were cleared, so the game stutters until it's compiled again.",
            PrimaryButtonText = "Compile now", SecondaryButtonText = "Don't compile", CloseButtonText = "Keep going", DefaultButton = ContentDialogButton.Primary,
        };
        var result = await App.ShowAsync(dialog);
        if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary)) return null;
        return await BeforeAfterUi.EndAsync(id, compile: result == ContentDialogResult.Primary);
    }
}
