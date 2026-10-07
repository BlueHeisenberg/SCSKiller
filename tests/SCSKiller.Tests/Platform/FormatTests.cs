using System.Globalization;
using System.Text.RegularExpressions;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

public class FormatTests : IDisposable
{
    // some tests switch the culture (the assembly's UI culture is pinned in TestEnv): each gets it back afterwards
    readonly CultureInfo culture = CultureInfo.CurrentCulture;
    readonly CultureInfo uiCulture = CultureInfo.CurrentUICulture;

    public void Dispose()
    {
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = uiCulture;
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ja-JP")]
    [InlineData("ar-EG")]
    public void Regional_formatting_is_independent_of_the_UI_language(string region)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(region);
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        Assert.Equal(1.5.ToString("0.#", CultureInfo.CurrentCulture) + " GB", Format.Bytes(3L << 29));
        Assert.Equal("Reading shaders", Format.QueueActivity(new("g", QueueStage.Indexing, null, null)));
        Assert.Equal("unknown diagnostic {details}", Loc.Text("unknown diagnostic {details}"));
    }

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

    [Theory]
    [InlineData("en-US", "2 h 5 min")]
    [InlineData("zh-Hans", "2 小时 5 分钟")]
    public void UI_duration_is_localized_while_stored_duration_is_invariant(string language, string expected)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(language);
        Assert.Equal(Format.Dash, Format.Duration(null));
        Assert.Equal(expected, Format.Duration(TimeSpan.FromMinutes(125)));
        Assert.Equal("2 h 5 min", ScsKiller.Duration(TimeSpan.FromMinutes(125)));
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
        Assert.Equal("XeSS: detected; added after a recording sees them", Format.Middleware(new("XeSS", ["libxess.dll"], 0)));
    }

    static readonly EngineInfo Ue = new("Unreal", "4.27", null, "D3D12", false, null);

    static GameState S(GameStatus status, string reason, EngineInfo? engine = null, AntiCheat ac = AntiCheat.None) =>
        new(new Game("steam:1", "Game", Store.Steam, @"X:\g", @"X:\g\g.exe"), engine ?? Ue, ac, status, reason,
            null, null, null, null, "610.88", null, null, false, null);

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
            (S(GameStatus.Ready, "no recording needed; also compiles every DirectX 11 shader (the game may run on either)"), "DirectX 11 and 12"),
            (S(GameStatus.Ready, "compiles every DirectX 11 shader (the game may run on either); for DirectX 12, turn on recording and play for about 5 minutes"), "DirectX 11; DirectX 12 needs a recording"),
            (S(GameStatus.Ready, "no recording needed; ray-traced effects aren't compiled: they need a recording, which BattlEye blocks", ac: AntiCheat.BattlEye), "Ray tracing blocked by BattlEye"),
            (S(GameStatus.NeedsRecording, Core.Planning.Planner.Record), "Turn on Record and play"),
            (S(GameStatus.NeedsRecording, Core.Planning.Planner.Record + "; " + ScsKiller.InDbNote) with { InCommunityDb = true }, "In the community database"),
            (S(GameStatus.NeedsRecording, ScsKiller.RtNote(false)) with { InCommunityDb = false }, "For ray-traced effects"),
            (S(GameStatus.NeedsRecording, "the recording has no draws: play into the game world"), "Play into the game world"),
            (S(GameStatus.Warmed, "warmed for driver 1.0; " + ScsKiller.RtAfterRecordingNote), "Ray tracing needs a 5-min recording"),
            (S(GameStatus.Warmed, "warmed for driver 1.0; " + ScsKiller.RtAfterRecordingNote) with { RecorderInstalled = true }, "Recorder on: play with ray tracing"),
            (S(GameStatus.Unsupported, "needs a recording, which EasyAntiCheat blocks", ac: AntiCheat.EasyAntiCheat), "Blocked by EasyAntiCheat"),
            (S(GameStatus.Unsupported, "needs a recording, which its anti-cheat blocks", ac: AntiCheat.Other), "Blocked by anti-cheat"),
            (S(GameStatus.Unsupported, "needs a recording, which its anti-cheat blocks; " + ScsKiller.InDbNote, ac: AntiCheat.Other) with { InCommunityDb = true },
                "Blocked by anti-cheat · in the community database"),
            (S(GameStatus.Unsupported, packed, Ue with { Version = "-", Unsupported = packed }), "Engine not supported yet"),
            (S(GameStatus.Unsupported, "encrypted game files (needs the game's AES key)", Ue with { Encrypted = true, Unsupported = "encrypted game files (needs the game's AES key)" }), null),
            (S(GameStatus.Unsupported, "no D3D shaders (SF_VULKAN_SM5)", Ue with { Unsupported = "no D3D shaders (SF_VULKAN_SM5)" }), "No DirectX shaders"),
            (S(GameStatus.Unsupported, "engine not supported yet") with { Engine = null }, "Engine not supported yet"),
            (S(GameStatus.Unsupported, "Access to the path is denied.") with { Engine = null }, "Couldn't read the game files"),
            (S(GameStatus.Unsupported, "runs on Vulkan"), "Runs on Vulkan"),
            (S(GameStatus.Unsupported, "runs on DirectX 11 (its config)"), "Runs on DirectX 11"),
            (S(GameStatus.Unsupported, "not supported on this GPU yet"), "Not supported on this GPU yet"),
            (S(GameStatus.Stale, "driver changed: 596.36 -> 610.88"), "Driver 610.88 cleared its cache"),
            (S(GameStatus.Stale, ScsKiller.TrimmedPartReason), "The driver trimmed its cache"),
            (S(GameStatus.Stale, ScsKiller.MissesGameReason), "The compile missed the game's cache"),
            (S(GameStatus.Stale, "game updated since the warm (build 1 -> 2)"), "Game updated"),
            (S(GameStatus.Stale, "game shaders changed since the warm"), "Game shaders changed"),
            (S(GameStatus.Stale, "SCSKiller can now compile 120 more pipelines for this game"), "SCSKiller can compile more"),
            (S(GameStatus.Stale, "1,234 new pipelines recorded; compile again to include them"), "1,234 new pipelines recorded"),
            (S(GameStatus.Warmed, "warmed for driver 610.88"), "Driver 610.88"),
        };
        Assert.All(cases, c => Assert.Equal(c.Note, Format.ShortNote(c.State)));
    }

    [Fact]
    public void Format_shows_English_under_the_invariant_UI_culture_the_command_line_sets()
    {
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;   // what Program.cs sets first, whatever the display language
        Assert.Equal("DLSS: compiled by the NVIDIA driver itself", Format.Middleware(new MiddlewareTag("DLSS", ["nvngx_dlss.dll"], 0)));
        Assert.Equal(("2 h 5 min", "3.2 GB"), (Format.Duration(TimeSpan.FromMinutes(125)), Format.Bytes((long)(3.2 * (1L << 30)))));
        Assert.Equal(ScsKiller.WhenIdleNote, Format.QueueNote(new QueueItem("g", QueueStage.Waiting, null, null, ScsKiller.WhenIdleNote)));
    }

    [Fact]
    public void Raw_diagnostics_paths_and_stored_notes_are_not_translated()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-Hans");
        const string unknown = @"C:\Games\custom.exe: unknown error 0x80070005; another clause";
        Assert.Equal((unknown, unknown), (Format.Reason(unknown), Format.QueueNote(new QueueItem("g", QueueStage.Waiting, null, null, unknown))));
        string file = Core.Warming.Warmer.ExeFor(GpuVendor.Nvidia);
        var failed = Format.QueueNote(new QueueItem("g", QueueStage.Failed, null, $"{file} not found next to the app or in proxy\\build\\Release"));
        Assert.True(failed.Contains(file) && failed.Contains(@"proxy\build\Release"), failed);   // translated, the file and folder kept
        FormattableString note = $"paused while {"Game {x}"} is running";
        var q = new QueueItem("g", QueueStage.Paused, null, null, note.ToString()) { NoteFormat = note };
        Assert.Contains("Game {x}", Format.QueueNote(q));
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(q with { NoteFormat = null }), System.Text.Json.JsonSerializer.Serialize(q));
    }

    [Theory]
    [InlineData("src/SCSKiller.Core/App/Strings.resx", "src/SCSKiller.Core/App/Strings.zh-Hans.resx")]
    [InlineData("src/SCSKiller.App/Strings/en-US/Resources.resw", "src/SCSKiller.App/Strings/zh-Hans/Resources.resw")]
    public void Translations_have_the_same_keys_and_only_the_placeholders_of_the_English(string english, string chinese)
    {
        static Dictionary<string, string> Read(string file) => System.Xml.Linq.XDocument.Load(Path.Combine(TestEnv.RepoRoot, file)).Root!
            .Elements("data").ToDictionary(d => (string)d.Attribute("name")!, d => (string)d.Element("value")!);
        static HashSet<string> Holes(string s) => Regex.Matches(s, @"(?<!\{)\{(\d+)").Select(m => m.Groups[1].Value).ToHashSet();
        var (en, zh) = (Read(english), Read(chinese));
        Assert.Equal(en.Keys.Order(), zh.Keys.Order());
        Assert.Equal(en.Count, en.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());   // resource names ignore case
        Assert.All(zh, e => Assert.True(Holes(e.Value).IsSubsetOf(Holes(en[e.Key])), $"{e.Key}: {e.Value}"));
    }

    /// <summary>The core's own messages: Format.Reason gives the same English back and knows every clause (a resource or a
    /// ReasonTemplates match), so a message reworded in Core fails here instead of showing untranslated.</summary>
    public static TheoryData<string> CoreMessages()
    {
        var (unreal, caps) = (new EngineInfo("Unreal", "5.4", null, "D3D12", false, null), new VendorCaps("nvidia-1", true, true, true));
        return new(ScsKiller.RtNote(null), ScsKiller.RtNote(false), ScsKiller.RtUnseenNote, ScsKiller.RtInlineNote, "warmed for driver 1.0; " + ScsKiller.RtAfterRecordingNote,
            "ray-traced effects aren't compiled: they need a recording, " + ScsKiller.ManualNoRecording, "needs a recording, which its anti-cheat blocks; " + ScsKiller.InDbNote,
            ScsKiller.PartialNote(new PlanStats(0, 1200, 0, 0, false, Uncovered: 500))!, ScsKiller.PartialNote(new PlanStats(300, 1200, 0, 0, false, Uncovered: 400))!,
            ScsKiller.NotChainableReason("OptiScaler"), ScsKiller.NotChainableReason("ReShade"), ScsKiller.SkipForeignDll, ScsKiller.SkipVulkanMod, ScsKiller.SkipShaderMod,
            ScsKiller.RtWhy(caps, unreal), ScsKiller.RtWhy(caps with { RtCacheGranularity = RtCacheGranularity.Collection }, unreal),
            ScsKiller.WhenIdleNote, ScsKiller.TrimmedAllReason, Core.Planning.Planner.UntestedNote, "driver changed: 591.44 -> 596.02");
    }

    [Theory]
    [MemberData(nameof(CoreMessages))]
    public void Core_messages_reach_Reason_unchanged_in_English_and_known_to_it(string message) =>
        Assert.True(Format.Reason(message) == message && KnownReason(message), message);

    static readonly System.Resources.ResourceManager Neutral = new("SCSKiller.Core.App.Strings", typeof(Loc).Assembly);
    static bool IsResource(string s) => Neutral.GetString(s, CultureInfo.InvariantCulture) != null || Neutral.GetString(char.ToUpperInvariant(s[0]) + s[1..], CultureInfo.InvariantCulture) != null;
    /// <summary>Every clause is a resource or matches a template, and when several match, one is the longest (Format.Reason
    /// takes it), so no template shadows a more specific one.</summary>
    internal static bool KnownReason(string message) => IsResource(message) || message.Split("; ").All(c => IsResource(c)
        || Format.ReasonTemplates.Where(t => Regex.IsMatch(c, "\\A" + string.Join("(.+?)", Regex.Split(t, @"\{\d+\}").Select(Regex.Escape)) + "\\z", RegexOptions.IgnoreCase))
            .Select(t => t.Length).OrderDescending().ToArray() is var lengths && (lengths.Length == 1 || lengths.Length > 1 && lengths[0] > lengths[1]));

    static GpuInfo Gpu(GpuVendor v, string name, ulong vram) => new(v, name, "1.0", 0, vram);

    [Fact]
    public void The_gpu_notice_shows_on_other_vendors_until_closed_for_that_gpu()
    {
        Assert.StartsWith("SCSKiller doesn't compile on Intel GPUs yet", Format.GpuNotice(Gpu(GpuVendor.Intel, "Intel Graphics", 0), null));
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
}
