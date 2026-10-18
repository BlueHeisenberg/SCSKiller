using System.Globalization;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

public class FormatTests
{
    [Fact]
    public void Bytes_are_whole_megabytes_below_a_gigabyte_and_one_decimal_from_there()
    {
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            Assert.Equal(["—", "0 MB", "512 MB", "1024 MB", "1 GB", "3.2 GB"],
                new long?[] { null, 0, 512L << 20, (1L << 30) - 1, 1L << 30, (long)(3.2 * (1L << 30)) }.Select(Format.Bytes));
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void The_tray_status_says_what_compiles_and_how_far()
    {
        var p = new WarmProgress(548, 1000, 0, 100);
        Assert.Equal(("Idle", (double?)null), Format.TrayStatus(null, null, quitting: false));
        Assert.Equal(("Compiling Game, 55%", (double?)0.548), Format.TrayStatus(new("g", QueueStage.Warming, p, null), "Game", quitting: false));
        Assert.Equal(("Paused: Game", (double?)0.548), Format.TrayStatus(new("g", QueueStage.Paused, p, null), "Game", quitting: false));
        Assert.Equal(("Preparing Game", (double?)null), Format.TrayStatus(new("g", QueueStage.Planning, null, null), "Game", quitting: false));
        Assert.Equal("Checking games for more to compile", Format.TrayStatus(new("g", QueueStage.Warming, p, null, PlanCheck: true), "Game", quitting: false).Line);
        Assert.Equal(("Finishing: the driver is saving the shader cache", (double?)null), Format.TrayStatus(new("g", QueueStage.Warming, p, null), "Game", quitting: true));
    }

    [Fact]
    public void A_left_click_on_the_tray_icon_opens_once_and_a_right_click_shows_the_menu()
    {
        const uint up = 0x202, dbl = 0x203, rup = 0x205, move = 0x200;
        var clicks = new TrayClicks(500);
        Assert.Equal(TrayAction.Open, clicks.On(up, 10_000));
        Assert.Equal([TrayAction.None, TrayAction.None], new[] { clicks.On(dbl, 10_150), clicks.On(up, 10_250) });   // the rest of a double click
        Assert.Equal(TrayAction.Open, clicks.On(up, 11_000));
        Assert.Equal(TrayAction.Open, clicks.On(dbl, 12_000));   // a double click without the click before it
        Assert.Equal([TrayAction.Menu, TrayAction.None], new[] { clicks.On(rup, 13_000), clicks.On(move, 13_100) });
    }

    [Fact]
    public void Left_out_says_why_and_what_playing_with_recording_on_does()
    {
        var was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            static PlanStats P(long uncovered, long leftOut) => new(0, 41_422, 41_422, 105, false, Uncovered: uncovered, StageSets: 224_279, LeftOut: leftOut);
            Assert.Equal("96,086 (not seen in a recording yet)", ScsKiller.LeftOutValue(P(0, 96_086)));
            Assert.Equal("1,000 (400 for shader slots SCSKiller can't rebuild, 600 not seen in a recording yet)", ScsKiller.LeftOutValue(P(400, 1_000)));
            Assert.Equal("182,857 (shader slots SCSKiller can't rebuild)", ScsKiller.LeftOutValue(P(182_857, 182_857)));
        }
        finally { CultureInfo.CurrentCulture = was; }
        Assert.Equal("SCSKiller hasn't seen how the game sets the rest up yet. Keep playing with recording on. What it sees is added the next time it compiles.",
            ScsKiller.NotSeenNote(recordOn: true));
        Assert.Equal("SCSKiller hasn't seen how the game sets the rest up yet. Playing longer with recording on teaches it.", ScsKiller.NotSeenNote(recordOn: false));
        Assert.Equal("The rest use shader slots this game's engine adds. Keep playing with recording on. They're rebuilt the next time it compiles.",
            ScsKiller.EngineSlotsNote(recordOn: true));
        Assert.Equal("The rest use shader slots this game's engine adds. A 5-minute recording lets SCSKiller rebuild them.", ScsKiller.EngineSlotsNote(recordOn: false));
    }

    [Fact]
    public void A_duration_is_ScsKillers_or_a_dash()
    {
        Assert.Equal(Format.Dash, Format.Duration(null));
        Assert.Equal(ScsKiller.Duration(TimeSpan.FromMinutes(125)), Format.Duration(TimeSpan.FromMinutes(125)));
    }

