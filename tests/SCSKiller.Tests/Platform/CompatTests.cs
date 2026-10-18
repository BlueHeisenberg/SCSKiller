using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SCSKiller.Core;
using SCSKiller.Core.App;
using static SCSKiller.Tests.Platform.CommunityTests;

namespace SCSKiller.Tests.Platform;

// Compatibility reports (Core/App/Compat.cs) against a fake handler, and the signed public aggregate with throwaway keys:
// nothing here reaches a server.
public class CompatTests : IDisposable
{
    static readonly Uri Com = new("https://api.test.com/"), Io = new("https://api.test.io/");
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-compat-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<(string Line, byte[] Body)> _sent = [];
    readonly Queue<HttpStatusCode> _answers = new();
    static readonly (string Seed, string Public) Feed = FeedTrust.NewKey(), Rules = FeedTrust.NewKey();
    static readonly DateTimeOffset T = new(2026, 10, 9, 12, 0, 0, TimeSpan.Zero);

    public CompatTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    Sharing Make(bool sharingOn = false) => new(_dir, () => sharingOn, new RouteFailover(new Fake(Answer), [Com, Io], new Clock()), new Clock());

    HttpResponseMessage Answer(HttpRequestMessage r)
    {
        lock (_sent) _sent.Add(($"{r.Method} {r.RequestUri!.AbsolutePath}{(r.Headers.Authorization is { } a ? " " + a : "")}", r.Content?.ReadAsByteArrayAsync().Result ?? []));
        return r.RequestUri.AbsolutePath switch
        {
            "/v1/devices" => Ours(HttpStatusCode.OK, """{"device_token":"sd1_test-anon","device_id":"dev1"}"""u8.ToArray()),
            "/v1/compat" when _answers.TryDequeue(out var code) => Ours(code, code == HttpStatusCode.OK ? """{"replaced":false}"""u8.ToArray() : null),
            "/v1/compat" => Ours(HttpStatusCode.OK, """{"replaced":false}"""u8.ToArray()),
            _ => Ours(HttpStatusCode.NotFound),
        };
    }

    static CompatReport Report(string? cpu = "AMD Ryzen 7 7800X3D 8-Core Processor", int? coverage = 99) =>
        new("steam:2909400", "steam:2909400@17405238", CompatChoice.NoHelp, true, false, true, cpu, "NVIDIA GeForce RTX 4070", "nvidia", "32.0.15.7680",
            "10.0.26300", "1.3.0-internal.2", new(CompatStatus.Partly, coverage, true, false));

    static Game G(Store store = Store.Steam, string? version = "17405238") => new("steam:2909400", "Fake", store, @"X:\Fake", @"X:\Fake\fake.exe", version);

    static GameState State(Game? g = null, DateTimeOffset? warmed = null, DateTimeOffset? played = null, GameStatus status = GameStatus.Warmed) =>
        new GameState(g ?? G(), null, AntiCheat.None, status, "", null, null, null, null, null, warmed, null, false, null) with { PlayedAt = played };

    // --- the gate ---

