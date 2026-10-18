using System.Collections.Concurrent;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.Tests.Platform;

// The library during a scan: games listed as soon as they're found, each updated as it's read, Add game listed at once.
public partial class AppTests
{
    /// <summary>Unreal for every game; a Detect of <paramref name="held"/> (every game when null) waits for <see cref="Gate"/>.</summary>
    sealed class GateReader(string? held = null) : IEngineReader
    {
        public readonly ManualResetEventSlim Gate = new();
        public readonly ConcurrentDictionary<string, int> Detects = new();
        public int Of(string id) => Detects.GetValueOrDefault(id);
        public EngineInfo? Detect(Game game)
        {
            Detects.AddOrUpdate(game.Id, 1, (_, n) => n + 1);
            if (held == null || held == game.Id) Gate.Wait(HangGuard);
            return Unreal;
        }
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct) => new("content-1", ["PCD3D_SM6"], new Dictionary<string, ShaderInfo>(), []);
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    /// <summary>A second game, named to sort before the fixture's.</summary>
    Game Another()
    {
        var dir = Path.Combine(_root, "Another Game");
        Directory.CreateDirectory(dir);
        var exe = Path.Combine(dir, "Another.exe");
        File.WriteAllBytes(exe, new byte[4096]);
        return new Game("test:another-" + Guid.NewGuid().ToString("N")[..8], "Another Game", Store.Other, dir, exe);
    }

    [Fact]
    public async Task A_first_scan_lists_every_game_before_it_reads_them_and_updates_each_as_it_is_read()
    {
        var other = Another();
        var reader = new GateReader(held: _game.Id);
        var k = Killer(reader, games: [other, _game]);
        var scan = k.ScanAsync(default);
        await Until(() => k.Games.Count == 2 && k.Games.Single(s => s.Game.Id == other.Id).Engine != null);   // the first one read, the next held
        var held = k.Games.Single(s => s.Game.Id == _game.Id);
        Assert.True(ScsKiller.IsUnchecked(held));
        Assert.Equal((GameStatus.Unsupported, ScsKiller.CheckingReason, ScsKiller.SkipUnsupported), (held.Status, held.StatusReason, held.RecorderSkip));
        Assert.False(Library.Shows(held, LibraryShow.NotSupported));
        Assert.False(k.Store.LoadScan().ContainsKey(_game.Id));   // nothing of it is kept until it's read

        reader.Gate.Set();
        await scan.WaitAsync(HangGuard);
        Assert.All(k.Games, s => Assert.Equal(("Unreal", false), (s.Engine?.Family, ScsKiller.IsUnchecked(s))));
        Assert.Equal("Unreal", k.Store.LoadScan()[_game.Id].Engine?.Family);
    }

    [Fact]
    public async Task A_rescan_keeps_each_game_as_it_was_listed_until_it_is_read_again()
    {
        var reader = new GateReader();
        reader.Gate.Set();
        var k = Killer(reader);
        await k.ScanAsync(default);
        var before = k.Games.Single();
        reader.Gate.Reset();
        var scan = k.RescanAsync(default);
        await Until(() => reader.Of(_game.Id) == 2);
        Assert.Same(before, k.Games.Single());
        reader.Gate.Set();
        await scan.WaitAsync(HangGuard);
        Assert.Equal("Unreal", k.Games.Single().Engine?.Family);
    }

    /// <summary>The game page of a game the scan hasn't reached reads it at once; the scan's later read never puts a placeholder back.</summary>
    [Fact]
    public async Task Opening_a_game_the_first_scan_has_not_reached_reads_it_at_once()
    {
        var other = Another();
        var reader = new GateReader(held: other.Id);
        var k = Killer(reader, games: [other, _game]);
        var scan = k.ScanAsync(default);
        await Until(() => reader.Of(other.Id) == 1);
        Assert.True(ScsKiller.IsUnchecked(k.Games.Single(s => s.Game.Id == _game.Id)));
        k.CheckGame(_game.Id);
        Assert.Equal("Unreal", k.Games.Single(s => s.Game.Id == _game.Id).Engine?.Family);
        reader.Gate.Set();
        await scan.WaitAsync(HangGuard);
        Assert.All(k.Games, s => Assert.False(ScsKiller.IsUnchecked(s)));
        Assert.Equal(1, reader.Of(_game.Id));   // the scan reused what the page read
    }

