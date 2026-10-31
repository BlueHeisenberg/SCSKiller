using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using SCSKiller.App.Design;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;
using SCSKiller.Ui.Pages;
using SCSKiller.Ui.Platform;

namespace SCSKiller.Ui;

/// <summary>--screenshots: the design data's pages, rendered off-screen in dark and light to PNG files named as the WinUI
/// app names its own, then exit. The pages the Avalonia app doesn't have yet are left out, but the steps that change the
/// sample data on the way run all the same, so the later shots show what the WinUI ones show.</summary>
static partial class Screenshots
{
    const int Width = 1264, Height = 871, MinWidth = 1056;   // the WinUI window's client area at its default and minimum width

    public static int Run(string dir)
    {
        App.WithSelawik(AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })).SetupWithoutStarting();
        int code = 0;
        using var done = new CancellationTokenSource();
        Dispatcher.UIThread.Post(async () =>
        {
            try { await TakeAsync(dir); }
            catch (Exception e)
            {
                Console.Error.WriteLine(e);
                code = 1;
            }
            finally { done.Cancel(); }
        });
        Dispatcher.UIThread.MainLoop(done.Token);
        return code;
    }

    static async Task TakeAsync(string dir)
    {
        Directory.CreateDirectory(dir);
        var main = App.Start(screenshots: true);
        var fake = (FakeScsKiller)Host.Core;
        // built, never shown and nothing registered
        foreach (var (file, xml) in Notifications.Samples(fake)) File.WriteAllText(Path.Combine(dir, file), xml);
        ThemeVariant[] themes = [ThemeVariant.Dark, ThemeVariant.Light];
        string Tag(ThemeVariant t) => t == ThemeVariant.Light ? "light" : "dark";
        LibraryPage Library() => (LibraryPage)main.Page!;
        DetailPage Detail() => (DetailPage)main.Page!;
        void Resize(int width) => (main.Width, main.Height) = (width, Height);

        // <name>-<theme><part>.png
        async Task Shot(string name, int delay = 1000, string part = "")
        {
            await Task.Delay(delay);
            SaveFrame(main, Path.Combine(dir, $"{name}-{Tag(main.ActualThemeVariant)}{part}.png"));
        }

        // a dialog on its own, open for as long as WinUI's
        async Task Dialog(ContentDialog dialog, string name)
        {
            _ = App.ShowAsync(dialog);
            await Task.Delay(800);
            SaveDialog(main, Path.Combine(dir, $"{name}-{Tag(main.ActualThemeVariant)}.png"));
            dialog.Hide();
            await Task.Delay(300);
        }

        // An alpha build's gate, started on an empty data folder as a first run, then each refusal.
        var empty = Path.Combine(Path.GetTempPath(), $"SCSKiller-gate-{Environment.ProcessId}");
        var gate = new GateWindow(new Account(empty), empty, () => throw new InvalidOperationException("an empty data folder opened the gate"));
        gate.Show();
        await gate.StartAsync();
        if (Directory.Exists(empty)) throw new InvalidOperationException("the gate wrote to an empty data folder");
        foreach (var (name, line) in new[] { ("alpha-gate", ""), ("alpha-gate-not-backer", AlphaGate.NotBacker), ("alpha-gate-supporter", AlphaGate.SupporterOnly), ("alpha-gate-too-old", AlphaGate.TooOld) })
            foreach (var theme in themes)
            {
                gate.RequestedThemeVariant = theme;
                gate.ShowLine(line);
                await Task.Delay(400);
                SaveFrame(gate, Path.Combine(dir, $"{name}-{Tag(theme)}.png"));
            }
        gate.Close();

        // "Report a problem" on the first game: the dialog with a message typed, then its thanks. The window shows on the
        // game's page, so the Library isn't loaded yet.
        string ff7 = fake.Games[0].Game.Id;
        main.MinWidth = 0;
        Resize(Width);
        main.Navigate(typeof(DetailPage), ff7);
        main.Show();
        foreach (var theme in themes)
        {
            main.RequestedThemeVariant = theme;
            await Task.Delay(800);
            foreach (var (name, id) in new[] { ("problem", (string?)null), ("problem-thanks", "3f9c2e1a") })
                await Dialog(Detail().ProblemDialog(id, "The game still stutters in the first town after a full compile."), name);
            await Dialog(StartDialogs.Welcome(WelcomeContent.Default).Dialog, "welcome");   // the bundled text
        }

        // "Use shared shader hashes" on the game page against Settings' "Download shared shader hashes", dark only.
        // SCSKILLER_SHOTS=shared takes only these.
        var sharedWas = fake.Settings;
        main.RequestedThemeVariant = ThemeVariant.Dark;
        foreach (var (name, global, choice) in new (string, bool, bool?)[] { ("shared-follow-global-on", true, null), ("shared-follow-global-off", false, null),
                     ("shared-on-global-off", false, true), ("shared-off-global-on", true, false) })
        {
            fake.Settings = sharedWas with { UseCommunityDb = global };
            fake.SetGameUseShared(ff7, choice);
            main.Navigate(typeof(DetailPage), ff7);
            await Task.Delay(1000);
            Detail().ScrollToShared();
            await Shot(name, 600);
        }
        fake.Settings = sharedWas;
        fake.SetGameUseShared(ff7, null);
        if (Environment.GetEnvironmentVariable("SCSKILLER_SHOTS") == "shared") return;

        // The traversal stutter fix: its one-time question, Settings and the game page (FF7, then an anti-cheat game), with more
        // than 8 GB of VRAM and with 8 GB. SCSKILLER_SHOTS=traversal takes only these.
        var (gpuWas, settingsWas) = (fake.Vendor, fake.Settings);
        fake.Settings = settingsWas with { Traversal = TraversalMode.All };
        var lowVram = new FakeScsKiller.FakeAmdVendor { Gpu = new(GpuVendor.Amd, "AMD Radeon", "26.8.1 (32.0.31041.1004)", 0, 8UL << 30) };
        foreach (var (tag, vendor) in new (string, IGpuVendorBackend)[] { ("", gpuWas), ("-lowvram", lowVram) })
        {
            fake.Vendor = vendor;
            main.ShowGpu();
            foreach (var theme in themes)
            {
                main.RequestedThemeVariant = theme;
                await Dialog(StartDialogs.TraversalDialog(TraversalFix.LowVram(vendor.Gpu)), "traversal-popup" + tag);
                main.Navigate(typeof(SettingsPage));
                await Task.Delay(800);
                ((SettingsPage)main.Page!).ScrollToTraversal();
                await Shot("traversal-settings" + tag, 600);
                foreach (var (name, id) in new[] { ("traversal-detail", "steam:2909400"), ("traversal-detail-anticheat", "steam:381210") })
                {
                    main.Navigate(typeof(DetailPage), id);
                    await Task.Delay(1000);
                    Detail().ScrollToTraversal();
                    await Shot(name + tag, 600);
                }
            }
        }
        (fake.Vendor, fake.Settings) = (gpuWas, settingsWas);
        main.ShowGpu();
        if (Environment.GetEnvironmentVariable("SCSKILLER_SHOTS") == "traversal") return;

        // Shaders delivered by Advanced Shader Delivery for this driver: the game page's tag and line.
        main.Navigate(typeof(DetailPage), "xbox:Microsoft.Halcyon_8wekyb3d8bbwe");
        foreach (var theme in themes)
        {
            main.RequestedThemeVariant = theme;
            await Shot("detail-asd");
        }

        // A scan in progress: first run (nothing listed yet), its games listed before they're read, then a rescan.
        // This is the Library's first load, so it scans.
        fake.HideGames = true;
        fake.HoldScan = new();
        fake.ScanProgress = "Scanning Steam… 120 games found";
        main.RequestedThemeVariant = ThemeVariant.Dark;
        main.Navigate(typeof(LibraryPage));
        foreach (var name in new[] { "library-scanning-empty", "library-after-update-empty", "library-first-scan", "library-scanning", "library-after-update" })
        {
            if (name == "library-first-scan")
            {
                fake.ScanProgress = "Checking each game… 4 of 120";
                fake.Checking = true;
                fake.ShowGames();
            }
            else if (name == "library-scanning")
            {
                fake.ScanProgress = "Checking each game… 40 of 120";
                fake.Checking = false;
                fake.ShowGames();
            }
            fake.AfterUpdateLine = name.Contains("after-update") ? ScsKiller.AfterUpdateText : null;
            foreach (var theme in themes)
            {
                main.RequestedThemeVariant = theme;
                await Shot(name);
            }
        }
        fake.AfterUpdateLine = null;
        fake.HoldScan.SetResult();
        fake.HoldScan = null;
        fake.ScanProgress = null;
        await Task.Delay(1000);
        (fake.Checking, fake.NotChecked) = (true, true);   // HDD mode: the games past the first three never read
        fake.ShowGames();
        foreach (var theme in themes)
        {
            main.RequestedThemeVariant = theme;
            await Shot("library-hdd");
        }
        (fake.Checking, fake.NotChecked) = (false, false);
        fake.ShowGames();
        await Task.Delay(800);
        Library().ScrollTo("xbox:Microsoft.Halcyon_8wekyb3d8bbwe");   // its Advanced Shader Delivery tag
        foreach (var theme in themes)
        {
            main.RequestedThemeVariant = theme;
            await Shot("library-asd");
        }
        Library().ScrollToTop();   // the kept page would carry this scroll into the later library shots, which WinUI takes at the top
        main.RequestedThemeVariant = ThemeVariant.Dark;
        main.Navigate(typeof(QueuePage));
        await Shot("queue-empty");
        // After an update: plan checks of the compiled games, one line in the header, no rows (they stay for the queue shots).
        fake.CheckPlans(ff7, "xbox:Sample.AtomicHeart", "epic:3300000");
        foreach (var theme in themes)
        {
            main.RequestedThemeVariant = theme;
            await Shot("queue-checks");
        }
        main.RequestedThemeVariant = ThemeVariant.Dark;

        // The queue's sample: one compile done (Orcs Must Die! 3), one running at 54.8% (Stellar Blade Demo), three waiting.
        fake.Enqueue("steam:1522820");
        fake.StartQueue();
        fake.JumpTo(1);
        await Task.Delay(1200);   // the next tick finishes it and the empty queue stops
        foreach (var id in new[] { "steam:3489700", "steam:1627720", "steam:990080", "steam:1817230" }) fake.Enqueue(id);
        fake.StartQueue();
        fake.JumpTo(0.548);
        fake.MoveInQueue("steam:990080", 0);
        fake.MoveInQueue("steam:1627720", 2);
        foreach (var _ in themes)
        {
            fake.PauseQueue();
            await Task.Delay(800);
            fake.ResumeQueue();
            await Task.Delay(800);
        }

        // the Library's cache total next to the Cache page's, with no compile growing the cache between the two
        fake.PauseQueue();
        main.Navigate(typeof(LibraryPage));
        await Shot("library-total");
        main.Navigate(typeof(CachePage));
        await Shot("cache-total");
        fake.ResumeQueue();

        // The Library, the game pages and the queue in both themes. The pages the Avalonia app doesn't have yet, and the
        // shots on them, keep their time, so the queue's sample runs on as far as in the WinUI shots.
        foreach (var theme in themes)
        {
            main.RequestedThemeVariant = theme;
            foreach (var (name, page, arg) in new (string, Type, object?)[]
                     { ("library", typeof(LibraryPage), null), ("detail", typeof(DetailPage), ff7),
                       ("detail-new", typeof(DetailPage), "steam:1139900"),   // never compiled: estimated cache, Clear cache disabled
                       ("detail-stutter", typeof(DetailPage), "steam:990080"),   // stutters without a compile, with a source link
                       ("detail-kept-cache", typeof(DetailPage), "steam:999999901"),   // compiled, then marked not supported: Clear it
                       ("detail-maximum", typeof(DetailPage), "steam:3600000"),   // Maximum mode suggested for this game
                       ("detail-partial", typeof(DetailPage), "steam:990080"),   // Hogwarts Legacy: a partial plan, the recording hint
                       ("detail-compiling", typeof(DetailPage), "steam:3489700"),   // mid-compile: the queue's progress on the game page
                       ("detail-report-off", typeof(DetailPage), "xbox:Sample.AtomicHeart"),   // Rate this compile off (not played since), "did not help"
                       ("detail-similar-warning", typeof(DetailPage), "epic:3300000"),   // similar systems: ran worse
                       ("detail-runtime-warning", typeof(DetailPage), "steam:1522820"),   // the D3D12 runtime can't compile its shaders
                       ("detail-cleanup", typeof(DetailPage), "steam:3900290"),   // its D3DSCache shrank, Windows' automatic cleanup on
                       ("detail-recorder-failed", typeof(DetailPage), "steam:3900270"),   // the recorder couldn't install
                       ("detail-other-api", typeof(DetailPage), "steam:3900280"),   // on DirectX 11, its DirectX 12 compile kept
                       ("detail-vulkan", typeof(DetailPage), "steam:3900070"), ("detail-vulkan-epic", typeof(DetailPage), "epic:3900080"),   // Vulkan, nothing recorded
                       ("detail-api-unknown", typeof(DetailPage), "manual:c4d2e81f09a7b356"),   // a carved game with no API it tells: the picker
                       ("queue", typeof(QueuePage), null) })
            {
                main.Navigate(page, arg);
                await Shot(name);
            }
            await CachePages(main, Shot, Dialog);
            await SettingsPages(main, Shot);
            await AboutPages(Dialog);

            // Rate this compile's popup on FF7 (on, "removed or reduced"): this install's first, a later one, after an earlier report of the build, thanked
            main.Navigate(typeof(DetailPage), ff7);
            await Task.Delay(800);
            foreach (var (name, sent, thanks, once) in new[] { ("report", (CompatSent?)null, false, false), ("report-later", null, false, true),
                         ("report-sent", new CompatSent(CompatChoice.Better, DateTimeOffset.Now.AddDays(-4)), false, true), ("report-thanks", null, true, true) })
            {
                fake.Settings = fake.Settings with { CompatReportSent = once };
                await Dialog(Detail().ReportDialog(sent, thanks), name);
            }
            fake.Settings = fake.Settings with { CompatReportSent = false };
            main.Navigate(typeof(QueuePage));
            await Task.Delay(1000);
            ((QueuePage)main.Page!).Select("steam:990080", "steam:1627720");   // Hogwarts Legacy and Lies of P
            await Shot("queue-selected", 400);
            fake.CopyAt(0.42);
            main.Navigate(typeof(QueuePage));
            await Shot("queue-copying");
            main.Navigate(typeof(DetailPage), "steam:3489700");
            await Shot("detail-copying");
            fake.CopyPast();
            main.Navigate(typeof(QueuePage));
            await Shot("queue-writing");
            main.Navigate(typeof(DetailPage), "steam:3489700");
            await Shot("detail-writing");
            fake.CopyAt(0.71);
            main.Navigate(typeof(QueuePage));
            await Shot("queue-copying-on");
            fake.SavingFor("1 min 05 s, 1.2 GB written");
            main.Navigate(typeof(QueuePage));
            await Shot("queue-saving");
            main.Navigate(typeof(DetailPage), "steam:3489700");
            await Shot("detail-saving");
            fake.PlanAt(0.37);
            main.Navigate(typeof(QueuePage));
            await Shot("queue-planning");
            main.Navigate(typeof(DetailPage), "steam:3489700");
            await Shot("detail-planning");
            fake.JumpTo(0.548);
            // a D3DSCache fold after the game's exit: the Library's line, the queue's row and the game page's
            (fake.Folding, fake.AfterUpdateLine) = (ff7, ScsKiller.FoldText(fake.Games[0].Game.Name + "'s", 42));
            main.Navigate(typeof(LibraryPage));
            await Shot("library-folding");
            Library().SearchText = fake.Games[0].Game.Name;
            await Shot("library-folding-row");
            Library().SearchText = "";
            main.Navigate(typeof(QueuePage));
            await Shot("queue-folding");
            main.Navigate(typeof(DetailPage), ff7);
            await Shot("detail-folding");
            (fake.FoldWriting, fake.AfterUpdateLine) = (true, ScsKiller.FoldText(fake.Games[0].Game.Name + "'s", 42, true));
            main.Navigate(typeof(LibraryPage));
            await Shot("library-folding-writing");
            main.Navigate(typeof(DetailPage), ff7);
            await Shot("detail-folding-writing");
            (fake.Folding, fake.FoldWriting, fake.AfterUpdateLine) = (null, false, null);
            await SettingsPatreonCard(main, Shot);
            await AboutChannelShots(main, Shot);
        }

        // Game pages at the default width and the minimum one, top and bottom: a partial stale plan with many tags, warmed with
        // a session, never compiled, needs a recording, anti-cheat, fully covered, compiled without its ray tracing.
        foreach (var (width, size) in new[] { (Width, "wide"), (MinWidth, "min") })
        {
            Resize(width);
            foreach (var theme in themes)
            {
                main.RequestedThemeVariant = theme;
                if (width == MinWidth)
                {
                    main.Navigate(typeof(LibraryPage));
                    await Shot("library-min");
                    main.Navigate(typeof(QueuePage));
                    await Shot("queue-min");
                    await SettingsMin(main, Shot);
                }
                foreach (var (name, id) in new[] { ("hogwarts", "steam:990080"), ("ff7", ff7), ("ghostrunner", "steam:1139900"),
                                                   ("palworld", "xbox:1623730"), ("eldenring", "steam:1245620"),
                                                   ("atomicheart", "xbox:Sample.AtomicHeart"), ("darwin", "epic:3300000"), ("townfall", "steam:3600000"),
                                                   ("added", "manual:5f1c0e9a2b7d4c30"), ("tekken", "steam:1778820"), ("racer", "steam:999999901"), ("unreached", "xbox:Sample.HollowCircuit"), ("bothapis", "gaijin:warthunder"),
                                                   ("guess-both", "xbox:CoffeeStainStudios.DeepRockGalactic"),
                                                   ("guess-version", "steam:3900010"), ("guess-dx", "steam:3900020"), ("rebound", "steam:1292630"), ("no-launch", "hoyoplay:nap_global"),
                                                   ("ran-other-exe", "ubisoft:13504"), ("game-changed", "steam:3900030"), ("never-loaded", "steam:3900040"), ("other-exe", "steam:3900050"),
                                                   ("foreign", "steam:3900060"), ("foreign-on", "steam:3900060"), ("foreign-off", "steam:3900060"),
                                                   ("guess-picked", "xbox:CoffeeStainStudios.DeepRockGalactic"), ("guess-overruled", "steam:3900020"), ("sure-picked", "steam:1139900"),
                                                   ("vulkan-pick", "steam:3900170"), ("dx11-frames", "steam:3900300"),
                                                   ("learned", "steam:4115450") })
                {
                    if (name is "guess-both" or "guess-dx" or "ghostrunner") { fake.ProveApi(id, null); fake.SetApiOverride(id, null); }
                    if (name == "guess-picked") fake.SetApiOverride(id, DxApi.D3D12);
                    if (name == "foreign") fake.MakeForeign(id);
                    if (name == "foreign-on") fake.SetRecorderOverride(id, RecorderOverride.On);   // what the switch's one click calls
                    if (name == "foreign-off") fake.SetRecorderOverride(id, RecorderOverride.Off);
                    if (name == "sure-picked") fake.SetApiOverride(id, DxApi.D3D11);
                    if (name == "guess-overruled") { fake.SetApiOverride(id, DxApi.D3D11); fake.ProveApi(id, "D3D12"); }
                    main.Navigate(typeof(DetailPage), id);
                    string file = $"d-{name}-{size}";
                    await Shot(file);
                    if (Detail().ScrollToEnd()) await Shot(file, 400, "-end");
                    if (name == "dx11-frames" && Detail().ScrollToFrames()) await Shot(file, 400, "-frames");   // the frames-only recorder's graph
                    if (name == "tekken") await Dialog(Detail().PartlyWhyDialog(), $"d-tekken-why-{size}");
                    if (name == "vulkan-pick") await Dialog(Detail().RiskDialog(Detail().Vm, Detail().Vm.Row.State), $"d-vulkan-pick-confirm-{size}");
                    if (name == "ff7")   // every count, under Details
                    {
                        var open = Detail();
                        open.ShowDetails();
                        await Task.Delay(600);
                        open.ScrollToEnd();
                        await Shot(file, 400, "-details");
                        if (open.ScrollToFrames()) await Shot(file, 400, "-frames");
                    }
                }
            }
        }
        foreach (var id in new[] { "xbox:CoffeeStainStudios.DeepRockGalactic", "steam:3900020", "steam:1139900" }) { fake.ProveApi(id, null); fake.SetApiOverride(id, null); }   // the library shots: as detected again
        Resize(Width);

        // Library search ("life": one game on Steam, one on EA) and the store sections at the end of the list.
        foreach (var theme in themes)
        {
            main.RequestedThemeVariant = theme;
            main.Navigate(typeof(LibraryPage));
            var library = Library();
            foreach (var (name, search) in new[] { ("library-back", ""), ("library-search", "life"), ("library-nomatch", "zelda"), ("library-rt", "darwin"), ("library-partly", "tekken"), ("library-unsupported", "racer"), ("library-unreached", "hollow"), ("library-bothapis", "thunder"), ("library-guessed", "guessed"), ("library-offline", "elden"), ("library-nothing-recorded", "valhalla"), ("library-other-exe", "ashen"), ("library-groups", "") })
            {
                library.SearchText = search;
                await Task.Delay(400);
                if (name == "library-groups") library.ScrollToEnd();
                await Shot(name, 800);
            }
            library.Vm.ShowHidden = true;   // the sample's hidden game under the search
            await Shot("library-hidden", 800);
            library.Vm.ShowHidden = false;
            // largest cache first, then only the compiled games, then back to the default order
            library.SearchText = "";
            library.Vm.SortBy(LibrarySort.CacheDescending, LibrarySort.CacheAscending);
            await Shot("library-sort-cache", 800);
            library.Vm.ShowIndex = (int)LibraryShow.Compiled;
            await Shot("library-show-compiled", 800);
            library.Vm.ShowIndex = (int)LibraryShow.All;
            library.Vm.SortBy(LibrarySort.Status, LibrarySort.Status);
            await AboutShots(main, (name, delay) => Shot(name, delay));
        }

        // The Library's last sections at the minimum width: it kept the scroll position of library-groups.
        Resize(MinWidth);
        foreach (var theme in themes)
        {
            main.RequestedThemeVariant = theme;
            main.Navigate(typeof(LibraryPage));
            await Shot("library-min", part: "-end");
        }

        // A compile over 16 GB queued: its warning on NVIDIA past the 16 GB limit ("queue" has the cache close to it), then on
        // AMD, which the theme shots keep.
        Resize(Width);
        var sampleGpu = fake.Vendor;
        fake.Enqueue("steam:1285190");
        foreach (var (name, gpu) in new[] { ("queue-large", (GpuInfo?)null), ("queue-large-amd", new GpuInfo(GpuVendor.Amd, "AMD Radeon", "25.10.1", 0, 16UL << 30)) })
        {
            if (gpu != null) { fake.Vendor = new UnsupportedVendor(gpu); main.ShowGpu(); }
            foreach (var theme in themes)
            {
                main.RequestedThemeVariant = theme;
                main.Navigate(typeof(QueuePage));
                await Shot(name);
            }
        }

        // The theme setting over the shots' own: pages, a flyout and a dialog follow it at once
        foreach (var setting in new[] { AppTheme.Light, AppTheme.Dark })
        {
            fake.Settings = fake.Settings with { Theme = setting };
            main.ApplyTheme(setting);
            var tag = setting.ToString().ToLowerInvariant();
            main.Navigate(typeof(SettingsPage));
            await Task.Delay(1000);
            SaveFrame(main, Path.Combine(dir, $"theme-{tag}-settings.png"));
            main.Navigate(typeof(LibraryPage));
            await Task.Delay(1000);
            SaveFrame(main, Path.Combine(dir, $"theme-{tag}-library.png"));
            var check = new List<string>();
            main.Bell.ShowAt(main.BellAnchor);
            await Task.Delay(800);
            if (main.Bell.Content is Control shown) check.Add($"flyout {shown.ActualThemeVariant}");
            main.Bell.Hide();
            var dialog = new ContentDialog
            {
                Title = "Rename Sample", PrimaryButtonText = "Rename", CloseButtonText = "Cancel",
                Content = new StackPanel { Spacing = 8, Children = { new TextBlock { Text = "Name" }, new TextBox { Text = "Sample", Classes = { "clearButton" } } } },
            };
            _ = App.ShowAsync(dialog);
            await Task.Delay(800);
            check.Add($"dialog {dialog.ActualThemeVariant}");
            if (dialog.Bounds.Width > 0) SaveDialog(main, Path.Combine(dir, $"theme-{tag}-dialog.png"));
            dialog.Hide();
            await Task.Delay(300);
            File.WriteAllLines(Path.Combine(dir, $"theme-{tag}.txt"), check);
        }
        fake.Settings = fake.Settings with { Theme = null };
        main.ApplyTheme(null);

        // An Intel GPU: the Library says it can't compile there.
        fake.Vendor = new UnsupportedVendor(new GpuInfo(GpuVendor.Intel, "Intel Graphics", "32.0.101.6881", 0, 128UL << 20));
        main.ShowGpu();
        foreach (var theme in themes)
        {
            main.RequestedThemeVariant = theme;
            main.Navigate(typeof(LibraryPage));
            Library().Vm.Refresh();
            await Shot("library-intel");
        }
        await PlatformShots(main, fake, sampleGpu, name => Shot(name));

        // A Vulkan game on Intel and AMD, which have no Vulkan warm
        foreach (var (tag, gpu) in new (string, IGpuVendorBackend)[] { ("intel", fake.Vendor), ("amd", new FakeScsKiller.FakeAmdVendor()) })
        {
            fake.Vendor = gpu;
            main.ShowGpu();
            fake.VulkanOn(gpu.Caps);
            foreach (var theme in themes)
            {
                main.RequestedThemeVariant = theme;
                foreach (var (name, id) in new[] { ("detail-vulkan", "steam:3900070"), ("detail-vulkan-epic", "epic:3900080") })
                {
                    main.Navigate(typeof(DetailPage), id);
                    await Shot($"{name}-{tag}");
                }
            }
        }
        fake.VulkanOn(sampleGpu.Caps);
        await BeforeAfterPages(main, fake, sampleGpu, name => Shot(name), dir);
    }

    /// <summary>The shots of what this OS's app alone shows, in both themes.</summary>
    private static partial Task PlatformShots(MainWindow main, FakeScsKiller fake, IGpuVendorBackend sampleGpu, Func<string, Task> shot);

    /// <summary>The window's last frame. It stays referenced until saved: a frame collected while the encoder reads it frees
    /// its pixels under it.</summary>
    static void SaveFrame(TopLevel window, string path)
    {
        using var frame = window.CaptureRenderedFrame();
        frame?.Save(path, PngBitmapEncoderOptions.Default);
    }

    /// <summary>The open dialog on its own, the window size, transparent around it (as WinUI renders a ContentDialog).</summary>
    static void SaveDialog(MainWindow main, string path)
    {
        main.DialogSmoke.IsVisible = false;
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)main.Width, (int)main.Height));
        bitmap.Render(main.Dialogs);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
        main.DialogSmoke.IsVisible = true;
    }
}
