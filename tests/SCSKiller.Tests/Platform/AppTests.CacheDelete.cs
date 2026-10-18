using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

// Deleting from the Cache page and following a cache folder the user moved with a junction, on fake cache folders under
// the test's own root. No real cache is touched.
public partial class AppTests
{
    [Fact]
    public async Task A_cache_folder_whose_link_target_is_missing_reads_empty_marks_nothing_trimmed_and_holds_compiles()
    {
        var k = await Warmed();
        var target = Path.Combine(_root, "elsewhere", "DXCache");
        Directory.CreateDirectory(target);
        File.WriteAllBytes(Path.Combine(target, "0002a91d33333333.nvph"), new byte[4096]);
        var link = Junction(Path.Combine(_root, "DXCache"), target);
        k.AppCache = new NvidiaAppCache(link);
        var rec = k.Store.LoadGame(_game.Id);
        (rec.CacheKeys, rec.WarmedKeys) = (["33333333"], ["33333333"]);
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        Assert.Equal((GameStatus.Warmed, 4096L), (k.Games.Single().Status, k.Games.Single().CacheOnDisk));

        Directory.Delete(target, true);   // the drive is gone
        Assert.True(Directory.Exists(link));   // Windows still reports the link
        Assert.Empty(k.AppCache.FilesOf(["33333333"]));
        Assert.Equal(link, AppCacheFiles.MissingTarget(k.AppCache));
        k.RefreshGame(_game.Id);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);   // out of reach, not trimmed
        Assert.True(Assert.Single(k.CacheInventory().Folders).TargetMissing);

