using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;

namespace SCSKiller.Tests.Platform;

// Several SCSKiller copies on one PC (installed, portable, test), each with its own data folder, sharing game folders and the
// one recorder key: each touches only the recorders it placed or the user turned on in it.
public partial class AppTests
{
    /// <summary>A copy with its own data folder under the test's; null: the installed one.</summary>
    ScsKiller Copy(string? name, Game[] games)
    {
        ScsKiller.LedgerDir = CopyLedger(name);
        var k = Killer(new FakeReader(Unreal), games: games, data: Path.Combine(_root, name ?? "data"));
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        return k;
    }

    /// <summary>The copy's identity the marker beside its recorders names (its data folder's armed\).</summary>
    string CopyLedger(string? name) => name == null ? ScsKiller.DefaultLedger : Path.Combine(_root, name, "armed");

    /// <summary>What a copy does at a start and while it runs: a scan, a reconcile, a recorder pass, and a clear of its
    /// recordings; its recorder indexes in the background have ended.</summary>
    static async Task Cycle(ScsKiller k, Game[] games)
    {
        await k.ScanAsync(default);
        k.ReconcileRecorders();
        await k.CheckRecorderGames();
        foreach (var g in games) k.ClearRecording(g.Id);
        await k.RecorderIndexing.WaitAsync(HangGuard);
    }

    /// <summary>A play session's records in the game folder's inbox, not imported yet.</summary>
    static void Played(Game g)
    {
        using var f = File.Create(Path.Combine(Path.GetDirectoryName(g.ExePath)!, "scskiller.db"));
        PsoDb.Write(f, 'C', PsoDb.Compute(PsoDb.Zero, new string('c', 40)));
    }