    [Fact]
    public void An_item_runs_from_indexing_until_it_finishes_paused_included()
    {
        QueueStage[] running = [QueueStage.Indexing, QueueStage.Planning, QueueStage.Materializing, QueueStage.Warming, QueueStage.Paused];
        Assert.All(Enum.GetValues<QueueStage>(), s => Assert.Equal(running.Contains(s), Format.Running(new QueueItem("g", s, null, null))));
    }

    [Fact]
    public void A_middleware_tag_says_what_is_compiled()
    {
        Assert.Equal("FSR4: 80 known pipelines, compiled with the game", Format.Middleware(new("FSR4", ["amdxcffx64.dll"], 80)));
        Assert.Equal("DLSS: compiled by the NVIDIA driver itself", Format.Middleware(new("DLSS", ["nvngx_dlss.dll"], 0)));
        Assert.Equal("XeSS: detected, added after a recording sees them", Format.Middleware(new("XeSS", ["libxess.dll"], 0)));
    }

    static readonly EngineInfo Ue = new("Unreal", "4.27", null, "D3D12", false, null);

    static GameState S(GameStatus status, string reason, EngineInfo? engine = null, AntiCheat ac = AntiCheat.None) =>
        new(new Game("steam:1", "Game", Store.Steam, @"X:\g", @"X:\g\g.exe"), engine ?? Ue, ac, status, reason,
            null, null, null, null, "610.88", null, null, false, null);

    /// <summary>"Not supported yet · An app update is needed for support": an unsupported game's page and Library tooltip
    /// add no notes to its reason, not a shader mod's; other statuses keep them.</summary>
    [Fact]
    public void An_unsupported_game_shows_its_reason_alone()
    {
        var unsupported = S(GameStatus.Unsupported, "An app update is needed for support") with { ShaderMod = "RenoDX" };
        Assert.Equal("", Format.ModNote(unsupported));

        var warmed = unsupported with { Status = GameStatus.Warmed };
        Assert.StartsWith("\nRenoDX ", Format.ModNote(warmed));
    }

    /// <summary>A VAC game counts as an anti-cheat game (its page's chip names VAC, never "No anti-cheat").</summary>
    [Fact]
    public void A_vac_game_has_anti_cheat_named_vac()
    {
        var vac = S(GameStatus.Unsupported, "needs a recording, which VAC blocks") with { Vac = true };
        Assert.Equal((true, "VAC"), (vac.AnyAntiCheat, ScsKiller.AntiCheatName(vac)));
        Assert.False((vac with { Vac = false }).AnyAntiCheat);
        Assert.True((vac with { AntiCheat = AntiCheat.Other, Vac = false }).AnyAntiCheat);
    }

