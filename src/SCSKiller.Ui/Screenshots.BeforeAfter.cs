using Avalonia.Media.Imaging;
using Avalonia.Styling;
using SCSKiller.App.Design;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Pages;

namespace SCSKiller.Ui;

static partial class Screenshots
{
    static async Task BeforeAfterPages(MainWindow main, FakeScsKiller fake, IGpuVendorBackend sampleGpu, Func<string, Task> shot, string dir)
    {
        const string lop = "steam:1139900";   // Ghostrunner: not queued
        // Before and after: the game page's states, the intro, the Library's chips, the report window and its image.
        fake.Vendor = sampleGpu;
        main.ShowGpu();
        main.Width = Width;
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            main.RequestedThemeVariant = theme;
            foreach (var (name, info) in new (string, BeforeAfterInfo?)[]
                     {
                         ("none", null), ("armed", new(BeforeAfterStage.BeforeArmed, 5, null, 0)),
                         ("clear-pending", new(BeforeAfterStage.BeforeArmed, 5, "Couldn't clear the cache: a file is in use", 0, ClearPending: true)), ("playing", new(BeforeAfterStage.BeforePlaying, 5, null, 78)),
                         ("before-oldcache", new(BeforeAfterStage.BeforeArmed, 5, ScsKiller.OldCacheNote, 0)),
                         ("compiling", new(BeforeAfterStage.Compiling, 5, null, 0)),
                         ("compile-stopped", new(BeforeAfterStage.Compiling, 5, "The compile was stopped. Compile again to go on.", 0)),
                         ("after-ready", new(BeforeAfterStage.AfterReady, 5, null, 0)),
                         ("after-short", new(BeforeAfterStage.AfterArmed, 5, "The after recording was 2 min of play. Play at least 3, the game compiles again first.", 0)),
                         ("timesup", new(BeforeAfterStage.AfterPlaying, 5, null, 305)), ("done", new(BeforeAfterStage.Done, 5, null, 0)),
                         ("voided", new(BeforeAfterStage.AfterReady, 5, ScsKiller.DriverVoidNote, 0)),
                     })
            {
                fake.SetBeforeAfter(lop, info);
                main.Navigate(typeof(DetailPage), lop);
                await shot("ba-" + name);
            }
        }
        main.Width = MinWidth;
        main.RequestedThemeVariant = ThemeVariant.Dark;
        foreach (var (name, info) in new (string, BeforeAfterInfo)[]
                 {
                     ("playing", new(BeforeAfterStage.BeforePlaying, 10, null, 78)), ("armed", new(BeforeAfterStage.BeforeArmed, 5, null, 0)),
                     ("done", new(BeforeAfterStage.Done, 5, null, 0)), ("compile-stopped", new(BeforeAfterStage.Compiling, 5, "The compile was stopped. Compile again to go on.", 0)),
                     ("before-oldcache", new(BeforeAfterStage.BeforeArmed, 5, ScsKiller.OldCacheNote, 0)),
                 })
        {
            fake.SetBeforeAfter(lop, info);
            main.Navigate(typeof(DetailPage), lop);
            await shot($"ba-{name}-min");   // the action row wraps
        }
        main.Width = Width;
        // no pair, and one can't start: the button greyed, the reason under it
        fake.SetBeforeAfter(lop, null);
        foreach (var (name, why) in new[]
                 {
                     ("recorder-off", "Turn the recorder on for this game first"), ("recorder-cant", $"The recorder can't run in this game: {ScsKiller.SkipForeignDll}"),
                     ("api", "Only for DirectX 12 and DirectX 11 games"), ("anti-cheat", "Not for games with anti-cheat"),
                     ("no-stutter", "Not for games without shader compilation stutter"), ("refused", ScsKiller.NeverSawNote),
                     ("running", "Ghostrunner is running"), ("compiling", "Wait for the compile to finish"), ("shared", "Its driver cache is shared with Hi-Fi RUSH"),
                 })
        {
            fake.BaBlocked[lop] = why;
            main.Navigate(typeof(DetailPage), lop);
            await shot("ba-blocked-" + name);
        }
        fake.BaBlocked.Remove(lop);
        main.Navigate(typeof(DetailPage), "steam:3200000");   // Unsupported: the status reason
        await shot("ba-blocked-status");

        // the intro: first time (the steps), then once seen (folded behind "How it works")
        fake.SetBeforeAfter(lop, null);
        main.Navigate(typeof(DetailPage), lop);
        await Task.Delay(800);
        foreach (var (name, seen) in new[] { ("ba-intro", false), ("ba-intro-seen", true) })
        {
            fake.Settings = fake.Settings with { BeforeAfterIntroSeen = seen };
            _ = BeforeAfterDialogs.IntroAsync("Ghostrunner", BeforeAfterDialogs.Intro.Start);
            await Task.Delay(1000);
            if (BeforeAfterDialogs.Showing is { } dialog)
            {
                SaveDialog(main, Path.Combine(dir, $"{name}-dark.png"));
                dialog.Hide();
            }
            await Task.Delay(400);
        }

        // the Library's chips, three games at three stages
        main.RequestedThemeVariant = ThemeVariant.Dark;
        fake.SetBeforeAfter("steam:1627720", new(BeforeAfterStage.AfterReady, 5, null, 0));
        fake.SetBeforeAfter("steam:1139900", new(BeforeAfterStage.Compiling, 5, null, 0));
        fake.SetBeforeAfter("steam:3489700", new(BeforeAfterStage.AfterPlaying, 5, null, 100));
        main.Navigate(typeof(LibraryPage));
        await Task.Delay(600);
        ((LibraryPage)main.Page!).ScrollTo("steam:3489700");
        await shot("ba-library");

        // The report window in both themes, top and bottom, and its image
        fake.SetBeforeAfter(lop, new(BeforeAfterStage.Done, 5, null, 0));
        if (await ReportWindow.OpenAsync(lop) is not { } report) throw new InvalidOperationException("no report");
        foreach (var theme in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            report.RequestedThemeVariant = theme;
            report.ScrollToTop();
            await Task.Delay(1000);
            var t = theme == ThemeVariant.Light ? "light" : "dark";
            SaveFrame(report, Path.Combine(dir, $"ba-report-{t}.png"));
            report.ScrollToEnd();
            await Task.Delay(600);
            SaveFrame(report, Path.Combine(dir, $"ba-report-{t}-end.png"));
        }
        using (var card = report.CardImage()) card.Save(Path.Combine(dir, "ba-card.png"), PngBitmapEncoderOptions.Default);
        report.Close();
    }
}
