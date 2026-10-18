using System.Security.Cryptography;
using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// One install, several games (Games/collections.json): one entry each, one recorder in the shared folder.
public partial class AppTests
{
    const string U4 = "steam:1659420", Tll = "steam:1659420:tll";

    const string CollectionJson = """
        { "collections": [ { "games": [
          { "id": "test:col", "name": "First Game", "exes": ["first.exe", "first-l.exe"], "data": "data\\first" },
          { "id": "test:col:second", "name": "Second Game", "exes": ["second.exe", "second-l.exe"], "data": "data\\second" } ] } ] }
        """;

    const string OwnFoldersJson = """
        { "collections": [ { "games": [
          { "id": "test:own", "name": "First Game", "exes": ["first\\bin\\first.exe"], "data": "first" },
          { "id": "test:own:second", "name": "Second Game", "exes": ["second\\bin\\second.exe"], "data": "second" } ] } ] }
        """;

    /// <summary>Indexes each game of the collection with its own shaders, as a reader of its data folder would.</summary>
    sealed class CollectionReader(Dictionary<string, string[]> shaders) : IEngineReader
    {
        public Action<Game, CancellationToken>? Reading;   // tests: called as each index starts
        public EngineInfo? Detect(Game game) => Unreal;
        public ShaderIndex Index(Game game, EngineInfo e, IProgress<string>? log, CancellationToken ct)
        {
            Reading?.Invoke(game, ct);
            return new(Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(game.Id))), ["PCD3D_SM6"], shaders[game.Id].ToDictionary(h => h, _ => (ShaderInfo)null!), []);
        }
        public void ReadShaders(Game game, EngineInfo e, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    [Fact]
    public void A_collection_lists_one_game_per_entry_whose_exe_the_install_has()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Collection")).FullName;
        var app = new Game(U4, "UNCHARTED: Legacy of Thieves Collection", Store.Steam, dir, "", "7");
        Assert.Empty(Collections.Expand(app));   // no exe of its first game: listed as one, the way any app is
        File.WriteAllBytes(Path.Combine(dir, "tll.exe"), [1]);
        Assert.Empty(Collections.Expand(app));
        File.WriteAllBytes(Path.Combine(dir, "u4-l.exe"), [1]);
        Assert.Equal([(U4, "UNCHARTED 4: A Thief's End", "u4-l.exe", "7"), (Tll, "UNCHARTED: The Lost Legacy", "tll.exe", "7")],
            Collections.Expand(app).Select(g => (g.Id, g.Name, Path.GetFileName(g.ExePath), g.Version)));
        File.WriteAllBytes(Path.Combine(dir, "u4.exe"), [1]);
        Assert.Equal("u4.exe", Path.GetFileName(Collections.Expand(app)[0].ExePath));   // the first of its exes there
        Assert.Empty(Collections.Expand(app with { Id = "steam:1" }));
        Assert.Equal(U4, Collections.RecordedBy(Tll));
        Assert.Null(Collections.RecordedBy(U4));
        Assert.Null(Collections.RecordedBy("steam:1"));
        var listed = Collections.Expand(app)[1];
        Assert.Equal(["tll.exe", "tll-l.exe"], Collections.Exes(listed));
        Assert.Null(Collections.Exes(listed with { ExePath = Path.Combine(dir, "launcher.exe") }));   // listed as any game: any exe of its folder
        Assert.Equal(new[] { "u4.exe", "u4-l.exe", "tll.exe", "tll-l.exe" }.Select(x => Path.Combine(dir, x)), Collections.ExesBeside(listed.ExePath));
        Assert.Empty(Collections.ExesBeside(Path.Combine(dir, "launcher.exe")));
        Assert.Equal(2, Collections.Parse(CollectionJson).Single().Count);
        Assert.Equal(2, Collections.Parse(OwnFoldersJson).Single().Count);

        foreach (var bad in new[] { CollectionJson.Replace("test:col:second", "test:other"), CollectionJson.Replace("second-l.exe", @"..\\second-l.exe"),
                     CollectionJson.Replace(@"data\\second", @"..\\second"), CollectionJson.Replace("\"second.exe\", \"second-l.exe\"", ""),
                     CollectionJson.Replace("test:col:second", "test:col"), CollectionJson.Replace("\"second.exe\"", @"""data\\second\\second.exe"""),
                     OwnFoldersJson.Replace(@"second\\bin\\second.exe", @"second\\..\\first\\bin\\first.exe"), OwnFoldersJson.Replace(@"second\\bin\\second.exe", "second.exe"),
                     OwnFoldersJson.Replace(@"second\\bin", @"first\\bin") })
            Assert.Throws<InvalidDataException>(() => Collections.Parse(bad));
    }

    /// <summary>UNCHARTED: Legacy of Thieves Collection, one Steam app: one game each for Uncharted 4 and The Lost Legacy,
    /// whose ids keep the app id for Steam's launch and VAC check.</summary>
    [Fact]
    public void Steam_lists_the_uncharted_collection_as_its_two_games()
    {
        var root = Path.Combine(_root, "Steam");
        var apps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
        var install = Directory.CreateDirectory(Path.Combine(apps, "common", "Uncharted Legacy of Thieves Collection")).FullName;
        foreach (var exe in new[] { "u4.exe", "u4-l.exe", "tll.exe", "tll-l.exe" }) File.WriteAllBytes(Path.Combine(install, exe), new byte[exe.Length]);
        File.WriteAllText(Path.Combine(apps, "appmanifest_1659420.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"1659420\"\n\t\"name\"\t\t\"UNCHARTED: Legacy of Thieves Collection\"\n"
            + "\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"Uncharted Legacy of Thieves Collection\"\n\t\"buildid\"\t\t\"42\"\n}\n");
        var games = new SteamSource(root).Discover();
        Assert.Equal([("steam:1659420", "UNCHARTED 4: A Thief's End"), ("steam:1659420:tll", "UNCHARTED: The Lost Legacy")], games.Select(g => (g.Id, g.Name)));
        Assert.All(games, g => Assert.Equal((install, "42"), (g.InstallDir, g.Version)));
        Assert.All(games, g => Assert.Contains(Path.GetFileName(g.ExePath), Collections.Member(g.Id)!.Value.Game.Exes));
        foreach (var g in games)
        {
            Assert.Equal(1659420u, SteamSource.AppId(g.Id));
            Assert.Equal(1659420u, Vac.AppId(g, root, out var steam));
            Assert.True(steam);
            Assert.Equal("steam://rungameid/1659420", StoreLaunch.Command(g)!.FileName);
        }
        Assert.Null(SteamSource.AppId("steam:x"));
        Assert.Null(SteamSource.AppId("steam:+1"));
        Assert.Null(SteamSource.AppId("epic:1"));
    }

    static string SteamApp(string root, uint id, string dir, params string[] exes)
    {
        var apps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
        var install = Directory.CreateDirectory(Path.Combine(apps, "common", dir)).FullName;
        foreach (var exe in exes.Select(x => Path.Combine(install, x)))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, [1]);
        }
        File.WriteAllText(Path.Combine(apps, $"appmanifest_{id}.acf"),
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{dir}\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"{dir}\"\n\t\"buildid\"\t\t\"42\"\n}}\n");
        return install;
    }

    /// <summary>FINAL FANTASY TACTICS - The Ivalice Chronicles and KINGDOM HEARTS HD 1.5+2.5 ReMIX: one game per exe of the
    /// install folder, sharing its recorder; a launcher of the folder is none of them.</summary>
    [Fact]
    public void Steam_lists_fft_and_kingdom_hearts_as_one_game_per_exe()
    {
        var root = Path.Combine(_root, "Steam");
        var fft = SteamApp(root, 1004640, "FINAL FANTASY TACTICS - The Ivalice Chronicles", "FFT_enhanced.exe", "FFT_classic.exe");
        var kh = SteamApp(root, 2552430, "KINGDOM HEARTS -HD 1.5+2.5 ReMIX-", "KINGDOM HEARTS HD 1.5+2.5 ReMIX.exe", "KINGDOM HEARTS HD 1.5+2.5 Launcher.exe",
            "KINGDOM HEARTS FINAL MIX.exe", "KINGDOM HEARTS Re_Chain of Memories.exe", "KINGDOM HEARTS II FINAL MIX.exe", "KINGDOM HEARTS Birth by Sleep FINAL MIX.exe");
        var games = new SteamSource(root).Discover();
        Assert.Equal([("steam:1004640", "FFT_enhanced.exe"), ("steam:1004640:classic", "FFT_classic.exe"), ("steam:2552430", "KINGDOM HEARTS FINAL MIX.exe"),
                ("steam:2552430:recom", "KINGDOM HEARTS Re_Chain of Memories.exe"), ("steam:2552430:kh2", "KINGDOM HEARTS II FINAL MIX.exe"),
                ("steam:2552430:bbs", "KINGDOM HEARTS Birth by Sleep FINAL MIX.exe")],
            games.Select(g => (g.Id, Path.GetFileName(g.ExePath))));
        Assert.All(games, g => Assert.Equal(g.Id.StartsWith("steam:1004640") ? fft : kh, g.InstallDir));
        Assert.Equal("steam:2552430", Collections.RecordedBy("steam:2552430:bbs"));
        Assert.Equal(4, Collections.ExesBeside(Path.Combine(kh, "KINGDOM HEARTS II FINAL MIX.exe")).Count());
        Assert.All(games, g => Assert.Equal($"steam://rungameid/{SteamSource.AppId(g.Id)}", StoreLaunch.Command(g)!.FileName));
    }

    /// <summary>Mass Effect Legendary Edition: its three games, each in a folder of its own, list as three games with that
    /// folder as their install and a recorder each. The EA app's entry for the Steam install is the same app, so it isn't
    /// listed again.</summary>
    [Fact]
    public async Task Steam_lists_mass_effect_legendary_edition_as_three_games_in_their_own_folders()
    {
        var root = Path.Combine(_root, "Steam");
        var install = SteamApp(root, 1328670, "Mass Effect Legendary Edition", @"Game\Launcher\MassEffectLauncher.exe",
            @"Game\ME1\Binaries\Win64\MassEffect1.exe", @"Game\ME2\Binaries\Win64\MassEffect2.exe", @"Game\ME3\Binaries\Win64\MassEffect3.exe");
        var games = new SteamSource(root).Discover();
        Assert.Equal([("steam:1328670", @"Game\ME3"), ("steam:1328670:me1", @"Game\ME1"), ("steam:1328670:me2", @"Game\ME2")],
            games.Select(g => (g.Id, Path.GetRelativePath(install, g.InstallDir))));
        foreach (var g in games)
        {
            Assert.Equal(Path.Combine(g.InstallDir, "Binaries", "Win64", $"MassEffect{(g.Id.EndsWith(":me1") ? 1 : g.Id.EndsWith(":me2") ? 2 : 3)}.exe"), g.ExePath);
            Assert.Equal(install, Collections.AppDir(g));
            Assert.Null(Collections.Member(g.Id));   // no shared recorder: each game's own
            Assert.Null(Collections.RecordedBy(g.Id));
            Assert.Empty(Collections.ExesBeside(g.ExePath));
            Assert.Equal("steam://rungameid/1328670", StoreLaunch.Command(g)!.FileName);
        }
        Assert.Equal(_game.InstallDir, Collections.AppDir(_game));

        var ea = new Game("ea:Origin.OFR.50.0004096", "Mass Effect Legendary Edition", Store.EA, install, games[0].ExePath);
        var k = new ScsKiller([new SteamSource(root), new FakeSource([ea])], new FakeVendor(Gpu), new FakeReader(null), new FakePlanner(),
            new FakeWarmer(), Path.Combine(_root, "data"), _proxy) { Processes = Ours, ProcessNames = OurNames };
        Assert.Equal(games.Select(g => g.Id).Order(), (await k.ScanAsync(default)).Select(s => s.Game.Id).Order());
        Assert.DoesNotContain(k.Games, s => s.Game.Id == ea.Id);
        k = new ScsKiller([new FakeSource([ea]), new SteamSource(root)], new FakeVendor(Gpu), new FakeReader(null), new FakePlanner(),
            new FakeWarmer(), Path.Combine(_root, "data2"), _proxy) { Processes = Ours, ProcessNames = OurNames };
        Assert.Equal(ea.Id, Assert.Single(await k.ScanAsync(default)).Game.Id);   // the EA app's first: the app's folder is its
    }

    /// <summary>A collection's games: each plays, follows and warms as its own exes only; the shared folder's recorder belongs
    /// to the first game and is stamped for the exe of whichever game ran last; each game's records go to its own recording,
    /// split by the shaders its build ships.</summary>
    [Fact]
    public async Task A_collections_recorder_is_stamped_for_the_game_that_ran_last_and_each_game_gets_its_own_records()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Collection")).FullName;
        var exes = new[] { "u4.exe", "u4-l.exe", "tll.exe", "tll-l.exe" }.Select(x => Path.Combine(dir, x)).ToArray();
        for (var i = 0; i < exes.Length; i++) File.WriteAllBytes(exes[i], new byte[64 + i]);
        var (first, second) = (Collections.Expand(new Game(U4, "UNCHARTED: Legacy of Thieves Collection", Store.Other, dir, "", "1")) is [var a, var b] ? (a, b) : throw new InvalidOperationException());
        byte[] Shader(string name) => SCSKiller.Tests.Planning.MiddlewarePackTests.Container("DXIL", name);
        var (mine, theirs, both) = (Shader("first's"), Shader("second's"), Shader("in both"));
        var reader = new CollectionReader(new()
        {
            [first.Id] = [CommunityTests.Sha1(mine), CommunityTests.Sha1(both)],
            [second.Id] = [CommunityTests.Sha1(theirs), CommunityTests.Sha1(both)],
        });
        string? running = null, stale = null;   // stale: a process the system's list still names after it exited
        var k = Killer(reader, new NeedsRecordingPlanner(), games: [first, second]);
        k.ProcessNames = () => new[] { running, stale }.OfType<string>().Select(n => Path.GetFileNameWithoutExtension(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        k.RunningGameExes = () => running == null ? new HashSet<string>() : new HashSet<string> { Path.GetFileName(running) };
        k.Processes = _ => running == null ? [] : [(4242, 1, Path.GetFileName(running))];
        k.ProcessPath = pid => pid == 4242 ? running : null;
        k.ManageRecorders = true;
        k.IndexRetryInterval = TimeSpan.Zero;
        await k.ScanAsync(default);
        GameState State(Game g) => k.Games.Single(s => s.Game.Id == g.Id);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(dir, ScsKiller.Dll12)));
        Assert.True(State(first).RecorderInstalled);
        Assert.False(State(second).RecorderInstalled);
        Assert.Equal(ScsKiller.SharedRecorder("UNCHARTED 4: A Thief's End"), State(second).RecorderSkip);
        Assert.Null(State(second).RecorderMod);   // the first game's recorder, not a mod in its place
        Assert.True(State(second).RecorderEffective);   // the first game's is in: on for it too
        Assert.Equal("Recorder on: play 5 minutes", Format.ShortNote(State(second)));
        Assert.Throws<InvalidOperationException>(() => k.InstallRecorder(second.Id));
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(dir, ScsKiller.Dll12)));   // turning it on there changes nothing of the first's
        await k.RecorderIndexing;
        Assert.True(Attested(first));
        string ArmedFor() => Path.GetFileName(exes.Single(x => File.ReadAllText(Path.Combine(dir, "scskiller.ini")).Contains($"exe_size={new FileInfo(x).Length}\r\n")));
        Assert.Equal("u4.exe", ArmedFor());

        async Task Launch(string exe, Action? whilePlaying = null)
        {
            k.PollGames();   // a poll before the launch: the run has a start
            running = Path.Combine(dir, exe);
            k.PollGames();
            whilePlaying?.Invoke();
            running = null;
            for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
            await k.RecorderIndexing;
        }

        // the second game's launch: only it plays and follows its exe; the recorder records it from the next launch
        await Launch("tll-l.exe", () =>
        {
            Assert.True(k.IsPlaying(second.Id));
            Assert.False(k.IsPlaying(first.Id));
            Assert.Equal(exes[3], k.Store.LoadGame(second.Id).RunsExe);
            Assert.Null(k.Store.LoadGame(first.Id).RunsExe);
            // what the recorder took meanwhile, of both games
            using var f = File.Create(Path.Combine(dir, "scskiller.db"));
            foreach (var s in new[] { mine, theirs, both })
            {
                PsoDb.WriteBlob(f, CommunityTests.Sha1(s), s);
                PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, CommunityTests.Sha1(s)));
            }
        });
        Assert.Equal("tll-l.exe", ArmedFor());
        Assert.True(Attested(first with { ExePath = exes[3] }));
        Assert.Equal(exes[0], State(first).Game.ExePath);
        Assert.Equal(exes[3], State(second).Game.ExePath);
        Assert.Equal("u4.exe", ScsKiller.WarmExeName(State(first).Game, k.Store.LoadGame(first.Id)));
        Assert.Equal("tll-l.exe", ScsKiller.WarmExeName(State(second).Game, k.Store.LoadGame(second.Id)));
        string[] Computes(Game g) => [.. PsoDb.Read(Path.Combine(k.Store.GameDir(g.Id), "recording.db")).Where(r => r.Tag == 'C').Select(r => PsoDb.Parse(r).Stages.Values.Single()).Order()];
        Assert.Equal(new[] { mine, both }.Select(CommunityTests.Sha1).Order(), Computes(first));
        Assert.Equal(new[] { theirs, both }.Select(CommunityTests.Sha1).Order(), Computes(second));
        Assert.Equal(GameStatus.Ready, State(second).Status);   // planned from its records

        // the first game's launcher-less exe: followed by the first game, and the recorder is stamped for it
        await Launch("u4-l.exe", () =>
        {
            Assert.True(k.IsPlaying(first.Id));
            Assert.False(k.IsPlaying(second.Id));
        });
        Assert.Equal(exes[1], k.Store.LoadGame(first.Id).RunsExe);
        Assert.Equal("u4-l.exe", ArmedFor());
        Assert.Equal(exes[1], State(first).Game.ExePath);
        await k.CheckRecorderGames();
        Assert.True(Attested(State(first).Game));

        // Steam starts the first game's exe, which hands over to the game played before (here the second) and exits
        k.PollGames();
        running = exes[0];
        k.PollGames();
        // the recorder, stamped for u4-l.exe, refuses u4.exe; a refusal for the first game's own exe too
        Directory.CreateDirectory(Path.GetDirectoryName(ScsKiller.RefusedFile(exes[0]))!);
        foreach (var x in exes[..2]) File.WriteAllText(ScsKiller.RefusedFile(x), $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} exe not as checked");
        running = exes[2];
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.True(k.IsPlaying(second.Id));
        Assert.False(k.IsPlaying(first.Id));
        Assert.Equal(exes[1], k.CollectionArmExe(State(first).Game));   // held while a game of the collection plays
        running = null;
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        await k.RecorderIndexing;
        Assert.Equal("tll.exe", ArmedFor());
        Assert.Equal(exes[2], State(second).Game.ExePath);
        Assert.Equal(exes[0], State(first).Game.ExePath);
        // stamped for one exe: the collection's others keep no refusal, and the first game's page shows none
        Assert.All(exes.Where(x => x != exes[2]), x => Assert.False(File.Exists(ScsKiller.RefusedFile(x)), x));
        Assert.Null(State(first).RecorderRefused);

        // Steam again: a recorder pass while u4.exe runs before it hands over, and another after it exited, keep the
        // stamp on tll.exe, the game it starts
        k.PollGames();
        running = exes[0];
        k.PollGames();
        Assert.True(k.IsPlaying(first.Id));
        await k.CheckRecorderGames();
        Assert.Equal("tll.exe", ArmedFor());
        running = exes[2];
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.True(k.IsPlaying(second.Id));
        Assert.False(k.IsPlaying(first.Id));
        await k.CheckRecorderGames();
        Assert.Equal("tll.exe", ArmedFor());

        // the first game's record from before the split: an index of the whole install, made for another exe. Its count
        // isn't shown; its re-index, which the exit's stamp starts, is cancelled by the next launch, and runs at the
        // recorder's first pass after it, also when no stamp follows (the process list named the exe a moment longer)
        var old = k.Store.LoadGame(first.Id);
        (old.IndexExeStamp, old.ShaderCount) = ("1:1", 3);
        k.Store.SaveGame(first.Id, old);
        using var reading = new SemaphoreSlim(0);
        using var go = new SemaphoreSlim(0);
        reader.Reading = (g, ct) =>
        {
            if (g.Id != first.Id) return;
            reading.Release();
            go.Wait();
            ct.ThrowIfCancellationRequested();
        };
        running = null;
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.True(await reading.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(State(first).ShaderCount);
        k.PollGames();
        running = exes[0];
        k.PollGames();
        go.Release();
        await k.RecorderIndexing;
        reader.Reading = null;
        running = exes[2];
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        (running, stale) = (null, exes[2]);
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        await k.RecorderIndexing;
        Assert.Equal("1:1", k.Store.LoadGame(first.Id).IndexExeStamp);
        stale = null;
        await k.CheckRecorderGames();
        await k.RecorderIndexing;
        Assert.Equal(2, k.Store.LoadGame(first.Id).ShaderCount);
        Assert.Equal("tll.exe", ArmedFor());

        // the user switches to the first game: u4.exe, refused while the recorder is stamped for tll.exe, plays it. The
        // stamp moves to it, and its page says why that launch wasn't recorded
        void Refuse(string exe)
        {
            var f = ScsKiller.RefusedFile(exe);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} exe not as checked");
            File.SetLastWriteTimeUtc(f, DateTime.UtcNow);   // the file system's clock is coarser than the run's: it may date it before the start
        }
        await Launch("u4.exe", () => Refuse(exes[0]));
        Assert.Equal("u4.exe", ArmedFor());
        Assert.Equal(ScsKiller.CollectionSwitchNote, State(first).RecorderRefused);
        Assert.True(ScsKiller.NeverRecorded(State(first)));
        // refused while stamped for itself: the game changed since
        await Launch("u4.exe", () => Refuse(exes[0]));
        Assert.Equal("u4.exe", ArmedFor());
        Assert.Equal(ScsKiller.GameChangedNote, State(first).RecorderRefused);

        // the same switch moved by a recorder pass (the process list named u4.exe a moment longer at its exit, so the exit
        // stamped nothing): the note is kept
        await Launch("tll.exe");
        Assert.Equal("tll.exe", ArmedFor());
        k.PollGames();
        running = exes[0];
        k.PollGames();
        Refuse(exes[0]);
        (running, stale) = (null, exes[0]);
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.Equal("tll.exe", ArmedFor());
        stale = null;
        await k.CheckRecorderGames();
        await k.RecorderIndexing;
        Assert.Equal("u4.exe", ArmedFor());
        Assert.Equal(ScsKiller.CollectionSwitchNote, State(first).RecorderRefused);
    }

    /// <summary>Anti-cheat (here VAC) found for a collection's second game: the shared recorder's removal counts for the first
    /// game too, so no stamp from a check of the first that began before it is written.</summary>
    [Fact]
    public async Task A_removal_for_a_collections_second_game_counts_for_the_first_games_stamp()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Collection")).FullName;
        foreach (var x in new[] { "u4.exe", "tll.exe" }) File.WriteAllBytes(Path.Combine(dir, x), new byte[64]);
        var (first, second) = Collections.Expand(new Game(U4, "UNCHARTED: Legacy of Thieves Collection", Store.Other, dir, "", "1")) is [var a, var b] ? (a, b) : throw new InvalidOperationException();
        var k = Killer(new FakeReader(Unreal), games: [first, second]);
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        await k.RecorderIndexing;
        Assert.True(Attested(first));
        var gen = k.RemovalGen(first.Id);

        k.VacCheck = g => g.Id == second.Id;
        await k.CheckRecorderGames();
        Assert.NotEqual(gen, k.RemovalGen(first.Id));
        Assert.False(ScsKiller.IsOurProxy(Path.Combine(dir, ScsKiller.Dll12)));
    }

    sealed class FakeProcesses { public string? Running; }

    /// <summary>The UNCHARTED collection in a test folder, its recorder in and stamped for tll.exe, then Steam's launch:
    /// u4.exe hands over to tll.exe and exits, so u4.exe's run ended last while The Lost Legacy plays.</summary>
    async Task<(ScsKiller K, string[] Exes, Game First, Game Second, FakeProcesses P)> UnchartedPlayingTheLostLegacy()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Collection")).FullName;
        var exes = new[] { "u4.exe", "u4-l.exe", "tll.exe", "tll-l.exe" }.Select(x => Path.Combine(dir, x)).ToArray();
        for (var i = 0; i < exes.Length; i++) File.WriteAllBytes(exes[i], new byte[64 + i]);
        var (first, second) = (Collections.Expand(new Game(U4, "UNCHARTED: Legacy of Thieves Collection", Store.Other, dir, "", "1")) is [var a, var b] ? (a, b) : throw new InvalidOperationException());
        var p = new FakeProcesses();
        var k = UnchartedKiller(first, second, p);
        await k.ScanAsync(default);
        await k.RecorderIndexing;
        Assert.True(Attested(first));
        Assert.Equal("u4.exe", ArmedFor(exes));
        k.PollGames();
        p.Running = exes[2];
        k.PollGames();
        p.Running = null;
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        await k.RecorderIndexing;
        Assert.Equal("tll.exe", ArmedFor(exes));

        k.PollGames();
        p.Running = exes[0];
        k.PollGames();
        p.Running = exes[2];
        for (int i = 0; i < ScsKiller.ExitPolls; i++) k.PollGames();
        Assert.True(k.IsPlaying(second.Id));
        Assert.False(k.IsPlaying(first.Id));
        Assert.True(k.Store.LoadGame(first.Id).LastPlay!.To > k.Store.LoadGame(second.Id).LastPlay!.To);
        Assert.Equal("tll.exe", ArmedFor(exes));
        return (k, exes, first, second, p);
    }

    ScsKiller UnchartedKiller(Game first, Game second, FakeProcesses p)
    {
        static string Hash(string s) => Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)));
        var k = Killer(new CollectionReader(new() { [first.Id] = [Hash("first")], [second.Id] = [Hash("second")] }), new NeedsRecordingPlanner(), games: [first, second]);
        k.ProcessNames = () => p.Running is { } r ? new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileNameWithoutExtension(r) } : [];
        k.RunningGameExes = () => p.Running is { } r ? new HashSet<string> { Path.GetFileName(r) } : [];
        k.Processes = _ => p.Running is { } r ? [(4242, 1, Path.GetFileName(r))] : [];
        k.ProcessPath = pid => pid == 4242 ? p.Running : null;
        k.ManageRecorders = true;
        k.IndexRetryInterval = TimeSpan.Zero;
        return k;
    }

    static string ArmedFor(string[] exes) =>
        Path.GetFileName(exes.Single(x => File.ReadAllText(Path.Combine(Path.GetDirectoryName(x)!, "scskiller.ini")).Contains($"exe_size={new FileInfo(x).Length}\r\n")));

    /// <summary>The app restarts while The Lost Legacy plays: the recorder stays stamped for tll.exe while it runs and after it
    /// exits, though u4.exe's run ended last and the new session never stamped anything.</summary>
    [Fact]
    public async Task A_collections_recorder_stays_on_the_game_playing_across_an_app_restart()
    {
        var (_, exes, first, second, p) = await UnchartedPlayingTheLostLegacy();

        var restarted = UnchartedKiller(first, second, p);
        await restarted.ScanAsync(default);
        await restarted.RecorderIndexing;
        restarted.PollGames();
        Assert.True(restarted.IsPlaying(second.Id));
        Assert.Equal(exes[2], restarted.CollectionArmExe(first));
        await restarted.CheckRecorderGames();
        Assert.Equal("tll.exe", ArmedFor(exes));

        // a run going when the app started has no LastPlay: its exit still counts as the latest
        p.Running = null;
        for (int i = 0; i < ScsKiller.ExitPolls; i++) restarted.PollGames();
        await restarted.RecorderIndexing;
        Assert.Equal(exes[2], restarted.CollectionArmExe(first));
        Assert.True(Attested(first with { ExePath = exes[2] }));
        Assert.Equal("tll.exe", ArmedFor(exes));
    }
}