    /// <summary>A Library row's note is a few words for each reason the core gives (the whole one stays the tooltip's).</summary>
    [Fact]
    public void A_row_note_is_a_short_label_of_the_reason()
    {
        const string packed = "no raw DXBC/DXIL shaders in its files (0.5 GB sampled): shaders are compressed or packed: needs an engine reader";
        var cases = new (GameState State, string? Note)[]
        {
            (S(GameStatus.Ready, Core.Planning.Planner.NoRecording), null),
            (S(GameStatus.Ready, "planned from a recording"), null),
            (S(GameStatus.Ready, Core.Planning.Planner.Untested), "Not tested on this engine version"),
            (S(GameStatus.Ready, "compiles every DirectX 11 shader"), "DirectX 11"),
            (S(GameStatus.Ready, "compiles every DirectX 11 shader but the vertex shaders; " + Core.Planning.Planner.D3D11VsRecord), "DirectX 11"),
            (S(GameStatus.Ready, "no recording needed; also compiles every DirectX 11 shader (the game may run on either)"), "DirectX 11 and 12"),
            (S(GameStatus.Ready, "compiles every DirectX 11 shader (the game may run on either); for DirectX 12, turn on recording and play for about 5 minutes"), "DirectX 11, DirectX 12 needs a recording"),
            (S(GameStatus.Ready, "compiles every DirectX 11 shader (the game may run on either); for DirectX 12, needs a recording, which its anti-cheat blocks", ac: AntiCheat.Other), "DirectX 11, DirectX 12 blocked by anti-cheat"),
            (S(GameStatus.Ready, "no recording needed; ray-traced effects aren't compiled: they need a recording, which BattlEye blocks", ac: AntiCheat.BattlEye), "Ray tracing blocked by BattlEye"),
            (S(GameStatus.NeedsRecording, Core.Planning.Planner.Record), "Turn on Record and play"),
            (S(GameStatus.NeedsRecording, Core.Planning.Planner.Record) with { RecorderSkip = ScsKiller.SkipForeignDll }, "Recorder not available"),
            (S(GameStatus.NeedsRecording, Core.Planning.Planner.Record) with { RecorderInstalled = true }, "Recorder on: play 5 minutes"),
            (S(GameStatus.NeedsRecording, Dx11Too) with { RecorderInstalled = true }, "Recorder on: play on DirectX 12"),
            (S(GameStatus.NeedsRecording, Core.Planning.Planner.Record + "; " + ScsKiller.InDbNote) with { InCommunityDb = true }, "In the community database"),
            (S(GameStatus.NeedsRecording, Core.Planning.Planner.Record + "; " + ScsKiller.InDbSupportersNote) with { InCommunityDb = true }, "In the community database for Patreon supporters"),
            (S(GameStatus.NeedsRecording, ScsKiller.RtNote(false, false)) with { InCommunityDb = false }, "For ray-traced effects"),
            (S(GameStatus.NeedsRecording, "the recording has no draws: play into the game world"), "Play into the game world"),
            (S(GameStatus.Warmed, "warmed for driver 1.0; " + ScsKiller.RtAfterRecordingNote), "Ray tracing needs a 5-min recording"),
            (S(GameStatus.Warmed, "warmed for driver 1.0; " + ScsKiller.RtAfterRecordingNote) with { RecorderInstalled = true }, "Recorder on: play with ray tracing"),
            (S(GameStatus.Unsupported, "needs a recording, which EasyAntiCheat blocks", ac: AntiCheat.EasyAntiCheat), "Blocked by EasyAntiCheat"),
            (S(GameStatus.Unsupported, "needs a recording, which its anti-cheat blocks", ac: AntiCheat.Other), "Blocked by anti-cheat"),
            (S(GameStatus.Unsupported, "needs a recording, which VAC blocks") with { Vac = true }, "Blocked by VAC"),
            (S(GameStatus.Ready, "no recording needed; ray-traced effects aren't compiled: they need a recording, which VAC blocks") with { Vac = true }, "Ray tracing blocked by VAC"),
            (S(GameStatus.Unsupported, "needs a recording, which its anti-cheat blocks; " + ScsKiller.InDbNote, ac: AntiCheat.Other) with { InCommunityDb = true },
                "Blocked by anti-cheat · in the community database"),
            (S(GameStatus.Unsupported, "needs a recording, which its anti-cheat blocks; " + ScsKiller.InDbSupportersNote, ac: AntiCheat.Other) with { InCommunityDb = true },
                "Blocked by anti-cheat · in the community database for Patreon supporters"),
            (S(GameStatus.Unsupported, packed, Ue with { Version = "-", Unsupported = packed }), "Engine not supported yet"),
            (S(GameStatus.Unsupported, "encrypted game files (needs the game's AES key)", Ue with { Encrypted = true, Unsupported = "encrypted game files (needs the game's AES key)" }), null),
            (S(GameStatus.Unsupported, "no D3D shaders (SF_VULKAN_SM5)", Ue with { Unsupported = "no D3D shaders (SF_VULKAN_SM5)" }), "No DirectX shaders"),
            (S(GameStatus.Unsupported, "engine not supported yet") with { Engine = null }, "Engine not supported yet"),
            (S(GameStatus.Unsupported, "Access to the path is denied.") with { Engine = null }, "Couldn't read the game files"),
            (S(GameStatus.Unsupported, "runs on Vulkan"), "Runs on Vulkan"),
            (S(GameStatus.Unsupported, "runs on DirectX 11 (its config)"), "Runs on DirectX 11"),
            (S(GameStatus.Unsupported, "not supported on this GPU yet"), "Not supported on this GPU yet"),
            (S(GameStatus.Stale, "driver changed: 596.36 -> 610.88"), "Driver 610.88 cleared its cache"),
            (S(GameStatus.Stale, ScsKiller.TrimmedPartReason), "Part of its shader cache is gone"),
            (S(GameStatus.Stale, ScsKiller.MissesGameReason), "The compile missed the game's cache"),
            (S(GameStatus.Unsupported, ScsKiller.CantReachReason), "The compile can't reach this game"),
            (S(GameStatus.Stale, "game updated since the warm (build 1 -> 2)"), "Game updated"),
            (S(GameStatus.Stale, "game shaders changed since the warm"), "Game shaders changed"),
            (S(GameStatus.Stale, "SCSKiller can now compile 120 more pipelines for this game"), "SCSKiller can compile more"),
            (S(GameStatus.Stale, "1,234 new pipelines recorded; compile again to include them"), "1,234 new pipelines recorded"),
            (S(GameStatus.Warmed, "warmed for driver 610.88"), "Driver 610.88"),
            (S(GameStatus.Warmed, ScsKiller.OverCacheLimitReason), "Too big for the shader cache"),
            (S(GameStatus.Warmed, ScsKiller.CacheFullReason), "Shader cache full"),
            (S(GameStatus.Warmed, "warmed for driver 610.88") with { Plan = new PlanStats(0, 38_114, 0, 0, true, D3D11Shaders: 41_206) }, "DirectX 11 and 12"),
            (S(GameStatus.Warmed, "warmed for driver 610.88") with { Plan = new PlanStats(0, 0, 0, 0, true, D3D11Shaders: 41_206) }, "Driver 610.88"),
            (S(GameStatus.Warmed, "warmed for driver 610.88") with { Plan = new PlanStats(0, 900, 0, 0, true, D3D11Shaders: 10, Uncovered: 101) }, "DirectX 11 and 12 · partly covered"),
            (S(GameStatus.Warmed, ScsKiller.UnreachedReason) with { CompileUnreached = true }, "This game needs a custom loader"),
            (Tekken(GameStatus.Warmed), $"The driver skipped {180_000:N0} of {258_210:N0} pipelines"),
            (Tekken(GameStatus.Warmed) with { CompileUnreached = true, StatusReason = ScsKiller.UnreachedReason }, "This game needs a custom loader"),
            (Tekken(GameStatus.Stale) with { StatusReason = "driver changed: 610.88 -> 616.92" }, "Driver 616.92 cleared its cache"),
        };
        Assert.All(cases, c => Assert.Equal(c.Note, Format.ShortNote(c.State)));
    }

