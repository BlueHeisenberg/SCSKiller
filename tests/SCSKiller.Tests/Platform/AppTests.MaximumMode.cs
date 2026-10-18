using System.Text.Json.Nodes;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

// Maximum mode suggested per game (Games.MaximumMode) and turned on for one game only.
public partial class AppTests
{
    MaximumMode Suggesting(string vendors) => MaximumMode.TryParse(
        $$"""{"about":null,"games":[{"name":"Fake Game","ids":["{{_game.Id}}"],"vendors":[{{vendors}}],"source":"","date":"2026-10-01","note":"Removed the last stutters"}]}""")!;

    [Fact]
    public async Task A_suggested_game_turns_maximum_on_for_itself_only_and_compiles_its_pairings()
    {
        var planner = new FakePlanner();
        var amd = new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }, perStage: true);
        var k = Killer(new FakeReader(Unreal), planner, vendor: amd);
        try
        {
            MaximumMode.Current = Suggesting("\"nvidia\"");
            await k.ScanAsync(default);
            Assert.Null(k.Games.Single().MaximumSuggestion);   // listed for the other vendor

            MaximumMode.Current = Suggesting("\"amd\"");
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(HangGuard);
            var s = k.Games.Single();
            Assert.Equal((GameStatus.Warmed, "Removed the last stutters", null, false), (s.Status, s.MaximumSuggestion, s.MaximumForGame, planner.BuiltMaximum));
            Assert.False(k.Settings.MaximumPlans);

            k.SetGameMaximum(_game.Id, true);
            s = k.Games.Single();
            Assert.Equal((GameStatus.Stale, "Maximum mode: every shader pairing is still to compile", null, true), (s.Status, s.StatusReason, s.MaximumSuggestion, s.MaximumForGame));
            Assert.False(k.Settings.MaximumPlans);   // the setting stays off for every other game
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(HangGuard);
            Assert.Equal((GameStatus.Warmed, true), (k.Games.Single().Status, planner.BuiltMaximum));

            k.SetGameMaximum(_game.Id, false);
            s = k.Games.Single();
            Assert.Equal(("Removed the last stutters", null), (s.MaximumSuggestion, s.MaximumForGame));
        }
        finally { MaximumMode.Current = MaximumMode.Embedded; }
    }

    /// <summary>The game page's Max compile: one compile with the Maximum plan, nothing kept for the game or in Settings. The
    /// game isn't stale for having compiled every pairing, and the next compile is a standard one.</summary>
    [Fact]
    public async Task Max_compile_compiles_every_pairing_once_and_the_next_compile_is_standard()
    {
        var planner = new FakePlanner();
        var k = Killer(new FakeReader(Unreal), planner, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }, perStage: true));
        await k.ScanAsync(default);
        k.CompileMax(_game.Id);
        Assert.Equal(_game.Id, Assert.Single(k.Queue).GameId);
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        var s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, true, (bool?)null, false), (s.Status, planner.BuiltMaximum, s.MaximumForGame, k.Settings.MaximumPlans));

        k.Compile(_game.Id);
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        s = k.Games.Single();
        Assert.Equal((GameStatus.Warmed, false, (bool?)null), (s.Status, planner.BuiltMaximum, s.MaximumForGame));
    }

    /// <summary>A Max compile asked while a compile of the game plans (a plan check the request promotes) plans again as a
    /// Maximum one, instead of warming the standard plan and forgetting the request.</summary>
    [Fact]
    public async Task A_max_compile_asked_while_the_game_plans_compiles_the_maximum_plan()
    {
        using var planning = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var builds = 0;
        var planner = new FakePlanner(onBuild: () => { if (Interlocked.Increment(ref builds) == 1) { planning.Set(); release.Wait(HangGuard); } });
        var k = Killer(new FakeReader(Unreal), planner, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }, perStage: true));
        await k.ScanAsync(default);
        k.Compile(_game.Id);
        Assert.True(planning.Wait(HangGuard));
        k.CompileMax(_game.Id);
        release.Set();
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        Assert.Equal((2, true, GameStatus.Warmed), (builds, planner.BuiltMaximum, k.Games.Single().Status));
    }

    [Fact]
    public async Task Maximum_on_for_every_game_suggests_nothing()
    {
        var k = Killer(new FakeReader(Unreal), vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Nvidia }, perStage: true));
        try
        {
            MaximumMode.Current = Suggesting("\"amd\",\"nvidia\"");
            await k.ScanAsync(default);
            Assert.Equal("Removed the last stutters", k.Games.Single().MaximumSuggestion);
            k.Settings = k.Settings with { MaximumPlans = true };
            await k.SettingsRefresh;
            Assert.Null(k.Games.Single().MaximumSuggestion);
        }
        finally { MaximumMode.Current = MaximumMode.Embedded; }
    }

    /// <summary>games.json from before the Maximum suggestion: its states read with neither member set.</summary>
    [Fact]
    public async Task A_kept_list_without_the_maximum_members_reads_as_before()
    {
        var k = Killer(new FakeReader(Unreal));
        var states = await k.ScanAsync(default);
        k.Store.SaveList(new KeptList("old", null, states.Select(s => s with { MaximumSuggestion = "x", MaximumForGame = true }).ToList()));
        var file = Path.Combine(k.Store.DataDir, "games.json");
        var doc = JsonNode.Parse(File.ReadAllText(file))!;
        foreach (var g in doc["Games"]!.AsArray()) { g!.AsObject().Remove("MaximumSuggestion"); g.AsObject().Remove("MaximumForGame"); }
        File.WriteAllText(file, doc.ToJsonString());
        var kept = k.Store.LoadList()!.Games.Single();
        Assert.Equal((null, (bool?)null, states.Single().Game), (kept.MaximumSuggestion, kept.MaximumForGame, kept.Game));
    }

    [Fact]
    public async Task On_amd_a_game_shows_what_maximum_would_add_at_most_and_nothing_once_on_or_on_nvidia()
    {
        long Pairing = Core.Planning.PlanBuilder.ExtraBytes;
        async Task<long?> Adds(GpuVendor vendor, long recorded, long generated, bool on = false)
        {
            if (Directory.Exists(Path.Combine(_root, "data"))) Directory.Delete(Path.Combine(_root, "data"), true);   // each case plans afresh
            var k = Killer(new FakeReader(Unreal), new FakePlanner(stats: new PlanStats(recorded, generated, 0, 7, true)),
                vendor: new FakeVendor(Gpu with { Vendor = vendor }, perStage: true) { StateIndependent = vendor != GpuVendor.Amd });
            await k.ScanAsync(default);
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(HangGuard);
            if (on) k.SetGameMaximum(_game.Id, true);
            return k.Games.Single().MaximumAddsBytes;
        }
        Assert.Equal((1_000 + 20_000) * Pairing, await Adds(GpuVendor.Amd, 1_000, 20_000));   // the plan's own size
        Assert.Equal(Core.Planning.PlanBuilder.ExtraBudget / Pairing * Pairing, await Adds(GpuVendor.Amd, 10_000, 300_000));   // 2 GiB
        Assert.Null(await Adds(GpuVendor.Amd, 1_000, 20_000, on: true));
        Assert.Null(await Adds(GpuVendor.Nvidia, 1_000, 20_000));
    }

    [Fact]
    public async Task An_amd_maximum_plan_from_before_the_cap_is_built_again_and_a_choice_is_kept_for_a_start_without_a_scan()
    {
        var builds = 0;
        var planner = new FakePlanner(onBuild: () => Interlocked.Increment(ref builds));
        var k = Killer(new FakeReader(Unreal), planner, vendor: new FakeVendor(Gpu with { Vendor = GpuVendor.Amd }, perStage: true) { StateIndependent = false });
        await k.ScanAsync(default);
        k.SetGameMaximum(_game.Id, true);
        Assert.True(k.Store.LoadList()!.Games.Single().MaximumForGame);   // kept: a start with Scan at start off shows it

        async Task Compile()
        {
            k.Enqueue(_game.Id);
            k.StartQueue();
            await k.WhenQueueIdle().WaitAsync(HangGuard);
        }
        await Compile();
        Assert.Equal(1, builds);
        await Compile();
        Assert.Equal(1, builds);   // the capped plan is current

        var rec = k.Store.LoadGame(_game.Id);
        rec.PlanMaximumCapped = null;   // as an earlier build left it
        k.Store.SaveGame(_game.Id, rec);
        await Compile();
        Assert.Equal(2, builds);
    }
}