    [Fact]
    public void Send_report_is_on_only_for_a_store_build_compiled_and_played_since_on_nvidia_or_amd()
    {
        var warmed = T;
        Assert.Null(CompatReport.WhyNot(State(warmed: warmed, played: warmed.AddHours(1)), "nvidia"));
        Assert.Null(CompatReport.WhyNot(State(warmed: warmed, played: warmed.AddHours(1)), "amd"));
        Assert.Equal("Compile the game and play it once to rate how it ran.", CompatReport.WhyNot(State(played: warmed), "nvidia"));
        Assert.Equal("Play the game once since it compiled to rate how it ran.", CompatReport.WhyNot(State(warmed: warmed), "nvidia"));
        Assert.Equal("Play the game once since it compiled to rate how it ran.", CompatReport.WhyNot(State(warmed: warmed, played: warmed.AddHours(-1)), "nvidia"));
        Assert.Equal("Ratings are for NVIDIA and AMD GPUs.", CompatReport.WhyNot(State(warmed: warmed, played: warmed.AddHours(1)), null));
        // the last compile of the installed build failed or was refused: no launch needed
        Assert.Null(CompatReport.WhyNot(State() with { CompileFailed = true }, "amd"));
        Assert.Null(CompatReport.WhyNot(State(warmed: warmed) with { CompileFailed = true }, "nvidia"));
        Assert.Equal("Ratings are for NVIDIA and AMD GPUs.", CompatReport.WhyNot(State() with { CompileFailed = true }, null));
        Assert.Equal("SCSKiller can't tell which version of this game is installed.", CompatReport.WhyNot(State(G(version: null)) with { CompileFailed = true }, "nvidia"));
        // an added game's id is made from its path: never reported
        Assert.Equal("SCSKiller can't tell which version of this game is installed.", CompatReport.WhyNot(State(G(Store.Manual), warmed, warmed.AddHours(1)), "nvidia"));
        Assert.Equal("SCSKiller can't tell which version of this game is installed.", CompatReport.WhyNot(State(G(version: null), warmed, warmed.AddHours(1)), "nvidia"));
    }

    [Fact]
    public void The_compile_facts_follow_the_game_state()
    {
        var plan = new PlanStats(994, 1000, 0, 1, true, StageSets: 100, LeftOut: 0);
        var s = State(warmed: T, played: T.AddHours(1)) with { Plan = plan, LastWarmFailed = 0 };
        Assert.Equal(new CompatCompile(CompatStatus.Compiled, 100, true, false), CompatReport.CompileOf(s, false));
        Assert.Equal(new CompatCompile(CompatStatus.Compiled, 100, true, true), CompatReport.CompileOf(s, true));
        Assert.Equal(new CompatCompile(CompatStatus.Compiled, 100, true, true), CompatReport.CompileOf(s with { MaximumForGame = true }, false));
        Assert.Equal(CompatStatus.Partly, CompatReport.CompileOf(s with { LastWarmFailed = 1500 }, false).Status);
        Assert.Equal(CompatStatus.NotSupported, CompatReport.CompileOf(s with { Status = GameStatus.Unsupported }, false).Status);
        Assert.Equal(new CompatCompile(CompatStatus.NotCompiled, null, false, false), CompatReport.CompileOf(State(), false));
        Assert.Equal(CompatStatus.Failed, CompatReport.CompileOf(State() with { CompileFailed = true }, false).Status);
        Assert.Equal(CompatStatus.Failed, CompatReport.CompileOf(s with { CompileFailed = true }, false).Status);   // a recompile that failed
        Assert.Equal(CompatStatus.NotSupported, CompatReport.CompileOf(State(status: GameStatus.Unsupported) with { CompileFailed = true }, false).Status);
        Assert.Equal(CompatStatus.NotCompiled, CompatReport.CompileOf(State(status: GameStatus.Unsupported), false).Status);
    }

    // --- one per build ---

    [Fact]
    public void A_sent_report_is_kept_per_store_build_and_sending_again_replaces_it()
    {
        var game = Path.Combine(_dir, "games", "steam_2909400");
        Assert.Null(CompatSent.For(game, "steam:2909400@1"));
        CompatSent.Save(game, "steam:2909400@1", new(CompatChoice.Gone, T));
        CompatSent.Save(game, "steam:2909400@2", new(CompatChoice.Worse, T));
        CompatSent.Save(game, "steam:2909400@1", new(CompatChoice.Better, T.AddDays(1)));
        Assert.Equal(new CompatSent(CompatChoice.Better, T.AddDays(1)), CompatSent.For(game, "steam:2909400@1"));
        Assert.Equal(new CompatSent(CompatChoice.Worse, T), CompatSent.For(game, "steam:2909400@2"));
        // only the choice and the time are kept
        var kept = JsonDocument.Parse(File.ReadAllText(Path.Combine(game, CompatSent.FileName))).RootElement;
        Assert.Equal(["Choice", "At"], kept.GetProperty("steam:2909400@1").EnumerateObject().Select(p => p.Name));
    }