    /// <summary>Every file under the folders, by its bytes.</summary>
    static Dictionary<string, string> Files(params string[] dirs) =>
        dirs.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories)).Distinct(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(f => f, f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), StringComparer.OrdinalIgnoreCase);

    static int RecordedCount(ScsKiller k, Game g) => File.Exists(Path.Combine(k.Store.GameDir(g.Id), "recording.db"))
        ? PsoDb.Read(Path.Combine(k.Store.GameDir(g.Id), "recording.db")).Count(r => r.Tag == 'C') : 0;

    static long Inbox(Game g) => new FileInfo(Path.Combine(Path.GetDirectoryName(g.ExePath)!, "scskiller.db")) is { Exists: true } f ? f.Length : 0;

    /// <summary>Stamped for recording once the copy's background work ended.</summary>
    static async Task<bool> StampedSettled(ScsKiller k, Game g)
    {
        await k.RecorderIndexing.WaitAsync(HangGuard);
        return Attested(g);
    }

    /// <summary>Copy A places the recorder and the game plays with it; copy B, with a fresh data folder, scans, reconciles and
    /// runs its recorder pass: A's recorder, its signed ini, the marker naming A and the recording waiting in the game folder
    /// stay byte for byte, and B's record names no recorder there. A's next scan imports the recording.</summary>
    [Fact]
    public async Task A_second_copy_never_takes_rekeys_or_imports_the_recorder_another_copy_placed()
    {
        var a = Copy("a", [_game]);
        await a.ScanAsync(default);
        Assert.True(await StampedSettled(a, _game));
        Assert.Equal(Path.GetFullPath(CopyLedger("a")), File.ReadAllText(Path.Combine(_exeDir, ScsKiller.LedgerStamp)));
        Played(_game);
        var before = Files(_game.InstallDir);

        var b = Copy("b", [_game]);
        await Cycle(b, [_game]);
        b.ReconcileRecorders(_game.Id);

        Assert.Equal(before, Files(_game.InstallDir));
        var rec = b.Store.LoadGame(_game.Id);
        Assert.Null(rec.RecorderExe);
        Assert.Empty(rec.RecorderFiles);
        Assert.NotEqual(RecorderOverride.On, rec.Recorder);
        Assert.Equal(0, RecordedCount(b, _game));
        Assert.False(b.Games.Single().RecorderInstalled);
        Assert.Single(SharedLog(Path.Combine(_root, "b", "recorders.log")).Split('\n'), l => l.Contains("recorder placed by another SCSKiller copy"));

        ScsKiller.LedgerDir = CopyLedger("a");
        await a.ScanAsync(default);
        Assert.Equal(1, RecordedCount(a, _game));
        Assert.Equal(0, Inbox(_game));
        Assert.True(await StampedSettled(a, _game));
    }

    /// <summary>A fresh portable copy next to the installed copy's stamped recorder and an older one placed before recorders
    /// were stamped, both with records in their inbox: nothing in either folder changes, nothing is adopted or imported. The
    /// installed copy then adopts and imports both, as before.</summary>
    [Fact]
    public async Task A_fresh_copy_with_its_own_data_folder_changes_nothing_of_the_installed_copys()
    {
        var old = FakeGame("test:old", "Old");
        var installed = Copy(null, [_game]);
        await installed.ScanAsync(default);
        Assert.True(await StampedSettled(installed, _game));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.LedgerStamp)));   // the installed copy marks nothing
        Played(_game);
        File.Copy(_proxy, Dll(old));
        Played(old);
        foreach (var g in new[] { _game, old })   // the installed copy's keys and its last compile's warmed list, which the other copy's keys upkeep would rewrite and delete
            foreach (var f in new[] { Recordings.KeysFile, Recordings.WarmedFile })
                File.WriteAllText(Path.Combine(Path.GetDirectoryName(g.ExePath)!, f), f);
        var before = Files(_game.InstallDir, old.InstallDir);

        var portable = Copy("portable", [_game, old]);
        await Cycle(portable, [_game, old]);

        Assert.Equal(before, Files(_game.InstallDir, old.InstallDir));
        Assert.Null(portable.Games.Single(s => s.Game.Id == _game.Id).RecorderSkip);   // not taken for a mod: it can be turned on to take it over
        foreach (var g in new[] { _game, old })
        {
            Assert.Equal(0, RecordedCount(portable, g));
            Assert.NotEqual(RecorderOverride.On, portable.Store.LoadGame(g.Id).Recorder);
            Assert.False(portable.Games.Single(s => s.Game.Id == g.Id).RecorderInstalled);
        }

        var again = Copy(null, [_game, old]);
        await again.ScanAsync(default);
        Assert.Equal(RecorderOverride.On, again.Store.LoadGame(old.Id).Recorder);
        foreach (var g in new[] { _game, old })
        {
            Assert.Equal(1, RecordedCount(again, g));
            Assert.Equal(0, Inbox(g));
        }
        Assert.True(await StampedSettled(again, old));
    }

    /// <summary>A recorder a portable copy placed is that copy's (its marker names it): that copy stamps it and imports its
    /// recording; another portable copy and the installed one leave the folder as it is.</summary>
    [Fact]
    public async Task A_recorder_is_adopted_by_the_copy_that_placed_it_and_ignored_by_the_others()
    {
        var a = Copy("a", [_game]);
        await a.ScanAsync(default);
        Assert.True(await StampedSettled(a, _game));
        Played(_game);
        var before = Files(_game.InstallDir);

        await Cycle(Copy("b", [_game]), [_game]);
        await Cycle(Copy(null, [_game]), [_game]);
        Assert.Equal(before, Files(_game.InstallDir));

        ScsKiller.LedgerDir = CopyLedger("a");
        await a.ScanAsync(default);
        Assert.Equal(1, RecordedCount(a, _game));
        Assert.Equal(0, Inbox(_game));
        Assert.True(await StampedSettled(a, _game));
    }

    /// <summary>A data folder copied whole, as a user copies the installed copy's to start a portable one.</summary>
    static void CopyData(string from, string to)
    {
        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(f, dest);
        }
    }

    /// <summary>The installed copy's data folder without recorder-inis.json, as before 1.3.</summary>
    void InstalledBefore13() => File.Delete(Path.Combine(_root, "data", "recorder-inis.json"));

    /// <summary>A portable copy's data folder copied from the installed copy's keeps the record of the unmarked recorder the
    /// installed copy placed, its files still the bytes that record names. Whether or not either data folder has a
    /// recorder-inis.json ("none": an installed copy from before 1.3, whose record holds the ini's hash, which the portable copy
    /// moves to its own at its start), the recorder stays the installed copy's: the folder stays byte for byte, the game says
    /// another copy placed it and its switch is off, until the user turns it on here, which takes it over.</summary>
    [Theory]
    [InlineData("none")]
    [InlineData("portable")]
    [InlineData("both")]
    public async Task A_copy_seeded_from_the_installed_copys_data_takes_over_its_recorder_only_on_the_users_switch(string inis)
    {
        var installed = Copy(null, [_game]);
        await installed.ScanAsync(default);
        Assert.True(await StampedSettled(installed, _game));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.LedgerStamp)));
        var ini = Path.Combine(_exeDir, "scskiller.ini");
        var iniHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(ini)));
        CopyData(Path.Combine(_root, "data"), Path.Combine(_root, "portable"));
        if (inis != "both") InstalledBefore13();
        if (inis == "none")
        {
            File.Delete(Path.Combine(_root, "portable", "recorder-inis.json"));
            var seeded = new AppStore(Path.Combine(_root, "portable"));
            var record = seeded.LoadGame(_game.Id);
            record.RecorderFiles["scskiller.ini"] = iniHash;
            seeded.SaveGame(_game.Id, record);
        }
        var before = Files(_game.InstallDir);

        var portable = Copy("portable", [_game]);
        await Cycle(portable, [_game]);
        portable.ReconcileRecorders(_game.Id);

        Assert.Equal(iniHash, ScsKiller.GetIni(portable.Store, _exeDir)?.Tracked);   // the record says this copy placed it
        Assert.Equal(before, Files(_game.InstallDir));
        var state = portable.Games.Single();
        Assert.StartsWith("placed by another SCSKiller copy, so it may still record", state.RecorderNote);
        Assert.False(state.RecorderEffective);
        Assert.True(state.RecorderForeign);

        portable.SetRecorderOverride(_game.Id, RecorderOverride.On);
        Assert.Equal(Path.GetFullPath(CopyLedger("portable")), File.ReadAllText(Path.Combine(_exeDir, ScsKiller.LedgerStamp)));
        Assert.True(await StampedSettled(portable, _game));
        Assert.True(portable.Games.Single().RecorderEffective);
        Assert.False(portable.Games.Single().RecorderForeign);
    }

    /// <summary>The seeded record names a recorder beside another exe of the game (its launcher's folder), placed there by the
    /// installed copy, and the game's own exe folder has none: the switch is off all the same, so one click takes it over.</summary>
    [Fact]
    public async Task A_seeded_copys_switch_is_off_while_the_recorded_folder_is_another_copys()
    {
        var installed = Copy(null, [_game]);
        await installed.ScanAsync(default);
        Assert.True(await StampedSettled(installed, _game));
        var launcherDir = _game.InstallDir;
        foreach (var f in Directory.EnumerateFiles(_exeDir).Where(f => !f.Equals(_game.ExePath, StringComparison.OrdinalIgnoreCase)))
            File.Move(f, Path.Combine(launcherDir, Path.GetFileName(f)));
        CopyData(Path.Combine(_root, "data"), Path.Combine(_root, "portable"));
        var seeded = new AppStore(Path.Combine(_root, "portable"));
        var record = seeded.LoadGame(_game.Id);
        record.RecorderExe = Path.Combine(launcherDir, "Fake.exe");
        record.Recorder = RecorderOverride.On;
        seeded.SaveGame(_game.Id, record);
        var before = Files(_game.InstallDir);

        var portable = Copy("portable", [_game]);
        await Cycle(portable, [_game]);
        portable.ReconcileRecorders(_game.Id);

        Assert.Equal(before, Files(_game.InstallDir));
        var state = portable.Games.Single();
        Assert.StartsWith("placed by another SCSKiller copy", state.RecorderNote);
        Assert.False(state.RecorderEffective);
        Assert.True(state.RecorderForeign);

        portable.SetRecorderOverride(_game.Id, RecorderOverride.On);
        Assert.True(await StampedSettled(portable, _game));
    }

    /// <summary>A 1.2.5 portable copy updated in place: its record names the recorder it placed, which has no marker (1.2.5
    /// wrote none), and the ini's hash only that record holds. The installed copy's data folder has the ledger 1.2.5 kept
    /// there and no records.</summary>
    async Task Portable125()
    {
        var old = Copy("portable", [_game]);
        await old.ScanAsync(default);
        Assert.True(await StampedSettled(old, _game));
        File.Delete(Path.Combine(_exeDir, ScsKiller.LedgerStamp));
        File.Delete(Path.Combine(_root, "portable", "recorder-inis.json"));
        var record = old.Store.LoadGame(_game.Id);
        record.RecorderFiles["scskiller.ini"] = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(_exeDir, "scskiller.ini"))));
        old.Store.SaveGame(_game.Id, record);
        Directory.CreateDirectory(Path.Combine(_root, "data", "armed"));
    }

    /// <summary>The 1.2.5 portable's record names the recorder, its files are the bytes it records, and no record of the
    /// installed copy names the folder: the recorder is this copy's again, marked and stamped.</summary>
    [Fact]
    public async Task A_portable_copy_updated_from_1_2_5_takes_back_the_recorder_it_placed()
    {
        await Portable125();

        var portable = Copy("portable", [_game]);
        await Cycle(portable, [_game]);

        Assert.Equal(Path.GetFullPath(CopyLedger("portable")), File.ReadAllText(Path.Combine(_exeDir, ScsKiller.LedgerStamp)));
        Assert.True(await StampedSettled(portable, _game));
        Assert.False(portable.Games.Single().RecorderForeign);
    }

    /// <summary>The same, but a record in the installed copy's data folder, under any game's id, names the folder (its
    /// recorder's, a move's old folder or an offline session's), or one can't be told (not JSON, a field of another type, a path
    /// that isn't one): the folder may be the installed copy's,
    /// and a data folder copied from it holds the same records. Nothing is taken over and the folder stays byte for byte.</summary>
    [Theory]
    [InlineData("recorder")]
    [InlineData("move")]
    [InlineData("session")]
    [InlineData("broken")]
    [InlineData("session-type")]
    [InlineData("bad-path")]
    public async Task A_portable_copy_updated_from_1_2_5_leaves_a_recorder_the_installed_copys_records_name_or_cant_tell(string installed)
    {
        await Portable125();
        var other = Path.Combine(_root, "data", "games", "other-game");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "state.json"), installed switch
        {
            "recorder" => System.Text.Json.JsonSerializer.Serialize(new { RecorderExe = _game.ExePath }),
            "move" => System.Text.Json.JsonSerializer.Serialize(new { RecorderMoveFrom = _game.ExePath }),
            "session" => System.Text.Json.JsonSerializer.Serialize(new { OfflineSession = new { Exe = _game.ExePath } }),
            "session-type" => "{ \"OfflineSession\": [] }",
            "bad-path" => System.Text.Json.JsonSerializer.Serialize(new { RecorderExe = Path.Combine(_root, "elsewhere", "game\0.exe") }),
            _ => "{ \"RecorderExe\": ",
        });
        var before = Files(_game.InstallDir);

        var portable = Copy("portable", [_game]);
        var claims = 0;
        portable.ClaimStep = () => claims++;
        await Cycle(portable, [_game]);
        portable.ReconcileRecorders(_game.Id);

        Assert.Equal(0, claims);
        Assert.Equal(before, Files(_game.InstallDir));
        Assert.True(portable.Games.Single().RecorderForeign);
    }

    /// <summary>Another copy takes the unmarked recorder over (its marker placed) after this copy found it unmarked and before
    /// it claims it: this copy leaves that copy's marker and the folder as they are.</summary>
    [Fact]
    public async Task A_claim_never_replaces_a_marker_another_copy_placed_meanwhile()
    {
        await Portable125();
        var marker = Path.Combine(_exeDir, ScsKiller.LedgerStamp);
        Dictionary<string, string>? before = null;

        var portable = Copy("portable", [_game]);
        portable.ClaimStep = () =>
        {
            File.WriteAllText(marker, Path.GetFullPath(CopyLedger("other")));
            before = Files(_game.InstallDir);
        };
        await Cycle(portable, [_game]);

        Assert.NotNull(before);
        Assert.Equal(before, Files(_game.InstallDir));
        Assert.Equal(Path.GetFullPath(CopyLedger("other")), File.ReadAllText(marker));
    }

    /// <summary>The installed copy takes the unmarked recorder (its record saved, or its ini signed again) after this copy
    /// checked it and before its marker went in: this copy's marker goes again and the folder is the installed copy's.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_claim_gives_way_when_the_installed_copy_takes_the_recorder_meanwhile(bool resigned)
    {
        await Portable125();
        Dictionary<string, string>? before = null;

        var portable = Copy("portable", [_game]);
        portable.ClaimStep = () =>
        {
            if (resigned) File.AppendAllText(Path.Combine(_exeDir, "scskiller.ini"), "; signed again\r\n");
            else
            {
                Directory.CreateDirectory(Path.Combine(_root, "data", "games", "installed"));
                File.WriteAllText(Path.Combine(_root, "data", "games", "installed", "state.json"), System.Text.Json.JsonSerializer.Serialize(new { RecorderExe = _game.ExePath }));
            }
            before = Files(_game.InstallDir);
        };
        await Cycle(portable, [_game]);

        Assert.NotNull(before);
        Assert.Equal(before, Files(_game.InstallDir));
        Assert.True(portable.Games.Single().RecorderForeign);
    }

    /// <summary>Copy A, portable, marks and stamps its recorder; copy B starts on a plain copy of A's data folder, whose record
    /// names that recorder: the marker names A, so B takes nothing and the game folder stays byte for byte.</summary>
    [Fact]
    public async Task A_copy_started_on_a_copy_of_another_copys_data_leaves_its_marked_recorder_as_it_is()
    {
        var a = Copy("a", [_game]);
        await a.ScanAsync(default);
        Assert.True(await StampedSettled(a, _game));
        Played(_game);
        CopyData(Path.Combine(_root, "a"), Path.Combine(_root, "b"));
        var before = Files(_game.InstallDir);

        var b = Copy("b", [_game]);
        await Cycle(b, [_game]);
        b.ReconcileRecorders(_game.Id);

        Assert.Equal(before, Files(_game.InstallDir));
        Assert.Equal(0, RecordedCount(b, _game));
        Assert.False(b.Games.Single().RecorderInstalled);
    }

    /// <summary>A copy of an older build took this copy's recorder over: its marker names it and its scskiller.armed files arm
    /// it. This copy's pass, which still has the recorder on its record and finds its stamp stale, leaves that folder byte
    /// for byte.</summary>
    [Fact]
    public async Task A_pass_leaves_the_armed_files_of_an_older_copy_that_took_its_recorder_over()
    {
        var a = Copy("a", [_game]);
        await a.ScanAsync(default);
        Assert.True(await StampedSettled(a, _game));
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.LedgerStamp), Path.GetFullPath(CopyLedger("older")));
        File.WriteAllText(Path.Combine(_exeDir, "scskiller.ini"), "[scskiller]\r\n");
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.ArmedFile), "[scskiller]\r\narmed=1\r\nnonce=00\r\n");
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.ArmedFile + ".0011.revoked"), "");
        var before = Files(_game.InstallDir);

        await a.CheckRecorderGames();

        Assert.Equal(before, Files(_game.InstallDir));
    }

    /// <summary>A takeover puts this build's recorder in place of the other copy's, also over a newer build's, and never while
    /// the game runs.</summary>
    [Fact]
    public async Task A_takeover_replaces_the_other_copys_recorder_with_this_builds()
    {
        var installed = Copy(null, [_game]);
        await installed.ScanAsync(default);
        Assert.True(await StampedSettled(installed, _game));
        var dll = Path.Combine(_exeDir, "d3d12.dll");
        var mine = File.ReadAllBytes(dll);
        // a newer build's: a PE with a version resource and the proxy's export name
        byte[] theirs = [.. File.ReadAllBytes(Path.Combine(Environment.SystemDirectory, "version.dll")), .. "SCSKiller_StartWarm"u8];
        File.WriteAllBytes(dll, theirs);
        Assert.True(ScsKiller.IsOurProxy(dll));

        var b = Copy("b", [_game]);
        await b.ScanAsync(default);
        b.ProcessNames = () => new HashSet<string> { Path.GetFileNameWithoutExtension(_game.ExePath) };
        Assert.Contains("is running", Assert.Throws<InvalidOperationException>(() => b.SetRecorderOverride(_game.Id, RecorderOverride.On)).Message);
        Assert.Equal(theirs, File.ReadAllBytes(dll));
        b.ProcessNames = () => new HashSet<string>();
        b.SetRecorderOverride(_game.Id, RecorderOverride.On);
        Assert.Equal(mine, File.ReadAllBytes(dll));
        Assert.True(await StampedSettled(b, _game));
        Assert.False(b.Store.LoadGame(_game.Id).RecorderClaimed);
    }

    /// <summary>The user turning the recorder on in another copy takes it over (not while the game runs): its marker names
    /// that copy, which stamps it and imports the inbox. The copy that placed it then leaves it alone, until the user turns
    /// it on there again, and its keys file is then this copy's.</summary>
    [Fact]
    public async Task Turning_the_recorder_on_in_another_copy_takes_it_over()
    {
        var installed = Copy(null, [_game]);
        await installed.ScanAsync(default);
        Assert.True(await StampedSettled(installed, _game));
        Played(_game);

        var b = Copy("b", [_game]);
        await b.ScanAsync(default);
        var before = Files(_game.InstallDir);
        b.ProcessNames = () => new HashSet<string> { Path.GetFileNameWithoutExtension(_game.ExePath) };
        Assert.Contains("is running", Assert.Throws<InvalidOperationException>(() => b.SetRecorderOverride(_game.Id, RecorderOverride.On)).Message);
        Assert.Equal(before, Files(_game.InstallDir));
        b.ProcessNames = () => new HashSet<string>();
        b.SetRecorderOverride(_game.Id, RecorderOverride.On);
        Assert.Equal(Path.GetFullPath(CopyLedger("b")), File.ReadAllText(Path.Combine(_exeDir, ScsKiller.LedgerStamp)));
        Assert.True(await StampedSettled(b, _game));
        await b.ScanAsync(default);
        await b.RecorderIndexing.WaitAsync(HangGuard);
        Assert.Equal(1, RecordedCount(b, _game));

        Played(_game);
        before = Files(_game.InstallDir);
        ScsKiller.LedgerDir = ScsKiller.DefaultLedger;
        await Cycle(installed, [_game]);
        Assert.Equal(before, Files(_game.InstallDir));
        Assert.Equal(0, RecordedCount(installed, _game));

        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        File.Delete(Path.Combine(_exeDir, "scskiller.db"));   // b imported it
        byte[] theirs = [.. "SCSKKEY1"u8, .. new byte[20]];
        File.WriteAllBytes(keys, theirs);   // naming what b recorded
        Recorded(installed, _game);
        installed.ManageRecorders = false;   // no background index rewrites the keys
        installed.SetRecorderOverride(_game.Id, RecorderOverride.On);
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.LedgerStamp)));
        Assert.NotEqual(theirs, File.ReadAllBytes(keys));   // written again from this copy's recording
        Assert.True(await StampedSettled(installed, _game));
    }

    /// <summary>An offline session journal a portable copy's first start copied from the installed copy's data: its cleanup
    /// leaves that session's folder as it is and drops the copied journal.</summary>
    [Fact]
    public void An_offline_session_journal_copied_from_another_copy_leaves_its_folder_alone()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Eac")).FullName;
        var exe = Path.Combine(dir, "Eac.exe");
        File.WriteAllBytes(exe, [0]);
        string[] created = ["d3d12.dll", "scskiller.ini", ScsKiller.ArmedFile, "scskiller.db"];
        foreach (var f in created) File.WriteAllText(Path.Combine(dir, f), f);
        var before = Files(dir);
        ScsKiller.LedgerDir = CopyLedger("portable");
        var store = new AppStore(Path.Combine(_root, "portable"));
        var rec = store.LoadGame("test:eac");
        rec.OfflineSession = new OfflineSession("test:eac", exe, dir, Created: created);
        store.SaveGame("test:eac", rec);
        Assert.Null(ScsKiller.CleanOfflineSession(store, "test:eac", _ => false, _ => { }));
        Assert.Equal(before, Files(dir));
        Assert.Null(store.LoadGame("test:eac").OfflineSession);
    }

    /// <summary>Anti-cheat appearing in a game with a recorder another copy placed (here one deleted since), chained to a
    /// mod: this copy takes the proxy out all the same and puts the mod back under its own name. Its unimported recording,
    /// or a keys file that can't be deleted, stays that copy's, with its marker: never imported here; without either the
    /// marker goes too.</summary>
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Anti_cheat_takes_out_a_recorder_another_copy_placed(bool played, bool locked)
    {
        File.Copy(_proxy, Path.Combine(_exeDir, "d3d12.dll"));
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        File.WriteAllBytes(Path.Combine(_exeDir, ScsKiller.ChainName), mod);
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.LedgerStamp), CopyLedger("gone"));
        if (played) Played(_game);
        var keys = Path.Combine(_exeDir, Recordings.KeysFile);
        File.WriteAllText(keys, "keys");
        var inbox = Files(_exeDir).Where(f => f.Key.EndsWith("scskiller.db", StringComparison.OrdinalIgnoreCase)).ToList();
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "EasyAntiCheat"));

        var installed = Copy(null, [_game]);
        using (locked ? new FileStream(keys, FileMode.Open, FileAccess.Read, FileShare.None) : null)
        {
            await installed.ScanAsync(default);
            await installed.RecorderIndexing.WaitAsync(HangGuard);
        }
        Assert.Equal(mod, File.ReadAllBytes(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(Path.Combine(_exeDir, ScsKiller.ChainName)));
        Assert.Equal(locked, File.Exists(keys));
        Assert.Equal(played || locked, File.Exists(Path.Combine(_exeDir, ScsKiller.LedgerStamp)));
        Assert.Equal(inbox, Files(_exeDir).Where(f => f.Key.EndsWith("scskiller.db", StringComparison.OrdinalIgnoreCase)).ToList());
        Assert.Equal(0, RecordedCount(installed, _game));
    }

    /// <summary>Another copy's chained mod that can't be renamed back yet (held open, readable or not) when anti-cheat takes
    /// its recorder out is put back by a later pass.</summary>
    [Theory]
    [InlineData(FileShare.Read)]
    [InlineData(FileShare.None)]
    public async Task Anti_cheat_puts_another_copys_chained_mod_back_once_it_can_be_renamed(FileShare share)
    {
        File.Copy(_proxy, Path.Combine(_exeDir, "d3d12.dll"));
        var mod = Planning.MiddlewarePackTests.Pe("d3d12.dll", Guid.NewGuid().ToByteArray());
        var chained = Path.Combine(_exeDir, ScsKiller.ChainName);
        File.WriteAllBytes(chained, mod);
        File.WriteAllText(Path.Combine(_exeDir, ScsKiller.LedgerStamp), CopyLedger("gone"));
        Directory.CreateDirectory(Path.Combine(_game.InstallDir, "Fake", "Content", "EasyAntiCheat"));

        var installed = Copy(null, [_game]);
        installed.RemovalRetryInterval = TimeSpan.Zero;
        using (new FileStream(chained, FileMode.Open, FileAccess.Read, share))
        {
            await installed.ScanAsync(default);
            await installed.RecorderIndexing.WaitAsync(HangGuard);
        }
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
        await installed.CheckRecorderGames();
        await installed.RecorderIndexing.WaitAsync(HangGuard);
        Assert.Equal(mod, File.ReadAllBytes(Path.Combine(_exeDir, "d3d12.dll")));
        Assert.False(File.Exists(chained));
    }

    /// <summary>SCSKiller's uninstall with a move pending whose destination another copy took over since: that folder is
    /// left to its copy; the move's source, this copy's, is cleaned.</summary>
    [Fact]
    public void The_uninstall_leaves_a_pending_moves_folder_another_copy_took_over()
    {
        var moved = Directory.CreateDirectory(Path.Combine(_root, "Moved")).FullName;
        File.WriteAllBytes(Path.Combine(moved, "Fake.exe"), [0]);
        File.Copy(_proxy, Path.Combine(moved, "d3d12.dll"));
        File.WriteAllText(Path.Combine(moved, ScsKiller.LedgerStamp), CopyLedger("b"));
        File.Copy(_proxy, Path.Combine(_exeDir, "d3d12.dll"));
        var store = new AppStore(Path.Combine(_root, "data"));
        var rec = store.LoadGame(_game.Id);
        (rec.RecorderExe, rec.RecorderInstallDir, rec.RecorderMoveFrom, rec.RecorderMoveTo) = (_game.ExePath, _game.InstallDir, _game.ExePath, Path.Combine(moved, "Fake.exe"));
        store.SaveGame(_game.Id, rec);
        var before = Files(moved);
        ScsKiller.RemoveAllRecorders(store, new HashSet<string>());
        Assert.Equal(before, Files(moved));
        Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
    }

    /// <summary>A portable copy turning its recorder off while its scskiller.ini is held open: the proxy goes, the ini waits,
    /// and the marker stays with it, so the folder is still this copy's to finish once the ini is let go.</summary>
    [Fact]
    public async Task A_copys_marker_stays_while_its_ini_removal_is_pending()
    {
        var a = Copy("a", [_game]);
        await a.ScanAsync(default);
        Assert.True(await StampedSettled(a, _game));
        var (ini, marker) = (Path.Combine(_exeDir, "scskiller.ini"), Path.Combine(_exeDir, ScsKiller.LedgerStamp));
        using (new FileStream(ini, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            a.SetRecorderOverride(_game.Id, RecorderOverride.Off);
            Assert.False(File.Exists(Path.Combine(_exeDir, "d3d12.dll")));
            Assert.True(File.Exists(marker));
            Assert.False(ScsKiller.Foreign(_exeDir));
        }
        a.ReconcileRecorders();
        Assert.False(File.Exists(ini));
        Assert.False(File.Exists(marker));
    }

    /// <summary>A portable copy and a test copy (SCSKILLER_DATA_DIR) start, stamp a recorder and take it out: the installed
    /// copy's folder, with its 1.2 ledger, key, revocation list and refusal, stays byte for byte. Each copy signs with its
    /// own key, in the folder its marker names.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_copy_with_its_own_data_folder_leaves_the_installed_copys_folder_as_it_was(bool moved)
    {
        var live = Path.GetDirectoryName(ScsKiller.DefaultLedger)!;
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(ScsKiller.DefaultLedger).FullName, ScsKiller.FileKey(_game.ExePath)), "[scskiller]\r\nnonce=00\r\n");
        File.WriteAllText(Path.Combine(live, "recorder.key"), new string('a', 64));
        File.WriteAllText(Path.Combine(live, "recorder.revoked"), ScsKiller.FileKey(_exeDir) + "\r\n");
        File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(live, "refused")).FullName, ScsKiller.FileKey(_game.ExePath)), "1 refused");
        var before = Files(live);
        var data = Path.Combine(_root, moved ? "moved" : "portable");
        string?[] was = [Environment.GetEnvironmentVariable("SCSKILLER_DATA_DIR"), Environment.GetEnvironmentVariable("SCSKILLER_TEST_RECORDER")];
        try
        {
            if (moved)
            {
                Environment.SetEnvironmentVariable("SCSKILLER_DATA_DIR", data);
                Environment.SetEnvironmentVariable("SCSKILLER_TEST_RECORDER", "1");
            }
            ScsKiller.RecorderDir = null!;
            ScsKiller.LedgerDir = moved ? null! : Path.Combine(data, "armed");
            var k = Killer(new FakeReader(Unreal), games: [_game], data: data);
            k.ProcessNames = () => new HashSet<string>();
            k.ManageRecorders = true;
            await k.ScanAsync(default);
            Assert.True(await StampedSettled(k, _game));
            Assert.Equal(Path.Combine(data, "armed"), File.ReadAllText(Path.Combine(_exeDir, ScsKiller.LedgerStamp)));
            Assert.True(File.Exists(Path.Combine(data, "recorder.key")));
            ScsKiller.RemoveAllRecorders(k.Store, new HashSet<string>());
            Assert.False(File.Exists(Path.Combine(_exeDir, "scskiller.ini")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCSKILLER_DATA_DIR", was[0]);
            Environment.SetEnvironmentVariable("SCSKILLER_TEST_RECORDER", was[1]);
            ScsKiller.RecorderDir = TestEnv.Recorder;
        }
        Assert.Equal(before, Files(live));
    }

    /// <summary>The proxy's side: `selftest ledgerdir` reads the recorder folder from scskiller.ledger as a copy writes it,
    /// and a marker it can't use gives none, never the installed copy's.</summary>
    [Fact]
    public void The_proxy_reads_the_copys_recorder_folder_from_its_marker()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root != null && !File.Exists(Path.Combine(root.FullName, "SCSKiller.slnx"))) root = root.Parent;
        var exe = root == null ? null : Path.Combine(root.FullName, "proxy", "build", "Release", "selftest.exe");
        if (exe == null || !File.Exists(exe)) return;   // this checkout's proxy isn't built
        using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, "ledgerdir") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0 && output.Contains("ledgerdir OK"), output);
    }

    /// <summary>A copy's identity follows the data folder, wherever SCSKILLER_DATA_DIR moves it.</summary>
    [Fact]
    public void A_copys_identity_follows_the_data_folder()
    {
        var was = Environment.GetEnvironmentVariable("SCSKILLER_DATA_DIR");
        try
        {
            Environment.SetEnvironmentVariable("SCSKILLER_DATA_DIR", Path.Combine(_root, "moved"));
            Assert.Equal(Path.Combine(_root, "moved", "armed"), ScsKiller.DataLedger);
        }
        finally { Environment.SetEnvironmentVariable("SCSKILLER_DATA_DIR", was); }
    }
}
