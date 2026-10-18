using System.Net;
using System.Text;
using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

// Rate this compile and the similar-systems line through the app's core, on a fake handler and a throwaway feed key.
public partial class AppTests
{
    static GpuInfo Rtx => Gpu with { Vendor = GpuVendor.Nvidia, Name = "NVIDIA GeForce RTX 4070" };

    async Task<ScsKiller> CompiledStoreBuild(Func<HttpRequestMessage, HttpResponseMessage> answer, List<string> log, FakeSource? source = null)
    {
        var k = Killer(new FakeReader(Unreal), source: source ?? new FakeSource([_game with { Version = "17405238" }]), vendor: new FakeVendor(Rtx));
        k.Sharing = new Sharing(k.Store.DataDir, () => false, new RouteFailover(new CommunityTests.Fake(r =>
        {
            lock (log) log.Add($"{r.RequestUri!.AbsolutePath} {r.Content?.ReadAsStringAsync().Result}");
            return answer(r);
        }), [new("https://api.test.com/")]));
        await k.ScanAsync(default);
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        return k;
    }

    static HttpResponseMessage CompatServer(HttpRequestMessage r) => r.RequestUri!.AbsolutePath switch
    {
        "/v1/devices" => CommunityTests.Ours(HttpStatusCode.OK, """{"device_token":"sd1_test-anon","device_id":"dev1"}"""u8.ToArray()),
        "/v1/compat" => CommunityTests.Ours(HttpStatusCode.OK, """{"replaced":false}"""u8.ToArray()),
        _ => CommunityTests.Ours(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task Send_report_waits_for_a_launch_after_the_compile_then_sends_the_state_and_remembers_the_build()
    {
        var log = new List<string>();
        var k = await CompiledStoreBuild(CompatServer, log);
        var s = k.Games.Single();
        Assert.Equal("Play the game once since it compiled to rate how it ran.", CompatReport.WhyNot(s, "nvidia"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => k.SendReportAsync(_game.Id, CompatChoice.Gone, false, false, false));
        Assert.Empty(log);   // nothing goes before a launch, nor without the click

        var rec = k.Store.LoadGame(_game.Id);
        rec.LastPlay = new(s.WarmedAt!.Value.AddMinutes(1), s.WarmedAt.Value.AddMinutes(30));
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        s = k.Games.Single();
        Assert.Equal(rec.LastPlay.From, s.PlayedAt);
        Assert.Null(CompatReport.WhyNot(s, "nvidia"));
        Assert.Empty(log);
        Assert.Null(k.ReportSent(_game.Id));

        Assert.Null(await k.SendReportAsync(_game.Id, CompatChoice.Better, true, false, true));
        Assert.Equal(["/v1/devices", "/v1/compat"], log.Select(l => l[..l.IndexOf(' ')]));
        var body = JsonDocument.Parse(log[1][(log[1].IndexOf(' ') + 1)..]).RootElement;
        Assert.Equal((_game.Id, _game.Id + "@17405238", "better", true, false, true, "NVIDIA GeForce RTX 4070", "nvidia", "100.01", AppVersion.Current.ToString()),
            (body.GetProperty("game_id").GetString(), body.GetProperty("store_build_key").GetString(), body.GetProperty("choice").GetString(),
             body.GetProperty("frame_gen").GetBoolean(), body.GetProperty("upscaler").GetBoolean(), body.GetProperty("mods").GetBoolean(),
             body.GetProperty("gpu_model").GetString(), body.GetProperty("gpu_vendor").GetString(), body.GetProperty("driver_version").GetString(),
             body.GetProperty("app_version").GetString()));
        Assert.Equal("compiled", body.GetProperty("compile").GetProperty("status").GetString());
        Assert.Equal(CompatChoice.Better, k.ReportSent(_game.Id)!.Choice);

        Assert.Null(await k.SendReportAsync(_game.Id, CompatChoice.Gone, false, false, false));   // again: replaces it
        Assert.Equal(CompatChoice.Gone, k.ReportSent(_game.Id)!.Choice);
        Assert.Equal(3, log.Count);   // the device is kept

        // a later build of the game has no report yet
        var later = Killer(new FakeReader(Unreal), game: _game with { Version = "17500000" }, vendor: new FakeVendor(Rtx));
        await later.ScanAsync(default);
        Assert.Null(later.ReportSent(_game.Id));
        Assert.Null(later.Games.Single().PlayedAt);   // played and compiled as the earlier build: no report for this one
        Assert.NotNull(CompatReport.WhyNot(later.Games.Single(), "nvidia"));
    }

    [Fact]
    public async Task A_game_updated_since_the_scan_isnt_reported_under_either_build()
    {
        var log = new List<string>();
        var source = new FakeSource([_game with { Version = "17405238" }]);
        var k = await CompiledStoreBuild(CompatServer, log, source);
        var rec = k.Store.LoadGame(_game.Id);
        rec.LastPlay = new(k.Games.Single().WarmedAt!.Value.AddMinutes(1), k.Games.Single().WarmedAt!.Value.AddMinutes(2));
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        Assert.Null(CompatReport.WhyNot(k.Games.Single(), "nvidia"));

        source.Games = [_game with { Version = "17500000" }];   // the store updated it while the app runs, no scan since
        await Assert.ThrowsAsync<InvalidOperationException>(() => k.SendReportAsync(_game.Id, CompatChoice.Gone, false, false, false));
        Assert.Empty(log);
        Assert.Equal("17500000", k.Games.Single().Game.Version);   // the page shows the installed build, Rate this compile off
        Assert.NotNull(CompatReport.WhyNot(k.Games.Single(), "nvidia"));

        // the store doesn't list it while it updates: the build can't be confirmed, nothing goes
        source.Games = [];
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => k.SendReportAsync(_game.Id, CompatChoice.Gone, false, false, false));
        Assert.Equal(CompatReport.NoBuild, refused.Message);
        Assert.Empty(log);
    }

    [Fact]
    public async Task A_failed_compile_of_the_installed_build_opens_send_report_without_a_launch()
    {
        var log = new List<string>();
        var source = new FakeSource([_game with { Version = "17405238" }]);
        var k = Killer(new FakeReader(null), source: source, vendor: new FakeVendor(Rtx));   // no engine: the compile is refused
        k.Sharing = new Sharing(k.Store.DataDir, () => false, new RouteFailover(new CommunityTests.Fake(r =>
        {
            lock (log) log.Add($"{r.RequestUri!.AbsolutePath} {r.Content?.ReadAsStringAsync().Result}");
            return CompatServer(r);
        }), [new("https://api.test.com/")]));
        await k.ScanAsync(default);
        Assert.Equal("Compile the game and play it once to rate how it ran.", CompatReport.WhyNot(k.Games.Single(), "nvidia"));   // never compiled

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        Assert.Equal("17405238", k.Store.LoadGame(_game.Id).CompileFailedBuild);   // kept per build in state.json
        var s = k.Games.Single();
        Assert.True(s.CompileFailed);
        Assert.Null(CompatReport.WhyNot(s, "nvidia"));
        Assert.Null(await k.SendReportAsync(_game.Id, CompatChoice.NotCompiled, false, false, false));
        var body = JsonDocument.Parse(log[^1][(log[^1].IndexOf(' ') + 1)..]).RootElement;
        Assert.Equal("not_compiled", body.GetProperty("choice").GetString());
        Assert.Contains(body.GetProperty("compile").GetProperty("status").GetString(), new[] { "failed", "not_supported" });

        source.Games = [_game with { Version = "17500000" }];   // another build: that failure isn't its
        await k.ScanAsync(default);
        Assert.False(k.Games.Single().CompileFailed);
        Assert.NotNull(CompatReport.WhyNot(k.Games.Single(), "nvidia"));
    }

    [Fact]
    public async Task A_failed_compile_counts_for_the_build_it_compiled_when_the_store_updated_it_meanwhile()
    {
        var source = new FakeSource([_game with { Version = "17405238" }]);
        var k = Killer(new FakeReader(null), source: source, vendor: new FakeVendor(Rtx));
        await k.ScanAsync(default);
        source.Games = [_game with { Version = "17500000" }];   // updated while the app runs, no scan since
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        Assert.Equal("17500000", k.Store.LoadGame(_game.Id).CompileFailedBuild);
        Assert.Equal(("17500000", true), (k.Games.Single().Game.Version, k.Games.Single().CompileFailed));
        Assert.Null(CompatReport.WhyNot(k.Games.Single(), "nvidia"));
    }

    [Fact]
    public async Task A_complete_compile_clears_an_earlier_failure()
    {
        var game = _game with { Version = "17405238" };
        var failed = Killer(new FakeReader(null), game: game, vendor: new FakeVendor(Rtx));
        await failed.ScanAsync(default);
        failed.Enqueue(_game.Id);
        failed.StartQueue();
        await failed.WhenQueueIdle().WaitAsync(HangGuard);
        Assert.True(failed.Games.Single().CompileFailed);

        var k = Killer(new FakeReader(Unreal), game: game, vendor: new FakeVendor(Rtx));
        await k.RescanAsync(default);   // the engine is found now
        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
        Assert.Null(k.Store.LoadGame(_game.Id).CompileFailedBuild);
        Assert.False(k.Games.Single().CompileFailed);
        Assert.Equal("Play the game once since it compiled to rate how it ran.", CompatReport.WhyNot(k.Games.Single(), "nvidia"));
    }

    [Fact]
    public async Task A_refused_report_isnt_remembered()
    {
        var log = new List<string>();
        var k = await CompiledStoreBuild(r => r.RequestUri!.AbsolutePath == "/v1/compat" ? CommunityTests.Ours(HttpStatusCode.TooManyRequests) : CompatServer(r), log);
        var rec = k.Store.LoadGame(_game.Id);
        rec.LastPlay = new(k.Games.Single().WarmedAt!.Value.AddMinutes(1), k.Games.Single().WarmedAt!.Value.AddMinutes(2));
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        Assert.Equal("Not sent. Try again later.", await k.SendReportAsync(_game.Id, CompatChoice.Worse, false, false, false));
        Assert.Null(k.ReportSent(_game.Id));
    }

    [Fact]
    public async Task The_similar_systems_line_comes_from_a_verified_feed_and_is_kept_with_the_list()
    {
        var key = FeedTrust.NewKey();
        const string body = """
            {"generated":"2026-10-09","min_devices":5,"families":[{"vendor":"nvidia","family":"nvidia-ada","match":["RTX 40"]}],
             "games":[{"ids":["GAME"],"classes":[{"vendor":"nvidia","family":"nvidia-ada","devices":12,
               "shares":{"gone":0.1,"better":0.1,"no_help":0.3,"worse":0.2,"no_start":0.05,"not_compiled":0,"no_stutter_before":0.3}}]}]}
            """;
        var signed = ContentTrust.Sign(Convert.FromBase64String(key.Seed), "feed-a", CompatFeed.Name, body.Replace("GAME", _game.Id), DateTimeOffset.UtcNow);
        var fake = new CommunityTests.Fake(r => r.RequestUri!.AbsolutePath == "/" + CompatFeed.ContentPath
            ? CommunityTests.Ours(HttpStatusCode.OK, Encoding.UTF8.GetBytes(signed)) : CommunityTests.Ours(HttpStatusCode.NotFound));

        var off = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Rtx));   // the shipped key slots are empty: not even asked for
        off.ContentRoutes = new RouteFailover(fake, [new("https://api.test.com/")]);
        await off.ScanAsync(default);
        await off.StutterUpdate;
        Assert.DoesNotContain(fake.Log, l => l.Contains(CompatFeed.FileName));
        Assert.Null(off.Games.Single().Similar);

        Directory.Delete(Path.Combine(_root, "data"), true);
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Rtx));
        k.CompatKeys = new Dictionary<string, string> { ["feed-a"] = key.Public };
        k.ContentRoutes = new RouteFailover(fake, [new("https://api.test.com/")]);
        await k.ScanAsync(default);
        await k.StutterUpdate;
        Assert.Equal(SimilarSystems.Warning, k.Games.Single().Similar);
        Assert.Equal(SimilarSystems.Warning, k.Store.LoadList()!.Games.Single().Similar);   // a start without a scan has it too

        var offline = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Rtx));
        offline.CompatKeys = k.CompatKeys;
        await offline.ScanAsync(default);   // no routes: the kept copy
        Assert.Equal(SimilarSystems.Warning, offline.Games.Single().Similar);
        var amd = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd, Name = "AMD Radeon RX 7800 XT" }));
        amd.CompatKeys = k.CompatKeys;
        await amd.ScanAsync(default);
        Assert.Null(amd.Games.Single().Similar);   // no row for AMD
    }

    /// <summary>games.json from before reports: its states read with neither member set.</summary>
    [Fact]
    public async Task A_kept_list_round_trips_the_report_members_and_reads_one_without_them()
    {
        var k = Killer(new FakeReader(Unreal));
        var states = await k.ScanAsync(default);
        var played = new DateTimeOffset(2026, 10, 9, 12, 0, 0, TimeSpan.FromHours(2));
        k.Store.SaveList(new KeptList("old", null, states.Select(s => s with { PlayedAt = played, Similar = SimilarSystems.NoHelp, CompileFailed = true }).ToList()));
        var kept = k.Store.LoadList()!.Games.Single();
        Assert.Equal((played, SimilarSystems.NoHelp, true), (kept.PlayedAt, kept.Similar, kept.CompileFailed));

        var file = Path.Combine(k.Store.DataDir, "games.json");
        var doc = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var g in doc["Games"]!.AsArray()) { g!.AsObject().Remove("PlayedAt"); g.AsObject().Remove("Similar"); g.AsObject().Remove("CompileFailed"); }
        File.WriteAllText(file, doc.ToJsonString());
        kept = k.Store.LoadList()!.Games.Single();
        Assert.Equal(((DateTimeOffset?)null, (SimilarSystems?)null, false, states.Single().Game), (kept.PlayedAt, kept.Similar, kept.CompileFailed, kept.Game));
    }
}