    // --- the body ---

    [Fact]
    public void The_body_has_exactly_the_disclosed_members_in_snake_case_with_nulls_sent_as_null()
    {
        var body = JsonDocument.Parse(Report(cpu: null, coverage: null).Body()).RootElement;
        Assert.Equal(["game_id", "store_build_key", "choice", "frame_gen", "upscaler", "mods", "cpu_model", "gpu_model", "gpu_vendor", "driver_version",
            "windows_build", "app_version", "compile"], body.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["status", "coverage_pct", "recorded", "maximum"], body.GetProperty("compile").EnumerateObject().Select(p => p.Name));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("cpu_model").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("compile").GetProperty("coverage_pct").ValueKind);
        Assert.Equal(("no_help", "partly", true, false), (body.GetProperty("choice").GetString(), body.GetProperty("compile").GetProperty("status").GetString(),
            body.GetProperty("frame_gen").GetBoolean(), body.GetProperty("upscaler").GetBoolean()));
    }

    [Fact]
    public void Every_choice_and_status_has_the_agreed_wire_name()
    {
        string Wire<T>(T v) => JsonSerializer.Serialize(v, CompatReport.Json).Trim('"');
        Assert.Equal(["gone", "better", "no_help", "worse", "no_start", "not_compiled", "no_stutter_before"], Enum.GetValues<CompatChoice>().Select(v => Wire(v)));
        Assert.Equal(["compiled", "partly", "not_supported", "failed", "not_compiled"], Enum.GetValues<CompatStatus>().Select(v => Wire(v)));
    }

    [Fact]
    public void The_build_key_is_the_uploads_and_windows_is_major_minor_build()
    {
        Assert.Equal("steam:2909400@17405238", CompatReport.BuildKey(G()));
        Assert.StartsWith(G().Id + "@", CompatReport.BuildKey(G()));
        Assert.Matches(new Regex(@"^\d{1,2}\.\d{1,2}\.\d{4,6}$"), CompatReport.ThisWindows);
    }

    [Fact]
    public void The_largest_body_stays_under_the_servers_4_KiB()
    {
        var wide = new string('\u2122', 128);   // escaped in JSON: six bytes a character
        var r = Report() with { GameId = "steam:" + new string('9', 114), StoreBuildKey = "steam:" + new string('9', 114) + "@" + wide, CpuModel = wide[..100],
            GpuModel = wide, DriverVersion = new string('9', 32), AppVersion = "1.3.0-internal.2+" + new string('a', 40), Choice = CompatChoice.NoStutterBefore,
            Compile = new(CompatStatus.NotSupported, 100, true, true) };
        Assert.True(r.Body().Length < 4096, $"{r.Body().Length} bytes");
    }

    // --- the transport ---

    [Fact]
    public async Task Send_registers_the_upload_device_only_then_and_posts_the_report_with_sharing_off()
    {
        var sharing = Make(sharingOn: false);
        Assert.Empty(_sent);   // nothing before the click
        Assert.Null(await sharing.SendCompatAsync(Report()));
        Assert.Equal(["POST /v1/devices", "POST /v1/compat Bearer sd1_test-anon"], _sent.Select(s => s.Line));
        Assert.Equal(Report().Body(), _sent[1].Body);
        Assert.Null(await sharing.SendCompatAsync(Report()));   // the device is kept: no second registration
        Assert.Equal(3, _sent.Count);
    }

    [Fact]
    public async Task A_401_goes_once_more_with_a_new_device_and_then_stops()
    {
        _answers.Enqueue(HttpStatusCode.Unauthorized);
        Assert.Null(await Make().SendCompatAsync(Report()));
        Assert.Equal(["POST /v1/devices", "POST /v1/compat Bearer sd1_test-anon", "POST /v1/devices", "POST /v1/compat Bearer sd1_test-anon"], _sent.Select(s => s.Line));

        _sent.Clear();
        _answers.Enqueue(HttpStatusCode.Unauthorized);
        _answers.Enqueue(HttpStatusCode.Unauthorized);
        Assert.Equal("Not sent. Try again later.", await Make().SendCompatAsync(Report()));
        Assert.Equal(2, _sent.Count(s => s.Line.StartsWith("POST /v1/compat")));
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "Not sent. Try again later.")]
    [InlineData(HttpStatusCode.BadRequest, "The server didn't take this report.")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "Not sent. Try again later.")]
    [InlineData(HttpStatusCode.InternalServerError, "Not sent. Try again later.")]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, "Not sent. Try again later.")]
    public async Task A_refusal_says_what_to_do_once_without_retrying(HttpStatusCode code, string said)
    {
        _answers.Enqueue(code);
        var sharing = Make();
        Assert.Equal(said, await sharing.SendCompatAsync(Report()));
        Assert.Equal(1, _sent.Count(s => s.Line.StartsWith("POST /v1/compat")));
        Assert.Null(await sharing.SendCompatAsync(Report()));   // the user's next click sends again
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task A_server_without_reports_isnt_asked_again_until_the_next_start(HttpStatusCode code)
    {
        _answers.Enqueue(code);
        var sharing = Make();
        Assert.Equal("Reports aren't open yet.", await sharing.SendCompatAsync(Report()));
        Assert.Equal("Reports aren't open yet.", await sharing.SendCompatAsync(Report()));
        Assert.Equal(1, _sent.Count(s => s.Line.StartsWith("POST /v1/compat")));
        Assert.Null(await Make().SendCompatAsync(Report()));   // a new start asks again
    }

    [Fact]
    public async Task Offline_says_not_sent()
    {
        var sharing = new Sharing(_dir, () => false, new RouteFailover(new Fake(_ => throw new HttpRequestException("offline")), [Com, Io], new Clock()), new Clock());
        Assert.Equal("Not sent. Try again later.", await sharing.SendCompatAsync(Report()));
    }

    // --- the public aggregate ---

    const string Families = """
        [{"vendor":"nvidia","family":"nvidia-ada","match":["RTX 40"]},
         {"vendor":"nvidia","family":"nvidia-older","match":["GeForce"]},
         {"vendor":"amd","family":"amd-igpu","match":["Radeon(TM) Graphics","780M"]},
         {"vendor":"amd","family":"amd-rdna3","match":["RX 7"]}]
        """;

    static string Shares(double gone = 0, double better = 0, double noHelp = 0, double worse = 0, double noStart = 0, double notCompiled = 0, double before = 0) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $$"""{"gone":{{gone}},"better":{{better}},"no_help":{{noHelp}},"worse":{{worse}},"no_start":{{noStart}},"not_compiled":{{notCompiled}},"no_stutter_before":{{before}}}""");

    static string Doc(string classes, int min = 5, string families = Families, string extra = "") =>
        $$"""{"generated":"2026-10-09","min_devices":{{min}},"families":{{families}},"games":[{"ids":["steam:2909400","epic:abc"],"classes":[{{classes}}]}]{{extra}}}""";

    static string Class(string vendor, string? family, int devices, string shares) =>
        $$"""{"vendor":"{{vendor}}","family":{{(family == null ? "null" : $"\"{family}\"")}},"devices":{{devices}},"shares":{{shares}}}""";

    [Fact]
    public void The_family_is_the_first_match_in_file_order_case_insensitive_and_of_the_own_vendor_only()
    {
        var feed = CompatFeed.TryParse(Doc(Class("nvidia", null, 9, Shares(gone: 1))))!;
        Assert.Equal("nvidia-ada", feed.FamilyOf("nvidia", "NVIDIA GeForce RTX 4070"));   // also matches "GeForce" below: the first wins
        Assert.Equal("nvidia-ada", feed.FamilyOf("nvidia", "nvidia geforce rtx 4060 laptop gpu"));
        Assert.Equal("nvidia-older", feed.FamilyOf("nvidia", "NVIDIA GeForce GTX 1080"));
        Assert.Equal("amd-igpu", feed.FamilyOf("amd", "AMD Radeon(TM) Graphics"));
        Assert.Equal("amd-rdna3", feed.FamilyOf("amd", "AMD Radeon RX 7800 XT"));
        Assert.Null(feed.FamilyOf("amd", "NVIDIA GeForce RTX 4070"));   // another vendor's entries never apply
        Assert.Null(feed.FamilyOf("nvidia", "AMD Radeon RX 7800 XT"));
        Assert.Null(feed.FamilyOf("amd", "AMD Radeon Pro W7900"));
        Assert.Null(feed.FamilyOf("nvidia", null));
    }

    [Fact]
    public void The_line_comes_from_the_family_row_else_the_vendor_row_each_from_min_devices()
    {
        var feed = CompatFeed.TryParse(Doc(string.Join(",", Class("nvidia", "nvidia-ada", 5, Shares(gone: 0.6)), Class("nvidia", null, 40, Shares(noHelp: 0.7)),
            Class("amd", "amd-rdna3", 4, Shares(worse: 0.5)), Class("amd", null, 6, Shares(better: 0.8)))))!;
        Game Of(string id) => new(id, "Fake", Store.Steam, "", "");
        Assert.Equal(SimilarSystems.Helped, feed.For(Of("steam:2909400"), "nvidia", "NVIDIA GeForce RTX 4090"));   // its family's
        Assert.Equal(SimilarSystems.Helped, feed.For(Of("epic:abc"), "nvidia", "NVIDIA GeForce RTX 4090"));   // any of the entry's ids
        Assert.Equal(SimilarSystems.NoHelp, feed.For(Of("steam:2909400"), "nvidia", "NVIDIA RTX A2000"));   // no family: the vendor's row
        Assert.Equal(SimilarSystems.NoHelp, feed.For(Of("steam:2909400"), "nvidia", "NVIDIA GeForce GTX 1080"));   // a family without a row
        Assert.Equal(SimilarSystems.Helped, feed.For(Of("steam:2909400"), "amd", "AMD Radeon RX 7900 XTX"));   // its family is under min_devices
        Assert.Null(feed.For(Of("steam:1"), "nvidia", "NVIDIA GeForce RTX 4090"));
        Assert.Null(feed.For(Of("steam:2909400"), null, "Intel Arc A770"));

        var few = CompatFeed.TryParse(Doc(Class("nvidia", null, 4, Shares(gone: 1))))!;
        Assert.Null(few.For(Of("steam:2909400"), "nvidia", "NVIDIA GeForce RTX 4090"));   // below min_devices: nothing
        Assert.Equal(SimilarSystems.Helped, CompatFeed.TryParse(Doc(Class("nvidia", null, 4, Shares(gone: 1)), min: 4))!.For(Of("steam:2909400"), "nvidia", null));
    }

    [Fact]
    public void The_thresholds_warn_first_then_mostly_helped_then_mostly_no_help()
    {
        CompatFeed.Shares S(double gone = 0, double better = 0, double noHelp = 0, double worse = 0, double noStart = 0, double notCompiled = 0, double before = 0) =>
            new(gone, better, noHelp, worse, noStart, notCompiled, before);
        Assert.Equal((0.20, 0.60), (CompatFeed.Warn, CompatFeed.Mostly));
        Assert.Equal(SimilarSystems.Warning, CompatFeed.Verdict(S(gone: 0.8, worse: 0.2)));   // it wins over helped
        Assert.Null(CompatFeed.Verdict(S(gone: 0.55, worse: 0.15, noStart: 0.3)));   // not starting isn't counted
        Assert.Null(CompatFeed.Verdict(S(gone: 0.55, worse: 0.15)));
        Assert.Equal(SimilarSystems.Helped, CompatFeed.Verdict(S(gone: 0.45, better: 0.15, noHelp: 0.4)));
        Assert.Equal(SimilarSystems.NoHelp, CompatFeed.Verdict(S(noHelp: 0.3, notCompiled: 0.2, before: 0.1, gone: 0.4)));
        Assert.Null(CompatFeed.Verdict(S(gone: 0.35, better: 0.2, noHelp: 0.45)));   // neither mostly
        Assert.Null(CompatFeed.Verdict(S()));   // shares need not sum to 1
    }

    [Fact]
    public void A_file_off_its_shape_is_refused_whole()
    {
        var good = Class("nvidia", null, 9, Shares(gone: 1));
        Assert.NotNull(CompatFeed.TryParse(Doc(good)));
        Assert.Null(CompatFeed.TryParse(Doc(good, extra: ""","note":"x" """)));   // an unknown member
        Assert.Null(CompatFeed.TryParse(Doc(good).Replace("\"generated\":\"2026-10-09\",", "")));   // a missing one
        Assert.Null(CompatFeed.TryParse(Doc(good).Replace("2026-10-09", "yesterday")));
        Assert.Null(CompatFeed.TryParse(Doc(good, min: 0)));
        Assert.Null(CompatFeed.TryParse(Doc(Class("nvidia", null, 9, Shares(gone: 1.5)))));
        Assert.Null(CompatFeed.TryParse(Doc(Class("nvidia", null, 9, Shares(gone: -0.1)))));
        Assert.Null(CompatFeed.TryParse(Doc(Class("nvidia", null, -1, Shares(gone: 1)))));
        Assert.Null(CompatFeed.TryParse(Doc(Class("intel", null, 9, Shares(gone: 1)))));
        Assert.Null(CompatFeed.TryParse(Doc(Class("nvidia", "Ada Lovelace", 9, Shares(gone: 1)))));
        Assert.Null(CompatFeed.TryParse(Doc(good, families: """[{"vendor":"nvidia","family":"nvidia-ada","match":[]}]""")));
        Assert.Null(CompatFeed.TryParse(Doc(good).Replace("\"steam:2909400\"", "\"no id\"")));
        Assert.Null(CompatFeed.TryParse(Doc(good).Replace("\"no_help\":0,", "")));
        Assert.Null(CompatFeed.TryParse("not json"));
    }

    // --- the signature ---

    ContentTrust CompatTrust(string? key = null) => new(new AppStore(_dir), new Dictionary<string, string> { ["feed-a"] = key ?? Feed.Public }, CompatFeed.Name, CompatFeed.MaxBytes);
    ContentTrust RulesTrust() => new(new AppStore(_dir), new Dictionary<string, string> { ["rules-a"] = Rules.Public });
    static string Signed(string seed, string kid, string name, string body, DateTimeOffset at) => ContentTrust.Sign(Convert.FromBase64String(seed), kid, name, body, at);
    static string Body => Doc(Class("nvidia", null, 9, Shares(gone: 1)));

    [Fact]
    public void Only_a_feed_key_signature_of_compat_is_accepted_and_a_feed_key_signs_nothing_else()
    {
        Assert.NotNull(CompatTrust().Accept(CompatFeed.Name, Signed(Feed.Seed, "feed-a", "compat", Body, T), CompatFeed.TryParse));
        var t = CompatTrust();
        Assert.Null(t.Accept(CompatFeed.Name, Signed(Rules.Seed, "rules-a", "compat", Body, T), CompatFeed.TryParse));   // a rules key
        Assert.Contains("unknown key", t.Rejected);
        Assert.Null(t.Accept(CompatFeed.Name, Signed(Rules.Seed, "feed-a", "compat", Body, T), CompatFeed.TryParse));   // not the pinned key
        Assert.Contains("bad signature", t.Rejected);
        Assert.Null(t.Accept(CompatFeed.Name, Body, CompatFeed.TryParse));   // unsigned
        Assert.Contains("malformed", t.Rejected);
        Assert.Null(t.Accept(CompatFeed.Name, Signed(Feed.Seed, "feed-a", "game-verdicts", Body, T), CompatFeed.TryParse));
        // the feed key's set refuses any other name, and the rules' set refuses the feed key
        Assert.Null(t.Accept(Core.Games.GameVerdicts.Name, Signed(Feed.Seed, "feed-a", Core.Games.GameVerdicts.Name, """{"games":[]}""", T), s => s));
        Assert.Contains("don't sign", t.Rejected);
        var rules = RulesTrust();
        foreach (var name in new[] { Core.Games.GameVerdicts.Name, Core.Games.MaximumMode.Name })
        {
            Assert.Null(rules.Accept(name, Signed(Feed.Seed, "feed-a", name, """{"games":[]}""", T), s => s));
            Assert.Contains("unknown key", rules.Rejected);
        }
        Assert.Null(new ContentTrust(new AppStore(_dir), ContentTrust.CompatKeys, CompatFeed.Name, CompatFeed.MaxBytes)
            .Accept(CompatFeed.Name, Signed(Feed.Seed, "feed-a", "compat", Body, T), CompatFeed.TryParse));   // the shipped slots are empty
    }

    [Fact]
    public void An_older_compat_file_is_refused_as_a_replay()
    {
        Assert.NotNull(CompatTrust().Accept(CompatFeed.Name, Signed(Feed.Seed, "feed-a", "compat", Body, T), CompatFeed.TryParse));
        var t = CompatTrust();   // the next start: the time accepted is kept on disk
        Assert.Null(t.Accept(CompatFeed.Name, Signed(Feed.Seed, "feed-a", "compat", Body, T.AddDays(-1)), CompatFeed.TryParse));
        Assert.Contains("older", t.Rejected);
    }

    [Fact]
    public void Compat_may_be_1_MB_while_the_rules_stay_at_64_KB()
    {
        var games = string.Join(",", Enumerable.Range(0, 1500).Select(i => $$"""{"ids":["steam:{{i}}"],"classes":[{{Class("nvidia", null, 9, Shares(gone: 1))}}]}"""));
        var big = $$"""{"generated":"2026-10-09","min_devices":5,"families":{{Families}},"games":[{{games}}]}""";
        Assert.InRange(Encoding.UTF8.GetByteCount(big), 128 * 1024, 1000 * 1024);
        Assert.NotNull(CompatTrust().Accept(CompatFeed.Name, Signed(Feed.Seed, "feed-a", "compat", big, T), CompatFeed.TryParse));
        var rules = RulesTrust();
        Assert.Null(rules.Accept("plan-rules", Signed(Rules.Seed, "rules-a", "plan-rules", big, T), s => s));
        Assert.Contains("malformed", rules.Rejected);
    }

    [Fact]
    public async Task The_feed_is_kept_while_served_and_dropped_once_the_server_answers_404()
    {
        var cache = Path.Combine(_dir, CompatFeed.FileName);
        string? served = Signed(Feed.Seed, "feed-a", "compat", Body, T);
        var routes = new RouteFailover(new Fake(r => served == null ? Ours(HttpStatusCode.NotFound) : Ours(HttpStatusCode.OK, Encoding.UTF8.GetBytes(served))), [Com], new Clock());
        Assert.NotNull(await CompatFeed.FetchAsync(CompatTrust(), cache, routes));
        Assert.True(File.Exists(cache));
        Assert.NotNull(CompatFeed.Cached(CompatTrust(), cache));   // a start offline

        served = Body;   // unsigned: refused, the kept one stays
        Assert.NotNull(await CompatFeed.FetchAsync(CompatTrust(), cache, routes));
        served = null;
        Assert.Null(await CompatFeed.FetchAsync(CompatTrust(), cache, routes));
        Assert.False(File.Exists(cache));
    }
}