    static GameState Tekken(GameStatus status) => S(status, "partly compiled for driver 610.88: the driver skipped 180,000 of 258,210 pipelines")
        with { Plan = new PlanStats(39_870, 218_340, 0, 0, true), LastWarmFailed = 179_990, LastWarmCrashed = 10 };

    /// <summary>The threshold sits between the warms measured on a dev PC (at most 0.4% rejected) and TEKKEN 8 on Windows 10's
    /// D3D12 runtime (about 70%). Unknown counts make no claim.</summary>
    [Fact]
    public void A_warm_is_partly_compiled_when_the_driver_rejects_more_than_a_tenth_of_its_plan()
    {
        Assert.False(ScsKiller.IsPartlyCompiled(92, 24_993));         // the highest share measured, 0.37%
        Assert.False(ScsKiller.IsPartlyCompiled(1_000, 10_000));
        Assert.True(ScsKiller.IsPartlyCompiled(1_001, 10_000));
        Assert.True(ScsKiller.IsPartlyCompiled(180_000, 258_210));
        Assert.False(ScsKiller.IsPartlyCompiled(null, 258_210));      // a warm from before the counts were kept
        Assert.False(ScsKiller.IsPartlyCompiled(180_000, null));      // no plan
        Assert.True(ScsKiller.IsPartlyCompiled(Tekken(GameStatus.Warmed)));   // crashed items count with the rejected ones
        Assert.False(ScsKiller.IsPartlyCompiled(Tekken(GameStatus.Warmed) with { LastWarmCrashed = null, LastWarmFailed = 25_000 }));
        Assert.False(ScsKiller.IsPartlyCompiled(Tekken(GameStatus.Stale)));
        Assert.False(ScsKiller.IsPartlyCompiled(Tekken(GameStatus.Warmed) with { CompileUnreached = true }));
        Assert.False(ScsKiller.IsPartlyCompiled(Tekken(GameStatus.Warmed) with { LastWarmFailed = 0, LastWarmCrashed = 0, LastWarmSkipped = 200_000 }));   // not in the install: the game never creates them
        Assert.Equal($"the driver skipped {180_000:N0} of {258_210:N0} pipelines", ScsKiller.PartlyCompiledNote(Tekken(GameStatus.Warmed)));
        Assert.Equal($"the driver refused {180_000:N0} of {258_210:N0} pipelines, so those can still stutter the first time", ScsKiller.RefusedNote(Tekken(GameStatus.Warmed)));
        Assert.Equal(30, ScsKiller.ShownPercent(Tekken(GameStatus.Warmed)));   // 78,210 of 258,210 compiled, floored
        Assert.Equal(99, ScsKiller.ShownPercent(Tekken(GameStatus.Warmed) with { LastWarmFailed = 92 }));
    }