    /// <summary>#116: a game added while the first scan reads the others is listed at once and stays listed through that
    /// scan's end; the refresh Add game starts waits for it instead of reading the same games again.</summary>
    [Fact]
    public async Task A_game_added_during_the_first_scan_is_listed_at_once_and_stays_listed()
    {
        var (_, stub, _) = ManualGamesTests.UnrealLayout(Path.Combine(_root, "Added Game"));
        var reader = new GateReader(held: _game.Id);
        var k = Killer(reader, sources: [new FakeSource([_game]), Manual()]);
        var first = k.ScanAsync(default);
        await Until(() => reader.Of(_game.Id) == 1);

        var added = k.AddManualGame(stub).Game;
        Assert.Equal("Unreal", k.Games.Single(s => s.Game.Id == added.Id).Engine?.Family);
        var second = k.ScanAsync(default);   // the library's refresh after Add game

        reader.Gate.Set();
        await first.WaitAsync(HangGuard);
        Assert.Contains(k.Games, s => s.Game.Id == added.Id);   // the first scan, which never found it, didn't take it out
        await second.WaitAsync(HangGuard);
        Assert.Equal([added.Id, _game.Id], k.Games.Select(s => s.Game.Id).Order());
        Assert.Equal((1, 1), (reader.Of(_game.Id), reader.Of(added.Id)));
    }

    /// <summary>A placeholder kept in games.json (a list saved while a scan ran) is read at the next start with Scan at start off.</summary>
    [Fact]
    public async Task A_kept_placeholder_is_read_at_a_start_that_reads_no_game()
    {
        var reader = new FakeReader(Unreal);
        var k = Killer(reader);
        k.Settings = k.Settings with { ScanAtStart = false };
        await k.ScanAsync(default);
        var kept = k.Store.LoadList()!;
        k.Store.SaveList(kept with { Games = [kept.Games.Single() with { Engine = null, Status = GameStatus.Unsupported, StatusReason = ScsKiller.CheckingReason }] });

        var next = Killer(reader);
        await next.ScanAsync(default);
        Assert.Equal(("Unreal", false), (next.Games.Single().Engine?.Family, ScsKiller.IsUnchecked(next.Games.Single())));
    }

    /// <summary>A placeholder's Unsupported never reads as "take the recorder out".</summary>
    [Fact]
    public async Task A_game_listed_before_it_is_read_keeps_its_recorder()
    {
        var k = Killer(new FakeReader(Unreal));
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var files = k.Store.LoadGame(_game.Id).RecorderFiles.Keys.Order().ToList();
        File.Delete(Path.Combine(k.Store.DataDir, "games.json"));   // no last list to show: the next start lists placeholders

        var reader = new GateReader();
        var next = Killer(reader);
        next.ProcessNames = () => new HashSet<string>();
        next.ManageRecorders = true;
        var scan = next.RescanAsync(default);
        await Until(() => reader.Of(_game.Id) == 1);
        Assert.True(ScsKiller.IsUnchecked(next.Games.Single()));
        next.ReconcileRecorders();
        Assert.True(File.Exists(dll));
        Assert.Equal(files, next.Store.LoadGame(_game.Id).RecorderFiles.Keys.Order().ToList());

        reader.Gate.Set();
        await scan.WaitAsync(HangGuard);
        Assert.True(File.Exists(dll));
        Assert.True(next.Games.Single().RecorderInstalled);
    }

    [Fact]
    public async Task Hiding_a_game_listed_before_it_is_read_takes_its_recorder_out()
    {
        var other = Another();
        var k = Killer(new FakeReader(Unreal), games: [other, _game]);
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        File.Delete(Path.Combine(k.Store.DataDir, "games.json"));

        var reader = new GateReader(held: other.Id);
        var next = Killer(reader, games: [other, _game]);
        next.ProcessNames = () => new HashSet<string>();
        var scan = next.RescanAsync(default);
        await Until(() => reader.Of(other.Id) == 1);
        Assert.True(ScsKiller.IsUnchecked(next.Games.Single(s => s.Game.Id == _game.Id)));
        next.HideGame(_game.Id);
        Assert.False(File.Exists(dll));

        reader.Gate.Set();
        await scan.WaitAsync(HangGuard);
        Assert.DoesNotContain(next.Games, s => s.Game.Id == _game.Id);
    }

    [Fact]
    public async Task A_game_listed_before_it_is_read_is_not_hidden_while_its_recorder_can_not_be_checked()
    {
        var other = Another();
        var k = Killer(new FakeReader(Unreal), games: [other, _game]);
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(_game.Id);
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        File.Delete(Path.Combine(k.Store.DataDir, "games.json"));
        SetWriteTime(_game.ExePath, Unreadable);

        var reader = new GateReader(held: other.Id);
        var next = Killer(reader, games: [other, _game]);
        next.ProcessNames = () => new HashSet<string>();
        var scan = next.RescanAsync(default);
        await Until(() => reader.Of(other.Id) == 1);
        Assert.Throws<InvalidOperationException>(() => next.HideGame(_game.Id));
        Assert.True(File.Exists(dll));
        Assert.False(next.Store.LoadGame(_game.Id).Hidden);

        reader.Gate.Set();
        await scan.WaitAsync(HangGuard);
        Assert.Contains(next.Games, s => s.Game.Id == _game.Id);
    }
}
