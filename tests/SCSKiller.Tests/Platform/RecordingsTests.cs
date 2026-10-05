using System.Security.Cryptography;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Unreal;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Platform;

// Compact recordings (PsoDb.WriteCompact), the store's merge, the recorder's keys file and the inbox's rotation (Recordings).
public class RecordingsTests(ITestOutputHelper output) : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("scskiller-recordings-test-").FullName;

    bool _leak;   // a test's thread may still hold a file in it

    public void Dispose()
    {
        if (!_leak) Directory.Delete(_dir, true);
    }

    static PsoDb.Rec Blob(byte[] b) => new('B', [.. SHA1.HashData(b), .. b]);
    static string Sha(byte[] b) => PsoDb.Hex(SHA1.HashData(b));
    static PsoDb.Rec Cs(byte[] rs, byte[] cs) => new('C', PsoDb.Compute(Sha(rs), Sha(cs)));
    static byte[] Shader(string name) => System.Text.Encoding.ASCII.GetBytes("DXBC shader " + name);
    static List<string> Keys(IEnumerable<PsoDb.Rec> recs) => recs.Select(r => r.Key).ToList();

    string Raw(string name, params IEnumerable<PsoDb.Rec> recs)
    {
        var path = Path.Combine(_dir, name);
        using var f = File.Create(path);
        foreach (var r in recs) PsoDb.Write(f, r.Tag, r.Payload);
        return path;
    }

    /// <summary>What the recorder writes of a session's creates (proxy.cpp store): the records the keys file doesn't name and,
    /// before each, the blobs it doesn't name.</summary>
    static IEnumerable<PsoDb.Rec> Recorded(IEnumerable<PsoDb.Rec> session, string keysFile)
    {
        var keys = File.Exists(keysFile) ? File.ReadAllBytes(keysFile)[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet() : [];
        return session.Where(r => keys.Add(r.Tag == 'B' ? PsoDb.Hex(r.Payload.AsSpan(0, 20)) : r.Key));
    }

    [Fact]
    public void A_compact_recording_reads_back_as_the_proxy_db_it_holds_and_a_proxy_db_still_reads()
    {
        var rs = CommunityTests.RootSignature();
        var recs = new[] { Blob(rs), Blob(Shader("a")), Cs(rs, Shader("a")), new PsoDb.NvState(Cs(rs, Shader("a")).Key, 7, 0, 1, 0).ToRec() };
        var raw = Raw("raw.db", recs);
        var compact = Path.Combine(_dir, "compact.db");
        PsoDb.WriteCompact(compact, PsoDb.Read(raw));

        Assert.True(PsoDb.IsCompact(compact));
        Assert.False(PsoDb.IsCompact(raw));
        Assert.Equal(Keys(recs), Keys(PsoDb.Read(compact)));
        Assert.Equal(Keys(recs), Keys(PsoDb.Read(raw)));
        PsoDb.CopyRaw(compact, Path.Combine(_dir, "back.db"));
        Assert.Equal(File.ReadAllBytes(raw), File.ReadAllBytes(Path.Combine(_dir, "back.db")));
        Assert.False(File.Exists(compact + ".tmp"));

        var bytes = File.ReadAllBytes(compact);
        File.WriteAllBytes(compact, bytes[..^4]);   // cut short: never read as a shorter recording
        Assert.Throws<InvalidDataException>(() => PsoDb.Read(compact).ToList());
        bytes[8] = 99;   // a version this build doesn't know
        File.WriteAllBytes(compact, bytes);
        Assert.Throws<InvalidDataException>(() => PsoDb.Read(compact).ToList());
    }

    [Fact]
    public void An_interrupted_write_leaves_the_old_recording_and_the_next_one_succeeds()
    {
        var rs = CommunityTests.RootSignature();
        var path = Raw("recording.db", Blob(rs), Blob(Shader("a")), Cs(rs, Shader("a")));
        var before = File.ReadAllBytes(path);
        IEnumerable<PsoDb.Rec> Failing()
        {
            foreach (var r in PsoDb.Read(path)) yield return r;
            throw new IOException("the disk is full");
        }
        Assert.Throws<IOException>(() => PsoDb.WriteCompact(path, Failing()));
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));

        File.WriteAllText(path + ".tmp", "left by an older build's process that was killed mid-write");
        File.SetLastWriteTime(path + ".tmp", DateTime.Now.AddHours(-2));   // not written for an hour: its writer is gone
        var recent = $"{path}.{Guid.NewGuid():N}.tmp";   // under the lock no writer is live, but it may be a minute old: kept for an hour
        File.WriteAllText(recent, "?");
        using (Recordings.Lock(path)) { }   // whoever writes next removes the old ones
        Assert.Equal([recent], Directory.GetFiles(_dir, "*.tmp"));
        File.SetLastWriteTime(recent, DateTime.Now.AddHours(-2));
        using (Recordings.Lock(path)) { }
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
        PsoDb.WriteCompact(path, PsoDb.Read(path).ToList());
        Assert.Equal(Keys(HashOnly.Records(before)), Keys(PsoDb.Read(path)));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public async Task The_lock_is_an_exclusive_file_reentered_by_its_holder_and_waited_for_up_to_a_limit()
    {
        var path = Path.Combine(_dir, "recording.db");
        var deadline = TimeSpan.FromSeconds(60);   // each step's: a wait that ignores its limit would otherwise take the default 10 minutes
        var held = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();
        var cleanup = new CancellationTokenSource();   // every acquisition's: the cleanup ends whichever still waits
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cleanup.Token);   // a community download's deadline, a stopped queue
        var workers = new List<Task>();
        Task<T> Worker<T>(Func<T> body)
        {
            var t = Task.Factory.StartNew(body, TaskCreationOptions.LongRunning);
            workers.Add(t);
            return t;
        }
        // Timed on the waiting thread itself: under a loaded test run the thread pool starves, which delays an await's
        // continuation and a CancellationTokenSource's timer by seconds, not the wait.
        Task<(Exception? Error, TimeSpan Waited)> Waiter(Action wait) => Worker(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            return (Record.Exception(wait), clock.Elapsed);
        });
        try
        {
            var first = Worker(() =>
            {
                using (Recordings.Lock(path, ct: cleanup.Token))
                using (Recordings.Lock(path, ct: cleanup.Token))   // re-entered (ImportRecording -> WriteKeys): no wait on itself
                {
                    held.Set();
                    release.Wait();
                }
                return true;
            });
            Assert.True(held.Wait(TimeSpan.FromSeconds(10)));
            var second = Worker(() => { using (Recordings.Lock(path, ct: cleanup.Token)) { } return true; });
            Assert.False(second.Wait(300));   // a file, so a process in another session waits the same way
            Assert.Throws<IOException>(() => new FileStream(path + ".lock", FileMode.Open, FileAccess.Read, FileShare.ReadWrite));

            var (error, waited) = await Waiter(() => Recordings.Lock(path, TimeSpan.FromMilliseconds(200), cleanup.Token).Dispose()).WaitAsync(deadline);
            Assert.IsType<IOException>(error);
            Assert.True(waited.TotalSeconds >= 0.15, $"gave up after {waited.TotalSeconds:0.000} s, before its limit");

            var cancelled = Waiter(() => Recordings.Lock(path, ct: cts.Token).Dispose());
            Assert.False(cancelled.Wait(300));
            cts.Cancel();
            Assert.IsAssignableFrom<OperationCanceledException>((await cancelled.WaitAsync(deadline)).Error);

            release.Set();
            await first.WaitAsync(deadline);
            await second.WaitAsync(deadline);
        }
        finally
        {
            // a failed step leaves no thread holding the lock file or using the events once they're disposed
            release.Set();
            cleanup.Cancel();
            bool joined;
            try { joined = Task.WaitAll([.. workers], deadline); }
            catch (AggregateException) { joined = true; }   // they ended; the test's own failure is the one reported
            if (!joined)
            {
                _leak = true;   // a thread still owns the lock file: the folder and the events are left, not raced
                Assert.Fail($"a lock thread still runs {deadline.TotalSeconds:0} s after the cleanup cancelled it; {_dir} is left");
            }
            foreach (var d in new IDisposable[] { held, release, cts, cleanup }) d.Dispose();
        }
    }

    [Fact]
    public void Two_writers_never_share_a_temp_file()
    {
        var rs = CommunityTests.RootSignature();
        var path = Raw("recording.db", Blob(rs), Blob(Shader("a")), Cs(rs, Shader("a")));
        var records = PsoDb.Read(path).ToList();
        using (new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))   // another writer's, mid-write
            PsoDb.WriteCompact(path, records);
        Assert.Equal(Keys(records), Keys(PsoDb.Read(path)));
    }

    [Fact]
    public async Task Recording_writes_take_turns_whatever_the_paths_spelling()
    {
        var path = Path.Combine(_dir, "recording.db");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var first = Task.Factory.StartNew(() =>
        {
            using (Recordings.Lock(path)) { held.Set(); release.Wait(); }
        }, TaskCreationOptions.LongRunning);
        held.Wait();
        var second = Task.Factory.StartNew(() => { using (Recordings.Lock(Path.Combine(_dir, ".", "RECORDING.DB"))) { } }, TaskCreationOptions.LongRunning);
        Assert.False(second.Wait(300));   // a named mutex: the app and the CLI wait for each other the same way
        release.Set();
        await first;
        await second.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_recording_being_read_is_not_replaced_and_the_next_write_is()
    {
        var rs = CommunityTests.RootSignature();
        var path = Path.Combine(_dir, "recording.db");
        PsoDb.WriteCompact(path, [Blob(rs), Cs(rs, Shader("a"))]);
        using (var reading = PsoDb.Read(path).GetEnumerator())
        {
            Assert.True(reading.MoveNext());
            Assert.ThrowsAny<UnauthorizedAccessException>(() => PsoDb.WriteCompact(path, [Blob(rs), Cs(rs, Shader("b"))]));
            Assert.True(reading.MoveNext());
            Assert.Equal(Cs(rs, Shader("a")).Key, reading.Current.Key);
        }
        Assert.False(File.Exists(path + ".tmp"));
        PsoDb.WriteCompact(path, [Blob(rs), Cs(rs, Shader("b"))]);
        Assert.Equal(Cs(rs, Shader("b")).Key, PsoDb.Read(path).Last().Key);
    }

    /// <summary>The store keeps its records and adds the inbox's new ones after them, once each; a shader blob a record names
    /// and the index has is left out, every other blob stays: root signatures, shaders in no file of the game, and blobs no
    /// record names (the planner reads those too).</summary>
    [Fact]
    public void The_merge_adds_what_is_new_once_and_leaves_out_the_shader_bytes_the_install_has()
    {
        var rs = CommunityTests.RootSignature();
        byte[] a = Shader("a"), b = Shader("b"), runtime = Shader("built at run time"), orphan = Shader("named by nothing");
        var nv = new PsoDb.NvState(Cs(rs, b).Key, 7, 0, 1, 0).ToRec();
        var store = Path.Combine(_dir, "recording.db");
        Assert.Equal([Cs(rs, a).Key], Recordings.Merge(store, Raw("s1.db", Blob(rs), Blob(a), Cs(rs, a)), null));
        var inbox = Raw("s2.db", Blob(rs), Blob(a), Cs(rs, a), Blob(b), Cs(rs, b), nv, Blob(runtime), Cs(rs, runtime), Blob(orphan));
        var shipped = new HashSet<string> { Sha(a), Sha(b), Sha(orphan), Sha(rs) };

        var added = Recordings.Merge(store, inbox, shipped.Contains);
        Assert.Equal(new[] { Cs(rs, b).Key, Cs(rs, runtime).Key }.Order(), added.Order());
        Assert.Equal(Keys([Blob(rs), Cs(rs, a), Cs(rs, b), nv, Blob(runtime), Cs(rs, runtime), Blob(orphan)]), Keys(PsoDb.Read(store)));
        Assert.Empty(Recordings.Merge(store, inbox, shipped.Contains));   // imported again: nothing new
        Assert.Equal(Keys([Blob(rs), Cs(rs, a), Cs(rs, b), nv, Blob(runtime), Cs(rs, runtime), Blob(orphan)]), Keys(PsoDb.Read(store)));
    }

    /// <summary>The keys file names every blob the store holds and every record that replays from the store and the index; a
    /// record whose shader bytes were left out for an index that no longer has them isn't named, so the recorder records it
    /// again with its bytes.</summary>
    [Fact]
    public void The_keys_file_names_what_the_recorder_needn_t_record_again()
    {
        var rs = CommunityTests.RootSignature();
        byte[] a = Shader("a"), gone = Shader("in the last build only"), runtime = Shader("built at run time");
        var store = Path.Combine(_dir, "recording.db");
        var nv = new PsoDb.NvState(Cs(rs, gone).Key, 7, 0, 1, 0).ToRec();
        Recordings.Merge(store, Raw("s.db", Blob(rs), Blob(a), Cs(rs, a), Blob(gone), Cs(rs, gone), nv, Blob(runtime), Cs(rs, runtime)),
            new HashSet<string> { Sha(a), Sha(gone) }.Contains);
        var keys = Path.Combine(_dir, Recordings.KeysFile);

        Assert.Equal(1, Recordings.WriteKeys(store, new HashSet<string> { Sha(a) }, keys));
        var file = File.ReadAllBytes(keys);
        Assert.Equal("SCSKKEY1"u8.ToArray(), file[..8]);
        var named = file[8..].Chunk(20).Select(Convert.ToHexStringLower).ToHashSet();
        Assert.Equal(new[] { Sha(a), Sha(rs), Sha(runtime), Cs(rs, a).Key, nv.Key, Cs(rs, runtime).Key }.Order(), named.Order());
    }

    /// <summary>With the index's shaders in the keys file, a first session names a shipped shader by hash only and keeps the
    /// bytes of one in no file of the game; the import and the install give every record its shaders back. Without an index
    /// there's no keys file and every shader is recorded whole; a shader a later index no longer has is recorded again.</summary>
    [Fact]
    public void A_shipped_shader_is_recorded_by_hash_only_and_read_back_from_the_install()
    {
        var rs = CommunityTests.RootSignature();
        byte[] a = Shader("a"), mod = Shader("a mod's");
        PsoDb.Rec[] session = [Blob(rs), Blob(a), Cs(rs, a), Blob(mod), Cs(rs, mod)];
        var shipped = new HashSet<string> { Sha(a) };
        var (store, keys) = (Path.Combine(_dir, "recording.db"), Path.Combine(_dir, Recordings.KeysFile));

        File.WriteAllText(keys, "left by a recording that is gone");
        Assert.Equal(0, Recordings.WriteKeys(store, null, keys));   // no index, no recording
        Assert.False(File.Exists(keys));
        Assert.Equal(Keys(session), Keys(Recorded(session, keys)));

        Recordings.WriteKeys(store, shipped, keys);
        Assert.Equal(8 + 20, new FileInfo(keys).Length);
        var inbox = Raw("scskiller.db", Recorded(session, keys));
        Assert.Equal(Keys([Blob(rs), Cs(rs, a), Blob(mod), Cs(rs, mod)]), Keys(PsoDb.Read(inbox)));
        Assert.Equal(2, Recordings.Merge(store, inbox, shipped.Contains).Count);
        Assert.Equal(0, Recordings.WriteKeys(store, shipped, keys));
        Assert.Empty(Recorded(session, keys));

        File.WriteAllBytes(Path.Combine(_dir, Sha(a) + ".bin"), a);   // the install
        var full = Path.Combine(_dir, "full.db");
        var back = Rehydrate.Run(store, full, new Game("test:keys", "Keys", Store.Other, _dir, _dir), new("Unreal", "4.26", null, "D3D12", false, null), new BytecodeDir(_dir));
        Assert.True(back.Complete);
        Assert.Equal(Keys(session).Order(), Keys(PsoDb.Read(full)).Order());

        Assert.Equal(1, Recordings.WriteKeys(store, new HashSet<string>(), keys));   // the next build doesn't ship a
        Assert.Equal(Keys([Blob(a), Cs(rs, a)]), Keys(Recorded(session, keys)));
    }

    /// <summary>A first session of a big game, simulated from SCSKiller's recording of it on this machine (every pipeline
    /// new, no recording yet): with the index's shaders in the keys file the inbox is a small part of what it is without, and
    /// importing it gives the compile the same shaders. Install and app data read only; returns early without the game or
    /// its recording.</summary>
    [Trait("Needs", "Game")]
    [Theory]
    [InlineData("xbox:BethesdaSoftworks.ProjectAltar_3275kfvn8vcwc")]   // Oblivion Remastered
    [InlineData("steam:990080")]                                        // Hogwarts Legacy
    [InlineData("ea:198300")]                                           // STAR WARS Jedi: Survivor
    public void A_first_session_of_a_big_game_stays_small(string id)
    {
        var game = new IGameSource[] { new Core.Games.SteamSource(), new Core.Games.EaSource(), new Core.Games.XboxSource() }
            .SelectMany(s => s.Discover()).FirstOrDefault(g => g.Id == id);
        if (game == null) return;
        Ff7.Codecs();
        var name = "keys-" + id.Split(':')[0];
        var data = Ff7.TempDir(name + "-data");
        string? made = null;
        try
        {
            var reader = new UnrealReader(data);
            var engine = reader.Detect(game)!;
            if (Ff7.Recording(game, engine, reader, name) is not { } session) return;
            var dir = made = Path.GetDirectoryName(session)!;
            var shipped = reader.Index(game, engine, null, CancellationToken.None).Shaders.Keys.ToHashSet();
            var (store, keys, inbox) = (Path.Combine(dir, "store.db"), Path.Combine(dir, Recordings.KeysFile), Path.Combine(dir, "scskiller.db"));

            Recordings.WriteKeys(store, shipped, keys);
            using (var f = new BufferedStream(File.Create(inbox), 1 << 20))
                foreach (var r in Recorded(PsoDb.Read(session), keys)) PsoDb.Write(f, r.Tag, r.Payload);
            long whole = new FileInfo(session).Length, byHash = new FileInfo(inbox).Length;
            output.WriteLine($"{game.Name}: a first session's scskiller.db {whole:N0} bytes with every shader's bytes, {byHash:N0} with the {shipped.Count:N0} shipped shaders by hash ({new FileInfo(keys).Length:N0} byte keys file)");
            foreach (var g in PsoDb.Read(inbox).GroupBy(r => r.Tag != 'B' ? "records" : Core.Carved.Dxbc.IsRootSignatureOnly(r.Payload.AsSpan(20)) ? "root signatures" : "shaders in no file of the game"))
                output.WriteLine($"  {g.Key}: {g.Count():N0}, {g.Sum(r => 5L + r.Payload.Length):N0} bytes");
            Assert.True(byHash * 5 < whole);

            var had = PsoDb.Read(session).Where(r => r.Tag == 'B').Select(r => PsoDb.Hex(r.Payload.AsSpan(0, 20))).ToHashSet();
            var missing = Rehydrate.References(PsoDb.Read(session)).Where(h => !had.Contains(h)).Order(StringComparer.Ordinal).ToList();
            Recordings.Merge(store, inbox, shipped.Contains);
            var back = Rehydrate.Run(store, Path.Combine(dir, "back.db"), game, engine, reader, moreBlobs: h => Core.Planning.Middleware.Blobs(game, h));
            Assert.Equal(missing, back.Missing);
            Assert.Equal(PsoDb.Read(session).Count(r => r.Tag != 'B'), PsoDb.Read(Path.Combine(dir, "back.db")).Count(r => r.Tag != 'B'));
        }
        finally
        {
            foreach (var d in new[] { data, made }.OfType<string>().Where(Directory.Exists)) Directory.Delete(d, true);
        }
    }

    /// <summary>A layer's 'W' (proxy.cpp): the key of the record the driver got, then the game's (none: the layer's own create).</summary>
    internal static PsoDb.Rec W(PsoDb.Rec driver, PsoDb.Rec? game) => new('W', [.. Convert.FromHexString(driver.Key), .. game is { } g ? Convert.FromHexString(g.Key) : new byte[20]]);

    /// <summary>A session under a layer: the game's pipeline, the one the driver got with the layer's root signature, and the
    /// layer's own pipeline of its own shader, each paired by a 'W'.</summary>
    (PsoDb.Rec Game, PsoDb.Rec Driver, PsoDb.Rec Own, PsoDb.Rec[] Session) Layered()
    {
        byte[] rs = CommunityTests.RootSignature(), layerRs = MiddlewarePackTests.Container("RTS0", "layer root signature"), a = Shader("a"), mod = Shader("the layer's");
        var (game, driver, own) = (Cs(rs, a), Cs(layerRs, a), Cs(layerRs, mod));
        return (game, driver, own, [Blob(rs), Blob(a), game, Blob(layerRs), driver, W(driver, game), Blob(mod), own, W(own, null)]);
    }

    /// <summary>What a layer made on this PC, from disk: every game folder's recording.db and the recorder's inbox next to
    /// the exe its state.json names, read while the recorder holds it open for writing; a state.json that isn't JSON throws.</summary>
    [Fact]
    public void What_a_layer_made_is_read_from_every_recording_and_inbox_on_disk()
    {
        var data = Path.Combine(_dir, "data");
        var store = new AppStore(data);
        var (game, driver, own, session) = Layered();
        var exeDir = Path.Combine(_dir, "game");
        Directory.CreateDirectory(exeDir);
        Directory.CreateDirectory(store.GameDir("test:a"));
        File.Move(Raw("recording.db", session.Where(r => !(r.Tag == 'W' && r.Payload.AsSpan(20).IndexOfAnyExcept((byte)0) < 0))), Path.Combine(store.GameDir("test:a"), "recording.db"));
        var rec = store.LoadGame("test:b");
        rec.RecorderExe = Path.Combine(exeDir, "game.exe");
        store.SaveGame("test:b", rec);
        var inbox = Raw("scskiller.db", W(own, null));
        File.Move(inbox, Path.Combine(exeDir, "scskiller.db"));
        using (new FileStream(Path.Combine(exeDir, "scskiller.db"), FileMode.Open, FileAccess.Write, FileShare.ReadWrite))   // the recorder's handle
            Assert.Equal(new[] { driver.Key, own.Key }.Order(), Recordings.LayerMadeOnDisk(data).Order());
        File.WriteAllText(Path.Combine(store.GameDir("test:b"), "state.json"), "{ damaged");
        Assert.ThrowsAny<System.Text.Json.JsonException>(() => Recordings.LayerMadeOnDisk(data));
        File.WriteAllText(Path.Combine(store.GameDir("test:b"), "state.json"), """{"RecorderExe":123}""");   // JSON, but not a path
        Assert.Throws<InvalidDataException>(() => Recordings.LayerMadeOnDisk(data));
        File.WriteAllText(Path.Combine(store.GameDir("test:b"), "state.json"), """{"RecorderExe":null,"RecorderMoveFrom":null}""");
        Assert.Equal([driver.Key], Recordings.LayerMadeOnDisk(data));   // null: no recorder
    }

    /// <summary>Nothing on the way to what a layer made turns an error into "none": a compact recording whose magic is
    /// damaged (its leading 0 kept), a games folder this user may not list, and a damaged line in packs\layer-made.keys
    /// all throw; only a missing games folder or list is empty.</summary>
    [Fact]
    public void A_damaged_or_unreadable_exclusion_source_throws()
    {
        var data = Path.Combine(_dir, "data");
        var store = new AppStore(data);
        Assert.Empty(Recordings.LayerMadeOnDisk(data));   // no games folder yet
        Directory.CreateDirectory(store.GameDir("test:a"));
        var (_, driver, _, session) = Layered();
        var db = Path.Combine(store.GameDir("test:a"), "recording.db");
        PsoDb.WriteCompact(db, session);
        Assert.Contains(driver.Key, Recordings.LayerMadeOnDisk(data));
        var bytes = File.ReadAllBytes(db);
        bytes[7] ^= 0x20;   // "\0SCSKREc"
        File.WriteAllBytes(db, bytes);
        // same size, and often the same write time as the first write (the clock ticks every ~15 ms), which the cache
        // would take for unchanged: a real rewrite comes later
        File.SetLastWriteTimeUtc(db, File.GetLastWriteTimeUtc(db).AddSeconds(1));
        Assert.Throws<Recordings.IncompleteLayerList>(() => Recordings.LayerMadeOnDisk(data));
        File.Delete(db);

        var games = new DirectoryInfo(Path.Combine(data, "games"));
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(sid, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        var acl = games.GetAccessControl();
        acl.AddAccessRule(deny);
        games.SetAccessControl(acl);
        try { Assert.ThrowsAny<UnauthorizedAccessException>(() => Recordings.LayerMadeOnDisk(data)); }
        finally
        {
            acl.RemoveAccessRule(deny);
            games.SetAccessControl(acl);
        }

        var packs = new MiddlewarePacks(Path.Combine(_dir, "packs"));
        packs.Exclude([driver.Key]);
        File.AppendAllText(Path.Combine(packs.Dir, "layer-made.keys"), "not a key\r\n");
        Assert.Throws<InvalidDataException>(() => packs.LayerMade());
    }

    /// <summary>'W' records are merged by key like 'N', aren't counted as added pipelines, are counted by
    /// <see cref="Recordings.Layered"/>, and are named in the keys file: a session that creates the same again records nothing.</summary>
    [Fact]
    public void A_layer_s_records_are_merged_once_counted_apart_and_named_in_the_keys_file()
    {
        var (game, driver, own, session) = Layered();
        var store = Path.Combine(_dir, "recording.db");
        Assert.Equal((0, 0), Recordings.Layered(store));
        Assert.Equal(new[] { game.Key, driver.Key, own.Key }.Order(), Recordings.Merge(store, Raw("s1.db", session), null).Order());
        Assert.Equal((1, 1), Recordings.Layered(store));
        Assert.Empty(Recordings.Merge(store, Raw("s2.db", session), null));
        Assert.Equal(Keys(session), Keys(PsoDb.Read(store)));

        var keys = Path.Combine(_dir, Recordings.KeysFile);
        Assert.Equal(0, Recordings.WriteKeys(store, null, keys));
        Assert.Empty(Recorded(session, keys));

        var other = new PsoDb.Rec('C', PsoDb.Compute(Sha(CommunityTests.RootSignature()), Sha(Shader("b"))));
        Recordings.Merge(store, Raw("s3.db", other, W(other, game)), null);
        Assert.Equal((2, 1), Recordings.Layered(store));   // the store changed: read again
    }

    /// <summary>The warm replays both the game's and the driver's records of a layered session; the 'W' pairing them is no
    /// item: in the work folder's db for the proxy (a warm through the layer creates what it made under it), not skipped,
    /// not a warm input.</summary>
    [Fact]
    public void A_layer_s_pairs_are_neither_warmed_nor_counted()
    {
        var (game, driver, own, session) = Layered();
        var recording = Raw("recording.db", session);
        var g = new Game("test:layer", "Fake", Store.Other, _dir, Path.Combine(_dir, "fake.exe"));
        var plan = new Plan(g.Id, "c", "PCD3D_SM6", "nvidia-1", new PlanStats(3, 0, 0, 0, false), Path.Combine(_dir, "plan.bin"));
        PlanFile.Write(plan, []);
        var work = Path.Combine(_dir, "work");
        new Planner().Materialize(plan, g, MiddlewarePackTests.Engine, new NoShaders(), new Recording(recording), work, default);

        var db = PsoDb.Read(Path.Combine(work, "scskiller.db")).ToList();
        Assert.Equal(Keys([game, driver, own]), Keys(db.Where(r => r.Tag is not ('B' or 'W'))));
        Assert.Equal(2, db.Count(r => r.Tag == 'W'));
        Assert.Equal(0, Planner.SkippedIn(work));
        Assert.Equal(new[] { game.Key, driver.Key, own.Key }.Order(), WarmInputs.Of([recording], null, [], _ => false).Order());
    }

    sealed class NoShaders : IEngineReader
    {
        public EngineInfo? Detect(Game game) => null;
        public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => throw new NotSupportedException();
        public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) { }
    }

    [Fact]
    public void The_inbox_is_emptied_only_at_its_imported_length_and_while_nothing_has_it_open()
    {
        var inbox = Raw("scskiller.db", Blob(Shader("a")));
        var length = new FileInfo(inbox).Length;
        using (new FileStream(inbox, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))   // the recorder's handle
            Assert.False(Recordings.Rotate(inbox, length));
        Assert.False(Recordings.Rotate(inbox, length - 1));   // it grew after the import read it
        Assert.Equal(length, new FileInfo(inbox).Length);
        Assert.True(Recordings.Rotate(inbox, length));
        Assert.Equal(0, new FileInfo(inbox).Length);
    }

    // state object records as the recorder writes them (proxy.cpp write_so): [base key], type, count, subobjects
    static byte[] U32(uint v) => BitConverter.GetBytes(v);
    static byte[] Str(string? s) => s == null ? U32(uint.MaxValue) : [.. U32((uint)s.Length), .. System.Text.Encoding.Unicode.GetBytes(s)];
    static byte[] So(string? baseKey, params byte[][] subs) =>
        [.. baseKey == null ? [] : Convert.FromHexString(baseKey), .. U32(3), .. U32((uint)subs.Length), .. subs.SelectMany(x => x)];
    static byte[] Library(string lib, params (string Name, string? Renames)[] exports) =>
        [.. U32(5), .. Convert.FromHexString(lib), .. U32((uint)exports.Length), .. exports.SelectMany(e => (byte[])[.. Str(e.Name), .. Str(e.Renames), .. U32(0)])];
    static byte[] HitGroup(string name, string? anyHit, string closestHit) => [.. U32(11), .. Str(name), .. U32(0), .. Str(anyHit), .. Str(closestHit), .. Str(null)];
    static byte[] Rs(uint type, string rs) => [.. U32(type), .. Convert.FromHexString(rs)];
    static byte[] Assoc(uint sub, params string[] exports) => [.. U32(7), .. U32(sub), .. U32((uint)exports.Length), .. exports.SelectMany(Str)];

    // The Witcher 3's shapes: its base pipeline (export names ending in a launch's suffix) and a material addition to it
    static PsoDb.Rec Pipeline(string suffix) => new('R', So(null, Library(new('1', 40), ("RGS_Shadows", null)), Library(new('2', 40), ("MISS_Occlusion", null)),
        Library(new('3', 40), ($"CHS_Occlusion_LRS_{suffix}", "OcclusionCHS")), HitGroup("HitGroup_Occlusion", null, $"CHS_Occlusion_LRS_{suffix}"),
        Rs(1, new('9', 40)), [.. U32(9), .. U32(32), .. U32(8)], [.. U32(12), .. U32(1), .. U32(0x200)], Rs(2, new('8', 40)), Assoc(7, "HitGroup_Occlusion")));
    static PsoDb.Rec Material(string baseKey, string closestHit, string anyHit, string name, string suffix = "2AE4465CABAC2C89") =>
        new('A', So(baseKey, Rs(1, new('9', 40)), [.. U32(9), .. U32(32), .. U32(8)], [.. U32(12), .. U32(1), .. U32(0x200)], [.. U32(0), .. U32(4)],
            Library(closestHit, ($"ClosestHit_{name}_LRS_{suffix}", $"ClosestHit_{name}")), Library(anyHit, ($"AnyHit_{name}_LRS_{suffix}", $"AnyHit_{name}")),
            HitGroup($"HitGroup_{name}", $"AnyHit_{name}_LRS_{suffix}", $"ClosestHit_{name}_LRS_{suffix}"), Rs(2, new('8', 40)), Assoc(7, $"HitGroup_{name}")));

    static HashSet<string> Inputs(string store, bool nvidia) => WarmInputs.Of(WarmInputs.Recorded.Read([store], _ => true, nvidia), null, [], _ => true);

    /// <summary>The Witcher 3 ends its base pipeline's export names with a suffix new every launch and adds its materials in
    /// another order each launch: in session 1791029034388, 29 additions were byte for byte an earlier session's on another
    /// base. The recording keeps every record as recorded; on NVIDIA, whose key leaves export names out and which caches an
    /// addition whatever it grows, the count of new pipelines takes those as nothing new and a new material as one. AMD
    /// caches a whole state object: there each new record counts.</summary>
    [Fact]
    public void Re_ordered_additions_of_known_materials_count_as_new_only_where_the_driver_needs_them()
    {
        string Lib(int i, char kind) => $"{i:x2}{new string(kind, 38)}";
        string Name(int i) => $"0x{i:X16}";
        var session1 = new List<PsoDb.Rec> { Pipeline("25B6436BA6B34F27") };
        foreach (var i in Enumerable.Range(0, 29)) session1.Add(Material(session1[^1].Key, Lib(i, 'c'), Lib(i, 'a'), Name(i)));
        var session2 = new List<PsoDb.Rec> { Pipeline("6B09914CA188FA81") };   // the next launch: the same 29 the other way round, then a new one
        foreach (var i in Enumerable.Range(0, 29).Reverse()) session2.Add(Material(session2[^1].Key, Lib(i, 'c'), Lib(i, 'a'), Name(i)));
        session2.Add(Material(session2[^1].Key, Lib(99, 'c'), Lib(99, 'a'), Name(99)));

        static PsoDb.Rec Nv(PsoDb.Rec r) => new PsoDb.NvState(r.Key, 12, 1, 1, 0).ToRec();   // as The Witcher 3 records them
        var store = Path.Combine(_dir, "recording.db");
        Recordings.Merge(store, Raw("s1.db", session1.SelectMany(r => new[] { r, Nv(r) })), null);
        var warmedNv = Inputs(store, true).Select(WarmInputs.Token).ToHashSet();   // the last warm's key files
        var warmedAmd = Inputs(store, false).Select(WarmInputs.Token).ToHashSet();
        Recordings.Merge(store, Raw("s2.db", session2.SelectMany(r => new[] { r, Nv(r) })), null);

        Assert.Equal(Keys([.. session1, .. session2]), Keys(PsoDb.Read(store).Where(r => r.Tag != 'N')));   // every record as recorded: a warm replays what the game created
        Assert.Equal([session2[^1].Key], Inputs(store, true).Where(i => !WarmInputs.Taken(warmedNv, i)).SelectMany(WarmInputs.Records));
        Assert.Equal(Keys(session2).Order(), Inputs(store, false).Where(i => !WarmInputs.Taken(warmedAmd, i)).Select(WarmInputs.Key).Order());
    }

    /// <summary>Only a launch's alias is taken as one: a name given to another function (ExportToRename) ending in the
    /// suffix. Two such aliases of different functions are two pipelines; a function's own name ending so, or a suffix in
    /// mid-name, is the name itself; a record whose names would collapse into one counts by its key.</summary>
    [Fact]
    public void Only_a_launch_alias_is_taken_off_an_export_name()
    {
        PsoDb.Rec Collection(params byte[][] subs) => new('R', [.. U32(0), .. U32((uint)subs.Length + 1), .. Rs(1, new('9', 40)), .. subs.SelectMany(x => x)]);
        int Count(params PsoDb.Rec[] recs)
        {
            var store = Path.Combine(_dir, $"recording-{Guid.NewGuid():N}.db");
            Recordings.Merge(store, Raw($"in-{Guid.NewGuid():N}.db", recs), null);
            return Inputs(store, true).Count;
        }
        string lib = new('3', 40), x = new('a', 40), y = new('b', 40);
        Assert.Equal(1, Count(Collection(Library(lib, ("Hit_LRS_25B6436BA6B34F27", "Shade"))), Collection(Library(lib, ("Hit_LRS_6B09914CA188FA81", "Shade")))));   // one function, two launches
        Assert.Equal(2, Count(Collection(Library(lib, ("Hit_LRS_25B6436BA6B34F27", "ShadeA"))), Collection(Library(lib, ("Hit_LRS_6B09914CA188FA81", "ShadeB")))));
        Assert.Equal(2, Count(Collection(Library(lib, ("Hit_LRS_25B6436BA6B34F27", null))), Collection(Library(lib, ("Hit_LRS_6B09914CA188FA81", null)))));   // two functions of the library
        Assert.Equal(2, Count(Collection(Library(lib, ("Hit_LRS_25B6436BA6B34F27_Opaque", "Shade"))), Collection(Library(lib, ("Hit_LRS_6B09914CA188FA81_Opaque", "Shade")))));
        // two aliases in one record that would both become "Hit": the record counts by its key, its associations as recorded
        byte[] Both() => Library(lib, ("Hit_LRS_1111111111111111", "ShadeA"), ("Hit_LRS_2222222222222222", "ShadeB"));
        Assert.Equal(2, Count(Collection(Both(), Rs(2, x), Assoc(2, "Hit_LRS_1111111111111111"), Rs(2, y), Assoc(4, "Hit_LRS_2222222222222222")),
            Collection(Both(), Rs(2, x), Assoc(2, "Hit_LRS_2222222222222222"), Rs(2, y), Assoc(4, "Hit_LRS_1111111111111111"))));
        // an alias beside a library that exports all its functions (Hit among them, unnamed in the record): counts by its key
        byte[] Implicit() => Library(new('4', 40));
        Assert.Equal(2, Count(Collection(Library(lib, ("Hit_LRS_1111111111111111", "ShadeA")), Implicit(), Rs(2, x), Assoc(3, "Hit_LRS_1111111111111111"), Rs(2, y), Assoc(5, "Hit")),
            Collection(Library(lib, ("Hit_LRS_1111111111111111", "ShadeA")), Implicit(), Rs(2, x), Assoc(3, "Hit"), Rs(2, y), Assoc(5, "Hit_LRS_1111111111111111"))));
        // one function under two aliases, created with other NVAPI state: another NVIDIA cache key
        PsoDb.Rec Aliased(string suffix) => Collection(Library(lib, ($"Hit_LRS_{suffix}", "Shade")));
        PsoDb.Rec Space(PsoDb.Rec r, uint space) => new PsoDb.NvState(r.Key, 12, space, 1, 0).ToRec();
        var (a1, a2) = (Aliased("25B6436BA6B34F27"), Aliased("6B09914CA188FA81"));
        Assert.Equal(2, Count(a1, Space(a1, 1001), a2, Space(a2, 404)));
        Assert.Equal(1, Count(a1, Space(a1, 1001), a2, Space(a2, 1001)));
    }

    static PsoDb.Rec HitSo(string suffix) => new('R', So(null, Library(new('3', 40), ($"Hit_LRS_{suffix}", "Shade")), Rs(1, new('9', 40))));
    static PsoDb.Rec NvSpace(PsoDb.Rec r, uint space) => new PsoDb.NvState(r.Key, 12, space, 1, 0).ToRec();

    static List<string> Pending(HashSet<string> warmed, HashSet<string> inputs) => [.. inputs.Where(i => !WarmInputs.Taken(warmed, i)).SelectMany(WarmInputs.Records)];
    static HashSet<string> Baseline(HashSet<string> inputs) => inputs.Select(WarmInputs.Token).ToHashSet();

    /// <summary>NVIDIA's key holds the NVAPI state a state object is replayed with, so a warm's key file must too: the
    /// object under a state recorded after the warm, with an alias of it under the same state, is one new pipeline there
    /// until the next warm takes it. AMD has no NVAPI state: there only the new record counts. A crash names a record:
    /// its identity's input stands for it.</summary>
    [Fact]
    public void A_state_object_under_another_NVAPI_state_is_new_on_NVIDIA_across_a_warm()
    {
        var (a1, a2) = (HitSo("25B6436BA6B34F27"), HitSo("6B09914CA188FA81"));
        var store = Path.Combine(_dir, "recording.db");
        Recordings.Merge(store, Raw("s1.db", a1, NvSpace(a1, 1001)), null);
        var (nv, amd) = (Baseline(Inputs(store, true)), Baseline(Inputs(store, false)));
        Recordings.Merge(store, Raw("s2.db", a1, NvSpace(a1, 404), a2, NvSpace(a2, 404)), null);   // the game's NVAPI space changed, and its launch suffix

        Assert.Equal([a1.Key, a2.Key], Pending(nv, Inputs(store, true)));
        Assert.Equal([a2.Key], Pending(amd, Inputs(store, false)));
        Assert.Empty(Pending(Baseline(Inputs(store, true)), Inputs(store, true)));
    }

    /// <summary>Two records of one identity and NVAPI state, warmed as one: one of them later recorded under another state is
    /// one new pipeline; the other's identity and state stay taken, whichever record now comes first.</summary>
    [Fact]
    public void An_identity_stays_taken_when_its_first_record_moves_to_another_NVAPI_state()
    {
        var (a, b) = (HitSo("25B6436BA6B34F27"), HitSo("6B09914CA188FA81"));
        var store = Path.Combine(_dir, "recording.db");
        Recordings.Merge(store, Raw("s1.db", a, NvSpace(a, 1001), b, NvSpace(b, 1001)), null);
        Assert.Single(Inputs(store, true));
        var nv = Baseline(Inputs(store, true));
        Recordings.Merge(store, Raw("s2.db", a, NvSpace(a, 404)), null);

        Assert.Equal(2, Inputs(store, true).Count);
        Assert.Equal([a.Key], Pending(nv, Inputs(store, true)));
    }

    /// <summary>A warm key file written before state object identities holds every record's key: after the update nothing
    /// it took counts, on NVIDIA either, and a new identity does.</summary>
    [Fact]
    public void A_key_file_from_before_identities_takes_the_identities_of_its_records()
    {
        var (a, b, c) = (HitSo("25B6436BA6B34F27"), HitSo("6B09914CA188FA81"), new PsoDb.Rec('R', So(null, Library(new('4', 40), ("Other", null)), Rs(1, new('9', 40)))));
        var store = Path.Combine(_dir, "recording.db");
        Recordings.Merge(store, Raw("s1.db", a, NvSpace(a, 1001), b, NvSpace(b, 1001)), null);
        var legacy = Baseline(Inputs(store, false));   // every record by its key, as the count kept them before
        Assert.Empty(Pending(legacy, Inputs(store, true)));
        Recordings.Merge(store, Raw("s2.db", c), null);
        Assert.Equal([c.Key], Pending(legacy, Inputs(store, true)));
    }

    /// <summary>A state object first created without NVAPI state and then with it replays with it (proxy.cpp g_nvext): on
    /// NVIDIA another compile than the warm took.</summary>
    [Fact]
    public void An_NVAPI_state_recorded_after_a_warm_makes_its_state_object_new_on_NVIDIA()
    {
        var a = HitSo("25B6436BA6B34F27");
        var store = Path.Combine(_dir, "recording.db");
        Recordings.Merge(store, Raw("s1.db", a), null);
        var (nv, amd) = (Baseline(Inputs(store, true)), Baseline(Inputs(store, false)));
        Recordings.Merge(store, Raw("s2.db", a, NvSpace(a, 404)), null);

        Assert.Equal([a.Key], Pending(nv, Inputs(store, true)));
        Assert.Empty(Pending(amd, Inputs(store, false)));
    }

    /// <summary>The NVAPI state counted is the one replayed: the last 'N' of the recordings' union (Community.Union), which
    /// drops a community 'N' this PC's recording already has.</summary>
    [Fact]
    public void The_NVAPI_state_counted_is_the_last_of_the_recordings_union()
    {
        var a = HitSo("25B6436BA6B34F27");
        var local = Raw("local.db", a, NvSpace(a, 1001), NvSpace(a, 404));
        var community = Raw("community.db", a, NvSpace(a, 1001));
        var merged = Path.Combine(_dir, "merged.db");
        Community.Union(local, community, merged);

        Assert.Equal(NvSpace(a, 404).Key, PsoDb.Read(merged).Last(r => r.Tag == 'N').Key);
        HashSet<string> Of(params string[] recordings) => WarmInputs.Of(WarmInputs.Recorded.Read(recordings, _ => true, true), null, [], _ => true);
        Assert.Equal(Of(Raw("as-404.db", a, NvSpace(a, 404))), Of(local, community));
        Assert.NotEqual(Of(Raw("as-1001.db", a, NvSpace(a, 1001))), Of(local, community));
    }
}