    /// <summary>The coverage ring is one measure in every state: recorded pipelines the last warm didn't compile count against
    /// it, refusals lower it by their share with no jump past a tenth, and with nothing new a Stale game shows what it showed
    /// warmed. Items skipped for hanging the driver or that SCSKiller can't build yet weren't compiled either.
    /// <paramref name="covered"/>: percent of 100 stage sets, 0 = not counted.</summary>
    [Theory]
    [InlineData(GameStatus.Stale, 0, 0, 0, 840, 54)]   // 1,000 of 1,840
    [InlineData(GameStatus.Warmed, 32, 99, 0, 0, 28)]
    [InlineData(GameStatus.Warmed, 32, 101, 0, 0, 28)]   // partly compiled: no higher than 99 refused
    [InlineData(GameStatus.Warmed, 32, 50, 0, 0, 30)]
    [InlineData(GameStatus.Stale, 32, 50, 0, 0, 30)]
    [InlineData(GameStatus.Stale, 0, 0, 200, 0, 80)]
    [InlineData(GameStatus.Warmed, 29, 0, 0, 0, 29)]
    [InlineData(GameStatus.Warmed, 0, 0, 0, 0, 80, 200)]   // 200 skipped for hanging the driver
    [InlineData(GameStatus.Warmed, 0, 0, 0, 0, 80, 0, 200)]   // 200 D3D11 items SCSKiller can't build yet
    public void The_coverage_ring_is_one_measure_in_every_state(GameStatus status, int covered, long refused, long newPlanned, long recorded, int ring, long hung = 0, long unbuilt = 0)
    {
        var s = S(status, "") with
        {
            Plan = new PlanStats(0, 1000, 0, 0, true, StageSets: covered > 0 ? 100 : 0, LeftOut: covered > 0 ? 100 - covered : 0),
            LastWarmFailed = refused, LastWarmCrashed = 0, LastWarmDriverSkipped = hung, LastWarmUnbuilt = unbuilt, NewPipelines = newPlanned, RecordedSinceWarm = recorded, NewOnly = status == GameStatus.Stale,
        };
        Assert.Equal(ring, ScsKiller.ShownPercent(s));
        if (newPlanned + recorded == 0)
            Assert.Equal(ring, ScsKiller.ShownPercent(s with { Status = status == GameStatus.Warmed ? GameStatus.Stale : GameStatus.Warmed }));
    }

    [Fact]
    public void A_plan_compiles_both_apis_with_DirectX_12_pipelines_and_DirectX_11_shaders()
    {
        Assert.True(ScsKiller.BothApis(new PlanStats(0, 38_114, 0, 0, true, D3D11Shaders: 41_206)));
        Assert.True(ScsKiller.BothApis(new PlanStats(500, 0, 0, 0, true, D3D11Shaders: 10)));   // DirectX 12 from a recording
        Assert.False(ScsKiller.BothApis(new PlanStats(0, 0, 0, 0, true, D3D11Shaders: 41_206)));
        Assert.False(ScsKiller.BothApis(new PlanStats(0, 38_114, 0, 0, true)));
        Assert.False(ScsKiller.BothApis(null));
    }

    const string Dx11Too = Core.Planning.Planner.Record + "; " + Core.Planning.Planner.Dx12Only;

