using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using SCSKiller.Core.App;
using SCSKiller.Core.Localization;
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
            Loc.F("Intro_Step1", Host.Platform.SystemShaderCache ? Loc.T("Intro_Caches_System") : Loc.T("Intro_Caches_Driver"), minutes),
            Loc.F("Intro_Step3", minutes));
        Steps();
        var steps = new StackPanel
        {
            Spacing = 8, IsVisible = full,
            Children = { step1, Line(Loc.T("Intro_Step2")), step3,
                Line(Loc.T("Intro_Step4")) },
        };
        var how = new HyperlinkButton { Content = Loc.T("Intro_HowItWorks"), Padding = new(0), IsVisible = !full };
        how.Click += (_, _) => (steps.IsVisible, how.IsVisible) = (true, false);

        var group = $"play-time-{Guid.NewGuid():N}";
        var (five, ten) = (new RadioButton { Content = Loc.T("Intro_FiveMinutes"), GroupName = group, IsChecked = true }, new RadioButton { Content = Loc.T("Intro_TenMinutes"), GroupName = group });
        ten.IsCheckedChanged += (_, _) => { minutes = ten.IsChecked == true ? 10 : 5; Steps(); };
        var time = new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = Loc.T("Intro_PlayTime") }, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { five, ten } } },
        };
        var sound = new CheckBox { Content = Loc.T("Intro_SoundCheck"), IsChecked = s.BeforeAfterSound };
        var overlay = new CheckBox { Content = Loc.T("Intro_OverlayCheck"), IsChecked = s.BeforeAfterOverlay };
        ToolTip.SetTip(overlay, Loc.T("Intro_OverlayTip"));
        var maxRun = new CheckBox { Content = Loc.T("Intro_MaxRunCheck"), IsChecked = s.BeforeAfterMax, IsVisible = max };
        ToolTip.SetTip(maxRun, DetailVm.MaxCompileWhy);
        var checks = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Children = { sound, overlay, maxRun } };
        var choices = new Grid { ColumnSpacing = 24, ColumnDefinitions = new("*,Auto"), Children = { time, checks }, IsVisible = mode != Intro.HowItWorks };
        Grid.SetColumn(checks, 1);

        static TextBlock Bullet(string text, bool shown = true) => new() { Text = "• " + text, TextWrapping = TextWrapping.Wrap, Classes = { "Secondary" }, IsVisible = shown };
        var dialog = new ContentDialog
        {
            Title = mode == Intro.StartOver ? Loc.F("Intro_Title_StartOver", game) : Loc.T("Intro_Title_Start"),
            Content = new StackPanel
            {
                Spacing = 16, MaxWidth = 560,
                Children =
                {
                    new InfoBar
                    {
                        IsClosable = false, Severity = InfoBarSeverity.Warning,
                        Message = Loc.T("Intro_InfoBar"),
                    },
                    steps, how, choices,
                    new StackPanel
                    {
                        Spacing = 6,
                        Children =
                        {
                            Bullet(Loc.T("Intro_Bullet_Graphics")),
                            Bullet(Loc.T("Intro_Bullet_Deleted")),
                            Bullet(Loc.T("Intro_Bullet_D3D11"), frames11),
                            Bullet(Loc.T("Intro_Bullet_Frames")),
                        },
                    },
                },
            },
            CloseButtonText = mode == Intro.HowItWorks ? Loc.T("Common_Close") : Loc.T("Common_Cancel"),
            PrimaryButtonText = mode == Intro.HowItWorks ? "" : Loc.T("Intro_StartBeforeRunBtn"),
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
            Title = Loc.T("Ba_Dialog_RecordAgainTitle"),
            Content = Loc.T("Ba_Dialog_RecordAgainContent"),
            PrimaryButtonText = Loc.T("Ba_Dialog_RecordAgainBtn"), CloseButtonText = Loc.T("Common_Cancel"), DefaultButton = ContentDialogButton.Primary,
        };
        if (await App.ShowAsync(dialog, on) != ContentDialogResult.Primary) return null;
        return await BeforeAfterUi.RecordAfterAgainAsync(id);
    }

    /// <summary>Cancel in progress: the caches were cleared, so "Compile now" is the default.</summary>
    public static async Task<string?> CancelAsync(string id)
    {
        var dialog = new ContentDialog
        {
            Title = Loc.T("Ba_Dialog_CancelTitle"),
            Content = Loc.T("Ba_Dialog_CancelContent"),
            PrimaryButtonText = Loc.T("Ba_Dialog_CompileNowBtn"), SecondaryButtonText = Loc.T("Ba_Dialog_DontCompileBtn"), CloseButtonText = Loc.T("Ba_Dialog_KeepGoingBtn"), DefaultButton = ContentDialogButton.Primary,
        };
        var result = await App.ShowAsync(dialog);
        if (result is not (ContentDialogResult.Primary or ContentDialogResult.Secondary)) return null;
        return await BeforeAfterUi.EndAsync(id, compile: result == ContentDialogResult.Primary);
    }
}