        k.Enqueue(_game.Id);
        k.StartQueue();
        await k.WhenQueueIdle().WaitAsync(HangGuard);
        Assert.Equal((QueueStage.Failed, "not ready: " + ScsKiller.LinkTargetMissing(link)), (k.Queue.Single().Stage, k.Queue.Single().Error));
        Directory.Delete(link);
    }

    /// <summary>A compiled NVIDIA game whose key 33333333 has two files, and an app's key 44444444 nobody learned.</summary>
    async Task<(ScsKiller K, string[] Ours, string Other)> CompiledOnNvidia()
    {
        var k = await Warmed();
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        k.DriverInstalledAt = () => DateTime.UtcNow.AddDays(-1);
        k.CompileRunning = () => false;   // never the session-wide mutex: other tests of this run hold it
        string[] ours = [CacheFile("0002a91d33333333.nvph", 65536), CacheFile("32e6a91d33333333.nvph", 131072)];
        var other = CacheFile("0002a91d44444444.nvph", 4096);
        var rec = k.Store.LoadGame(_game.Id);
        (rec.CacheKeys, rec.WarmedKeys) = (["33333333"], ["33333333"]);
        k.Store.SaveGame(_game.Id, rec);
        k.RefreshGame(_game.Id);
        return (k, ours, other);
    }

    static CacheItem ItemOf(CacheInventory inv, CacheGroupKind kind, string name) =>
        inv.Groups.Single(g => g.Kind == kind).Items.Single(i => i.Name == name);

    [Fact]
    public async Task Deleting_a_games_item_takes_exactly_its_files_and_it_needs_a_compile_again()
    {
        var (k, ours, other) = await CompiledOnNvidia();
        var item = ItemOf(k.CacheInventory(), CacheGroupKind.Installed, "Fake Game");
        Assert.Equal([_game.Id], item.GameIds);

        var r = k.DeleteCaches([item]);
        Assert.Equal((65536L + 131072, 2, 0), (r.Bytes, r.Files, r.Skipped.Count));
        Assert.All(ours, f => Assert.False(File.Exists(f)));
        Assert.True(File.Exists(other));
        Assert.Equal((GameStatus.Ready, null), (k.Games.Single().Status, k.Games.Single().WarmedAt));
    }

    [Fact]
    public async Task Nothing_is_deleted_while_a_compile_runs_and_a_running_or_compiling_games_item_is_skipped()
    {
        var (k, ours, _) = await CompiledOnNvidia();
        k.Store.SaveGame("steam:42", new GameRecord { CacheKeys = ["55555555"], WarmedExeName = "Gone-Win64-Shipping.exe" });
        var gone = CacheFile("0002a91d55555555.nvph", 2048);
        var inv = k.CacheInventory();
        CacheItem[] both = [ItemOf(inv, CacheGroupKind.Installed, "Fake Game"), ItemOf(inv, CacheGroupKind.Uninstalled, "Gone-Win64-Shipping.exe (steam_42)")];

        k.CompileRunning = () => true;
        Assert.Throws<InvalidOperationException>(() => k.DeleteCaches(both));
        k.CompileRunning = () => false;

        k.ProcessNames = () => new HashSet<string>(["Fake-Win64-Shipping", "Gone-Win64-Shipping"], StringComparer.OrdinalIgnoreCase);
        var r = k.DeleteCaches(both);
        Assert.Equal(["Fake Game: Fake Game is running", "Gone-Win64-Shipping.exe (steam_42): Gone-Win64-Shipping.exe is running"], r.Skipped);
        k.ProcessNames = () => new HashSet<string>();

        // another process compiles the game: it holds the game's compile lock
        using (new FileStream(Path.Combine(k.Store.GameDir(_game.Id), "compile.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(["Fake Game: a compile of it is in progress"], k.DeleteCaches([both[0]]).Skipped);
        Assert.All([.. ours, gone], f => Assert.True(File.Exists(f)));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task A_file_changed_since_the_page_read_it_keeps_its_whole_key_and_the_rest_goes()
    {
        var (k, ours, other) = await CompiledOnNvidia();
        var inv = k.CacheInventory();
        File.WriteAllBytes(ours[1], new byte[262144]);   // the driver grew it meanwhile

        var r = k.DeleteCaches(inv.Everything(withOthers: true));
        Assert.Equal(["Fake Game: 32e6a91d33333333.nvph changed since the page read it"], r.Skipped);
        Assert.All(ours, f => Assert.True(File.Exists(f)));
        Assert.False(File.Exists(other));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);   // none of its files went
    }

    [Fact]
    public async Task A_file_in_use_is_skipped_with_its_process_named_and_its_key_kept_at_once()
    {
        var (k, ours, other) = await CompiledOnNvidia();
        var inv = k.CacheInventory();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        CacheDeleteResult r;
        using (new FileStream(ours[0], FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))   // as the driver holds it
            r = k.DeleteCaches(inv.Everything(withOthers: true));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");   // one try, no retry loop
        Assert.Equal($"Fake Game: files in use by {System.Diagnostics.Process.GetCurrentProcess().ProcessName}.exe (pid {Environment.ProcessId}) (0002a91d33333333.nvph)",
            Assert.Single(r.Skipped));
        Assert.All(ours, f => Assert.True(File.Exists(f)));
        Assert.False(File.Exists(other));
    }

    [Fact]
    public async Task A_delete_never_follows_a_link_inside_a_cache_folder_nor_leaves_the_cache_folders()
    {
        var (k, _, _) = await CompiledOnNvidia();
        var outside = Path.Combine(_root, "Documents");
        Directory.CreateDirectory(outside);
        var precious = Path.Combine(outside, "save.bin");
        File.WriteAllBytes(precious, new byte[1000]);
        var gl = Path.Combine(_root, "AMD", "GLCache");
        Directory.CreateDirectory(gl);
        File.WriteAllBytes(Path.Combine(gl, "a.bin"), new byte[100]);
        var link = Junction(Path.Combine(gl, "sub"), outside);
        try
        {
            var inv = k.CacheInventory();
            var amd = ItemOf(inv, CacheGroupKind.OtherVendor, "AMD OpenGL");
            Assert.Equal([Path.Combine(gl, "a.bin")], amd.Files.Select(f => f.Path));   // the link isn't listed

            var info = new FileInfo(precious);
            CacheItem Forged(string path) => new("forged", CacheGroupKind.Unattributed, [new(path, info.Length, info.LastWriteTimeUtc)], [], []);
            var r = k.DeleteCaches([Forged(Path.Combine(link, "save.bin")), Forged(precious)]);
            Assert.Equal(["forged: save.bin is reached through a link inside the cache folder", "forged: save.bin is outside the driver's cache folders"], r.Skipped);
            Assert.True(File.Exists(precious));
            Assert.Equal(0, r.Files);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task A_cache_folder_moved_with_a_junction_is_deleted_through_it_and_the_link_stays()
    {
        var k = await Warmed();
        var target = Path.Combine(_root, "D-drive", "DXCache");
        Directory.CreateDirectory(target);
        var link = Junction(Path.Combine(_root, "DXCache"), target);
        try
        {
            k.AppCache = new NvidiaAppCache(link);
            k.CompileRunning = () => false;
            File.WriteAllBytes(Path.Combine(target, "0002a91d44444444.nvph"), new byte[4096]);
            var r = k.DeleteCaches(k.CacheInventory().Everything(withOthers: true));
            Assert.Equal((4096L, 0), (r.Bytes, r.Skipped.Count));
            Assert.Empty(Directory.GetFiles(target));
            Assert.True(Directory.Exists(link));
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task A_file_the_driver_added_to_a_key_after_the_page_read_it_keeps_the_whole_key()
    {
        var (k, ours, _) = await CompiledOnNvidia();
        var item = ItemOf(k.CacheInventory(), CacheGroupKind.Installed, "Fake Game");
        var added = CacheFile("bc2fa91d33333333.nvph", 2048);   // a ray tracing file, written by the game played since
        // file times come from a coarser clock than the read's: one made just after the read can look older than it
        File.SetCreationTimeUtc(added, DateTime.UtcNow.AddMinutes(-5));

        Assert.Equal(["Fake Game: bc2fa91d33333333.nvph was added since the page read it"], k.DeleteCaches([item]).Skipped);
        Assert.All([.. ours, added], f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public async Task Another_game_with_the_same_exe_name_shares_the_item_and_a_new_owner_since_the_read_is_refused()
    {
        var (k, ours, other) = await CompiledOnNvidia();
        // an uninstalled game whose exe has the same name: NVIDIA keys the cache by that name, so it reads these files too
        k.Store.SaveGame("steam:77", new GameRecord { WarmedExeName = Path.GetFileName(_game.ExePath).ToUpperInvariant() });
        var shared = ItemOf(k.CacheInventory(), CacheGroupKind.Installed, $"Fake Game, {Path.GetFileName(_game.ExePath).ToUpperInvariant()} (steam_77)");
        Assert.Equal(new[] { _game.Id, "steam_77" }.Order(), shared.GameIds);

        // a game compiled as Old.exe before an update renamed its exe: another game still named Old.exe reads that key too
        var rec = k.Store.LoadGame(_game.Id);
        rec.WarmedExeName = "Old.exe";
        k.Store.SaveGame(_game.Id, rec);
        k.Store.SaveGame("steam:99", new GameRecord { LaunchedExeName = "Old.exe" });
        Assert.Contains("steam_99", ItemOf(k.CacheInventory(), CacheGroupKind.Installed,
            $"Fake Game, {Path.GetFileName(_game.ExePath).ToUpperInvariant()} (steam_77), Old.exe (steam_99)").GameIds);
        k.Store.SaveGame("steam:99", new GameRecord());
        shared = ItemOf(k.CacheInventory(), CacheGroupKind.Installed, $"Fake Game, {Path.GetFileName(_game.ExePath).ToUpperInvariant()} (steam_77)");

        // the app's key: unknown when the page read it, then a game seen running learned it
        var unknown = ItemOf(k.CacheInventory(), CacheGroupKind.Unattributed, "44444444");
        k.Store.SaveGame("steam:88", new GameRecord { CacheKeys = ["44444444"], WarmedExeName = "Other.exe" });
        Assert.Equal(["44444444: these files are now Other.exe (steam_88)'s"], k.DeleteCaches([unknown]).Skipped);
        Assert.True(File.Exists(other));

        Assert.Empty(k.DeleteCaches([shared]).Skipped);
        Assert.All(ours, f => Assert.False(File.Exists(f)));
    }

    [Fact]
    public async Task A_key_with_a_linked_file_is_kept_whole()
    {
        var (k, ours, _) = await CompiledOnNvidia();
        var outside = Path.Combine(_root, "elsewhere.bin");
        File.WriteAllBytes(outside, new byte[512]);
        // needs Developer Mode or admin, as on the PCs these tests run on
        File.CreateSymbolicLink(Path.Combine(_root, "DXCache", "bc2fa91d33333333.nvph"), outside);
        var item = ItemOf(k.CacheInventory(), CacheGroupKind.Installed, "Fake Game");
        Assert.Equal(2, item.Files.Count);   // the link isn't listed

        Assert.Equal(["Fake Game: bc2fa91d33333333.nvph is a link"], k.DeleteCaches([item]).Skipped);
        Assert.All([.. ours, outside], f => Assert.True(File.Exists(f)));
    }

    [Fact]
    public async Task No_compile_starts_while_a_cache_delete_runs()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        Task idle;
        using (Busy.Hold(ScsKiller.DeletingName))   // a delete in this or another process
        {
            k.Enqueue(_game.Id);
            k.StartQueue();
            idle = k.WhenQueueIdle();
            await Task.Delay(1500);
            Assert.False(idle.IsCompleted);
            Assert.Null(k.Games.Single().WarmedAt);
        }
        await idle.WaitAsync(HangGuard);
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }

    [Fact]
    public async Task Clear_everything_leaves_other_apps_unless_ticked_and_its_warning_names_them()
    {
        var (k, _, _) = await CompiledOnNvidia();
        var inv = k.CacheInventory();
        Assert.DoesNotContain(inv.Everything(withOthers: false), i => inv.Groups.Single(g => g.Kind == CacheGroupKind.Unattributed).Items.Contains(i));
        Assert.Contains(inv.Everything(withOthers: true), i => i.Name == "44444444");
        Assert.Contains($"The {ScsKiller.OtherApps.ToLowerInvariant()} group (0 MB) is kept unless you tick it", ScsKiller.EverythingWarning(inv, false));
        Assert.Contains($"The {ScsKiller.OtherApps.ToLowerInvariant()} group (0 MB) is included", ScsKiller.EverythingWarning(inv, true));
    }

    [Fact]
    public async Task Freeing_what_an_older_driver_wrote_keeps_the_games_compile()
    {
        var (k, ours, _) = await CompiledOnNvidia();
        var old = CacheFile("fc52a91d33333333.nvph", 1024);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        var inv = k.CacheInventory();
        Assert.Equal(1024, k.OldDriverBytes());
        var group = inv.Groups.Single(g => g.Kind == CacheGroupKind.OldDriver);
        Assert.Empty(Assert.Single(group.Items).GameIds);

        Assert.Equal(1024, k.DeleteCaches(group.Items).Bytes);
        Assert.False(File.Exists(old));
        Assert.All(ours, f => Assert.True(File.Exists(f)));
        Assert.Equal(GameStatus.Warmed, k.Games.Single().Status);
    }
}