    /// <summary>A game that may run on DirectX 11 never loads the recorder there: the whole reason keeps saying so.</summary>
    [Fact]
    public void The_recorder_on_note_keeps_the_DirectX_12_hint()
    {
        var s = S(GameStatus.NeedsRecording, Dx11Too) with { RecorderInstalled = true };
        Assert.Equal("recorder on: play for about 5 minutes, " + Core.Planning.Planner.Dx12Only, Format.RecorderOnNote(s));
        Assert.Equal("recorder on: play for about 5 minutes", Format.RecorderOnNote(s with { StatusReason = Core.Planning.Planner.Record }));
    }

    static GpuInfo Gpu(GpuVendor v, string name, ulong vram) => new(v, name, "1.0", 0, vram);

    [Fact]
    public void The_gpu_notice_shows_on_other_vendors_until_closed_for_that_gpu()
    {
        Assert.Equal("Intel support is experimental and off. Turn it on in Settings.", Format.GpuNotice(Gpu(GpuVendor.Intel, "Intel Graphics", 0), null));
        Assert.Equal("Intel support is experimental. DirectX 12 games need a recording first.", Format.GpuNotice(Gpu(GpuVendor.Intel, "Intel Graphics", 0), null, intel: true));
        Assert.StartsWith("SCSKiller doesn't compile on Qualcomm GPUs yet", Format.GpuNotice(Gpu(GpuVendor.Qualcomm, "Adreno", 0), null));
        Assert.StartsWith("SCSKiller doesn't compile on this GPU yet", Format.GpuNotice(Gpu(GpuVendor.Unknown, "no D3D adapter", 0), null));
        Assert.Null(Format.GpuNotice(Gpu(GpuVendor.Nvidia, "NVIDIA GeForce", 8UL << 30), null));
        Assert.Null(Format.GpuNotice(Gpu(GpuVendor.Amd, "AMD Radeon", 8UL << 30), null));
        Assert.Null(Format.GpuNotice(Gpu(GpuVendor.Intel, "Intel Graphics", 0), "Intel Graphics"));
        Assert.NotNull(Format.GpuNotice(Gpu(GpuVendor.Intel, "Intel Arc", 0), "Intel Graphics"));   // another GPU than the one it was closed for
        Assert.Null(AppStore.DefaultSettings.GpuNoticeDismissed);
    }

    [Fact]
    public void A_laptop_with_integrated_intel_and_a_discrete_gpu_uses_the_discrete_one_and_gets_no_notice()
    {
        var intel = new DxgiAdapter(Gpu(GpuVendor.Intel, "Intel Graphics", 128UL << 20), 0x46A6, 0);
        foreach (var dgpu in new[] { Gpu(GpuVendor.Nvidia, "NVIDIA GeForce Laptop GPU", 8UL << 30), Gpu(GpuVendor.Amd, "AMD Radeon", 8UL << 30) })
        {
            var discrete = new DxgiAdapter(dgpu, 0x10, 0);
            Assert.Equal(dgpu, GpuBackends.Primary([intel, discrete]));   // DXGI lists the iGPU first on most laptops
            Assert.Equal(dgpu, GpuBackends.Primary([discrete, intel]));
            Assert.Null(Format.GpuNotice(GpuBackends.Primary([intel, discrete])!, null));
        }
        Assert.Equal(intel.Gpu, GpuBackends.Primary([intel]));
    }

