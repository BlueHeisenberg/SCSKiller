using System.Text.Json;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// Ray tracing shaders off (Settings.CompileRayTracing, or a game's override): the plan, the warm and the count leave the
// ray tracing state objects out (Planner.LeftOutWithoutRt).
public partial class AppTests
{
    /// <summary>Appends a ray tracing state object of one library to the game's recording, with the library and its root signature.</summary>
    void RecordStateObject(string seed)
    {
        var lib = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", seed);
        var rs = SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", seed + " root signature");
        using var f = new FileStream(Path.Combine(_exeDir, "scskiller.db"), FileMode.Append);
        PsoDb.WriteBlob(f, CommunityTests.Sha1(lib), lib);
        PsoDb.WriteBlob(f, CommunityTests.Sha1(rs), rs);
        var so = new MemoryStream();
        var w = new BinaryWriter(so);
        w.Write(3u); w.Write(2u);
        w.Write(1u); w.Write(Convert.FromHexString(CommunityTests.Sha1(rs)));
        w.Write(5u); w.Write(Convert.FromHexString(CommunityTests.Sha1(lib))); w.Write(0u);
        PsoDb.Write(f, 'R', so.ToArray());
    }

    [Fact]
    public void Ray_tracing_shaders_are_on_by_default_and_for_earlier_settings_and_games()
    {
        Assert.True(AppStore.DefaultSettings.CompileRayTracing);
        Assert.True(JsonSerializer.Deserialize<Settings>("""{"Threads":4,"Priority":"Idle","OnDriverUpdate":"Ask","BackgroundThreads":2,"PauseWhileGaming":true}""", AppStore.Json)!.CompileRayTracing);
        var rec = JsonSerializer.Deserialize<GameRecord>("{}", AppStore.Json)!;
        Assert.Equal((null, true, true), (rec.RayTracing, rec.PlanRayTracing, rec.WarmedRayTracing));
    }

    /// <summary>Off: the warm replays no state object while a RayQuery pipeline stays, one recorded since counts as nothing
    /// new, and nothing asks for a rebuild. On again: stale for the ray tracing only, compiled through "only what isn't
    /// compiled yet", not a full rebuild.</summary>
    [Fact]
    public async Task Ray_tracing_off_leaves_state_objects_out_of_the_warm_and_the_count_and_on_again_compiles_only_them()
    {
        var planner = new FakePlanner(records: [], stats: new PlanStats(0, 10000, 5, 7, true, RtLibraries: 5));
        var k = Killer(new FakeReader(Unreal), planner);
        await k.ScanAsync(default);
        k.Settings = k.Settings with { CompileRayTracing = false };
        await k.SettingsRefresh;
        RecordBuiltAtRunTime("a pipeline that traces rays inline");
        RecordStateObject("first");
        k.RefreshGame(_game.Id);
        List<char>? replayed = null;
        planner.Materialized = () => replayed = planner.MaterializedWith is { } r ? [.. PsoDb.Read(r.DbPath).Select(x => x.Tag)] : [];
        await Compile(k);
        Assert.Equal(false, planner.BuiltRayTracing);
        Assert.Contains('C', replayed!);
        Assert.DoesNotContain('R', replayed!);   // the recording the warm replays

        RecordStateObject("second");
        k.RefreshGame(_game.Id);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, 0L, true), (s.Status, s.RecordedSinceWarm, s.RtSkipped));   // the count: nothing new

        k.Settings = k.Settings with { CompileRayTracing = true };
        await k.SettingsRefresh;
        s = k.Games.Single();
        Assert.Equal((GameStatus.Stale, true, 2L, false), (s.Status, s.NewOnly, s.RecordedSinceWarm, s.RtSkipped));   // both state objects, nothing else
        Assert.Contains(ScsKiller.RtToCompileReason, s.StatusReason);
        await Compile(k);
        Assert.Equal(true, planner.BuiltRayTracing);
        Assert.Equal(2, replayed!.Count(t => t == 'R'));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task A_games_ray_tracing_override_wins_over_the_setting_both_ways()
    {
        var planner = new FakePlanner();
        var k = Killer(new FakeReader(Unreal), planner);
        await k.ScanAsync(default);
        Assert.Equal((false, (bool?)null), (k.Games.Single().RtSkipped, k.Games.Single().RayTracingOverride));

        k.Settings = k.Settings with { CompileRayTracing = false };
        await k.SettingsRefresh;
        Assert.True(k.Games.Single().RtSkipped);
        k.SetGameRayTracing(_game.Id, true);
        Assert.Equal((false, (bool?)true), (k.Games.Single().RtSkipped, k.Games.Single().RayTracingOverride));
        await Compile(k);
        Assert.Equal(true, planner.BuiltRayTracing);

        k.Settings = k.Settings with { CompileRayTracing = true };
        await k.SettingsRefresh;
        k.SetGameRayTracing(_game.Id, false);
        Assert.Equal((true, (bool?)false), (k.Games.Single().RtSkipped, k.Games.Single().RayTracingOverride));
        await Compile(k);
        Assert.Equal(false, planner.BuiltRayTracing);   // its plan was rebuilt without

        k.SetGameRayTracing(_game.Id, null);
        Assert.Equal((false, (bool?)null), (k.Games.Single().RtSkipped, k.Games.Single().RayTracingOverride));
    }

    [Fact]
    public async Task A_game_left_out_of_Add_all_ready_stays_out_across_a_restart_and_is_off_for_earlier_games()
    {
        Assert.False(JsonSerializer.Deserialize<GameRecord>("{}", AppStore.Json)!.LeftOutOfAddAll);
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        var s = k.Games.Single();
        Assert.True(s.Status is GameStatus.Ready or GameStatus.Stale);
        Assert.Equal((false, true), (s.LeftOutOfAddAll, ScsKiller.InAddAllReady(s)));

        k.SetGameLeftOutOfAddAll(_game.Id, true);
        s = k.Games.Single();
        Assert.Equal((true, false), (s.LeftOutOfAddAll, ScsKiller.InAddAllReady(s)));
        Assert.True(k.Store.LoadGame(_game.Id).LeftOutOfAddAll);

        var restarted = Killer(new FakeReader(Unreal));
        await restarted.ScanAsync(default);
        Assert.True(restarted.Games.Single().LeftOutOfAddAll);
        restarted.SetGameLeftOutOfAddAll(_game.Id, false);
        Assert.True(ScsKiller.InAddAllReady(restarted.Games.Single()));
    }
}