    /// <summary>A game that moves to another Library section is removed and inserted: with the list's default add/remove
    /// animations, it vanished and came back.</summary>
    [Fact]
    public void The_library_list_changes_rows_without_an_animation()
    {
        var page = System.Xml.Linq.XDocument.Load(Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App", "Pages", "LibraryPage.xaml"));
        System.Xml.Linq.XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var list = page.Descendants(ui + "ListView").Single(e => (string?)e.Attribute(x + "Name") == "List");
        var transitions = Assert.Single(list.Elements(ui + "ListView.ItemContainerTransitions"));
        Assert.Empty(Assert.Single(transitions.Elements(ui + "TransitionCollection")).Elements());
    }

    /// <summary>The Library updates a game's row in place: its template must bind one-way, or the row keeps showing the old state.</summary>
    [Fact]
    public void The_library_rows_bind_one_way()
    {
        var page = System.Xml.Linq.XDocument.Load(Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App", "Pages", "LibraryPage.xaml"));
        System.Xml.Linq.XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation", x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var template = page.Descendants(ui + "DataTemplate").Single(e => (string?)e.Attribute(x + "DataType") == "local:GameRow");
        Assert.Equal("OneWay", (string?)template.Elements().Single().Attribute(x + "DefaultBindMode"));
    }

    /// <summary>Set AES key sits in the game page's actions, shown only while the game needs its key.</summary>
    [Fact]
    public void The_game_page_offers_set_aes_key_only_while_the_key_is_missing()
    {
        var page = System.Xml.Linq.XDocument.Load(Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App", "Pages", "DetailPage.xaml"));
        System.Xml.Linq.XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var button = page.Descendants(ui + "Button").Single(e => (string?)e.Attribute("Click") == "OnSetAesKey");
        Assert.Equal("Set AES key", (string?)button.Attribute("Content"));
        Assert.Equal("{x:Bind Vm.NeedsAesKey, Mode=OneWay}", (string?)button.Attribute("Visibility"));
    }

    /// <summary>The before and after intro warns on every start, Start over included, that the game's own shader caches go too.</summary>
    [Fact]
    public void The_before_and_after_intro_always_says_the_game_caches_are_deleted()
    {
        var ui = File.ReadAllText(Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App", "BeforeAfterUi.cs"));
        Assert.Contains("Message = \"This also deletes the game's own shader cache, which isn't backed up", ui);
    }

    /// <summary>A WinUI slider clamps a thread or memory setting above this PC's to its maximum and writes that back: the getters show the
    /// clamped value so opening a page doesn't save it.</summary>
    [Fact]
    public void The_WinUI_thread_and_memory_sliders_dont_save_their_clamp()
    {
        var vms = File.ReadAllText(Path.Combine(TestEnv.RepoRoot, "src", "SCSKiller.App", "ViewModels.cs"));
        int Count(string s) => vms.Split(s).Length - 1;
        Assert.Equal(1, Count("public double Threads { get => Used; set { if ((int)value != Used) S = S with { Threads = (int)value }; } }"));
        Assert.Equal(1, Count("int Used => ScsKiller.UsedThreads(S.Threads);"));
        Assert.Equal(1, Count("public double Threads { get => UsedThreads; set { if ((int)value != UsedThreads) S = S with { Threads = (int)value }; } }"));
        Assert.Equal(1, Count("int UsedThreads => ScsKiller.UsedThreads(S.Threads);"));
        Assert.Equal(1, Count("public double BackgroundThreads { get => UsedBackground; set { if ((int)value != UsedBackground) S = S with { BackgroundThreads = (int)value }; } }"));
        Assert.Equal(1, Count("int UsedBackground => ScsKiller.UsedThreads(S.BackgroundThreads);"));
        Assert.Equal(1, Count("public double MaxCompileMemory { get => ShownMemory; set { var v = (int)value == 1 ? 2 : (int)value; if ((int)value != ShownMemory && v != S.MaxCompileMemoryGB)"));
        Assert.Equal(1, Count("int ShownMemory => Math.Min(S.MaxCompileMemoryGB, (int)MaxCompileMemoryMax);"));
    }

    [Fact]
    public void Raise_the_limit_is_offered_only_for_a_settable_limit_that_isnt_unlimited()
    {
        static VendorCaps Caps(bool settable) => new("test", true, true, settable);
        Assert.True(ScsKiller.CanRaiseCacheLimit(Caps(true), new(16L << 30, true)));
        Assert.True(ScsKiller.CanRaiseCacheLimit(Caps(true), null));
        Assert.False(ScsKiller.CanRaiseCacheLimit(Caps(true), new(null, false)));
        Assert.False(ScsKiller.CanRaiseCacheLimit(Caps(false), new(16L << 30, false)));
    }

    [Fact]
    public void An_engine_without_a_version_is_just_its_family()
    {
        EngineInfo E(string family, string version) => new(family, version, null, "D3D12", false, null);
        Assert.Equal("Carved DXBC", Format.Engine(E("Carved", "DXBC")));
        Assert.Equal("UE 5.4", Format.Engine(E("Unreal", "5.4"), "UE"));
        Assert.Equal(["Carved", "FromSoft", "Unity", "Carved"], new[] { E("Carved", "-"), E("FromSoft", "-"), E("Unity", "?"), E("Carved", "") }.Select(e => Format.Engine(e)));
    }
}
