using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

// A Steam game uninstalled with the recorder in its folder, on a fake Steam library under the test's folder.
public partial class AppTests
{
    const string SteamId = "steam:4242";

    sealed class Uninstalled(AppTests t, ScsKiller k, Game g, FakeSource source)
    {
        public ScsKiller K => k;
        public Game G => g;
        public string ExeDir => Path.GetDirectoryName(g.ExePath)!;
        public string Apps => Path.GetDirectoryName(Path.GetDirectoryName(g.InstallDir))!;
        public string Manifest => Path.Combine(Apps, "appmanifest_4242.acf");
        public static readonly string[] Data = ["scskiller.log", "scskiller_creates.csv", "scskiller_frames.bin", "scskiller.db"];

        /// <summary>What Steam's uninstall does: the manifest and the game's files go, the folders holding the recorder's stay.</summary>
        public void Uninstall()
        {
            source.Games = [];
            File.Delete(Manifest);
            File.Delete(g.ExePath);
            if (Directory.Exists(Path.Combine(g.InstallDir, "Engine"))) Directory.Delete(Path.Combine(g.InstallDir, "Engine"), true);
            File.Delete(Path.Combine(g.InstallDir, "Fake.exe"));
        }

        public void Reinstall()
        {
            t.WriteManifest(g.InstallDir, 4);
            File.WriteAllBytes(g.ExePath, new byte[4096]);
            source.Games = [g];
        }

        public GameRecord Rec => k.Store.LoadGame(g.Id);

        /// <summary>The first sighting moved back past <see cref="ScsKiller.GoneFor"/>, as a later scan would find it.</summary>
        public void Later()
        {
            var r = Rec;
            Assert.NotNull(r.RecorderGoneSince);
            r.RecorderGoneSince -= ScsKiller.GoneFor + TimeSpan.FromMinutes(1);
            k.Store.SaveGame(g.Id, r);
        }

        public Task Scan() => k.ScanAsync(default);
        public string Log => File.ReadAllText(Path.Combine(k.Store.DataDir, "recorders.log"));
    }

    void WriteManifest(string install, int flags) =>
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(install))!, "appmanifest_4242.acf"),
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"4242\"\n\t\"name\"\t\t\"Fake Game\"\n\t\"StateFlags\"\t\t\"{flags}\"\n\t\"installdir\"\t\t\"{Path.GetFileName(install)}\"\n}}\n");

    /// <summary>The fixture's game moved into a Steam library as app 4242, its recorder installed and its data files written.</summary>
    async Task<Uninstalled> SteamRecorded(string? library = null)
    {
        var install = Path.Combine(library ?? Path.Combine(_root, "Lib"), "steamapps", "common", "FakeGame");
        Directory.CreateDirectory(Path.GetDirectoryName(install)!);
        Directory.Move(_game.InstallDir, install);
        var g = _game with { Id = SteamId, Store = Store.Steam, InstallDir = install, ExePath = Path.Combine(install, "Fake", "Binaries", "Win64", Path.GetFileName(_game.ExePath)) };
        WriteManifest(install, 4);
        var source = new FakeSource([g], Store.Steam);
        var k = Killer(new FakeReader(Unreal), source: source);
        k.ProcessNames = () => new HashSet<string>();
        await k.ScanAsync(default);
        k.InstallRecorder(g.Id);
        k.ManageRecorders = true;
        var u = new Uninstalled(this, k, g, source);
        foreach (var f in Uninstalled.Data) File.WriteAllBytes(Path.Combine(u.ExeDir, f), f == "scskiller.db" ? [] : [1, 2, 3]);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(u.ExeDir, "d3d12.dll")) && File.Exists(Path.Combine(u.ExeDir, "scskiller.ini")));
        Assert.NotNull(u.Rec.RecorderExe);
        return u;
    }

    [Fact]
    public async Task An_uninstalled_steam_games_recorder_goes_on_a_scan_an_hour_after_the_first_that_saw_it_with_its_empty_folders()
    {
        var u = await SteamRecorded();
        var other = Directory.CreateDirectory(Path.Combine(u.Apps, "common", "OtherGame")).FullName;   // the library's other games
        File.WriteAllBytes(Path.Combine(other, "d3d12.dll"), [1]);
        u.Uninstall();
        var files = Directory.GetFiles(u.ExeDir).Select(Path.GetFileName).OfType<string>().ToList();
        Assert.Contains("scskiller.ini", files);   // stamped
        await u.Scan();
        Assert.Equal(files, Directory.GetFiles(u.ExeDir).Select(Path.GetFileName).OfType<string>().ToList());   // the first sighting only notes it
        await u.Scan();
        Assert.True(Directory.Exists(u.ExeDir));   // not an hour yet
        u.Later();
        await u.Scan();
        Assert.False(Directory.Exists(u.G.InstallDir));
        Assert.True(File.Exists(Path.Combine(other, "d3d12.dll")));
        Assert.True(Directory.Exists(Path.Combine(u.Apps, "common")));
        var rec = u.Rec;
        Assert.Equal((null, null, 0), (rec.RecorderExe, rec.RecorderGoneSince, rec.RecorderFiles.Count));
        foreach (var f in files) Assert.Contains($"deleted {Path.Combine(u.ExeDir, f)}", u.Log);
        Assert.Contains($"deleted the empty folder {u.G.InstallDir}", u.Log);
    }

    [Fact]
    public async Task A_steam_game_being_updated_or_downloaded_keeps_its_recorder()
    {
        var u = await SteamRecorded();
        u.Uninstall();
        WriteManifest(u.G.InstallDir, 1026);   // update running: not fully installed, the exe not there yet
        await u.Scan();
        Assert.Null(u.Rec.RecorderGoneSince);
        File.Delete(u.Manifest);
        var staged = Directory.CreateDirectory(Path.Combine(u.Apps, "downloading", "4242")).FullName;
        await u.Scan();
        Assert.Null(u.Rec.RecorderGoneSince);
        Directory.Delete(staged);
        File.WriteAllText(Path.Combine(u.Apps, "downloading", "state_4242_4243.patch"), "");
        await u.Scan();
        Assert.Null(u.Rec.RecorderGoneSince);
        File.Delete(Path.Combine(u.Apps, "downloading", "state_4242_4243.patch"));
        await u.Scan();
        Assert.NotNull(u.Rec.RecorderGoneSince);   // nothing else held it back
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(u.ExeDir, "d3d12.dll")));
        Assert.True(File.Exists(Path.Combine(u.ExeDir, "scskiller.log")));
    }

    [Fact]
    public async Task A_steam_game_reinstalled_between_scans_keeps_its_recorder_and_a_later_uninstall_waits_again()
    {
        var u = await SteamRecorded();
        u.Uninstall();
        await u.Scan();
        u.Later();
        u.Reinstall();
        await u.Scan();
        Assert.Null(u.Rec.RecorderGoneSince);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(u.ExeDir, "d3d12.dll")));
        u.Uninstall();
        await u.Scan();
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(u.ExeDir, "d3d12.dll")));   // a fresh first sighting
    }

    [Fact]
    public async Task An_uninstalled_games_d3d12_dll_that_isnt_ours_and_a_changed_ini_stay_with_the_folders_holding_them()
    {
        var u = await SteamRecorded();
        u.Uninstall();
        var dll = Path.Combine(u.ExeDir, "d3d12.dll");
        File.WriteAllBytes(dll, [7, 7, 7]);   // the user's own, or a mod's
        File.AppendAllText(Path.Combine(u.ExeDir, "scskiller.ini"), "; edited\r\n");
        var edited = File.ReadAllBytes(Path.Combine(u.ExeDir, "scskiller.ini"));
        var saved = Directory.CreateDirectory(Path.Combine(u.G.InstallDir, "Fake", "Saved")).FullName;
        File.WriteAllText(Path.Combine(saved, "settings.txt"), "the user's");
        await u.Scan();
        u.Later();
        await u.Scan();
        Assert.Equal([7, 7, 7], File.ReadAllBytes(dll));
        Assert.Equal(edited, File.ReadAllBytes(Path.Combine(u.ExeDir, "scskiller.ini")));
        Assert.False(File.Exists(Path.Combine(u.ExeDir, "scskiller.log")) || File.Exists(Path.Combine(u.ExeDir, "scskiller.armed")));
        Assert.True(File.Exists(Path.Combine(saved, "settings.txt")));
        Assert.Contains($"left {dll}", u.Log);

        File.Delete(dll);
        File.Delete(Path.Combine(u.ExeDir, "scskiller.ini"));
        Assert.Null(u.Rec.RecorderExe);   // nothing more is the recorder's: the next scans leave the folder
        await u.Scan();
        Assert.True(Directory.Exists(u.ExeDir));
    }

    [Fact]
    public async Task An_uninstalled_games_folder_reached_through_a_junction_is_left()
    {
        var u = await SteamRecorded();
        u.Uninstall();
        var binaries = Path.GetDirectoryName(u.ExeDir)!;
        var outside = Path.Combine(_root, "Elsewhere");
        Directory.Move(binaries, outside);
        Junction(binaries, outside);
        try
        {
            await u.Scan();
            u.Later();
            await u.Scan();
            Assert.True(ScsKiller.IsOurProxy(Path.Combine(outside, "Win64", "d3d12.dll")));
            Assert.True(File.Exists(Path.Combine(outside, "Win64", "scskiller.log")));
            Assert.Contains("is a link", u.Log);
        }
        finally { Directory.Delete(binaries); }
    }

    [Fact]
    public async Task An_uninstalled_games_data_file_that_cant_be_deleted_is_retried_on_the_next_scan()
    {
        var u = await SteamRecorded();
        u.Uninstall();
        await u.Scan();
        u.Later();
        var log = Path.Combine(u.ExeDir, "scskiller.log");
        using (new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.Read))
            await u.Scan();
        Assert.True(File.Exists(log));
        Assert.False(ScsKiller.IsOurProxy(Path.Combine(u.ExeDir, "d3d12.dll")));
        Assert.Contains($"deleted {Path.Combine(u.ExeDir, "d3d12.dll")}", u.Log);
        Assert.NotNull(u.Rec.RecorderExe);
        await u.Scan();
        Assert.False(Directory.Exists(u.G.InstallDir));
        Assert.Null(u.Rec.RecorderExe);
    }

    [Fact]
    public async Task An_uninstalled_game_in_a_library_reached_through_a_junction_is_left()
    {
        var lib = Junction(Path.Combine(_root, "LinkedLib"), Directory.CreateDirectory(Path.Combine(_root, "Lib")).FullName);
        try
        {
            var u = await SteamRecorded(lib);
            u.Uninstall();
            await u.Scan();
            u.Later();
            await u.Scan();
            Assert.True(ScsKiller.IsOurProxy(Path.Combine(u.ExeDir, "d3d12.dll")));
            Assert.Contains($"{lib} is a link", u.Log);
        }
        finally { Directory.Delete(lib); }
    }

    sealed class FailingSource : IGameSource
    {
        public Store Store => Store.Other;
        public IReadOnlyList<Game> Discover() => throw new IOException("unreadable");
    }

    [Fact]
    public async Task No_leftover_is_removed_on_a_scan_whose_discovery_failed()
    {
        var u = await SteamRecorded();
        u.Uninstall();
        await u.Scan();
        u.Later();
        var k = Killer(new FakeReader(Unreal), sources: [new FakeSource([], Store.Steam), new FailingSource()]);
        k.ProcessNames = () => new HashSet<string>();
        k.ManageRecorders = true;
        await k.ScanAsync(default);
        Assert.True(ScsKiller.IsOurProxy(Path.Combine(u.ExeDir, "d3d12.dll")));
        Assert.NotNull(u.Rec.RecorderGoneSince);
    }

    [Fact]
    public void Steam_uninstalled_needs_a_steam_id_and_its_library_there_on_a_fixed_ready_drive()
    {
        var install = Path.Combine(_root, "Lib", "steamapps", "common", "Gone");
        Directory.CreateDirectory(Path.Combine(install, "Bin"));
        var exe = Path.Combine(install, "Bin", "game.exe");
        Assert.True(ScsKiller.SteamUninstalled(SteamId, exe, install));
        Assert.True(ScsKiller.SteamUninstalled("steam_4242", exe, install));   // a record's folder name
        Assert.False(ScsKiller.SteamUninstalled("epic:4242", exe, install));
        Assert.False(ScsKiller.SteamUninstalled(SteamId, exe, Path.Combine(install, "Bin")));   // not the library's own game folder
        var unc = @"\\localhost\" + install[0] + "$" + install[2..];
        Assert.False(ScsKiller.SteamUninstalled(SteamId, Path.Combine(unc, "Bin", "game.exe"), unc));
        var gone = Path.Combine(_root, "RemovedLib", "steamapps", "common", "Gone");   // the library's folder missing
        Assert.False(ScsKiller.SteamUninstalled(SteamId, Path.Combine(gone, "Bin", "game.exe"), gone));
        var free = Enumerable.Range('F', 20).Select(c => $"{(char)c}:\\").First(d => !Directory.Exists(d));
        var offline = Path.Combine(free, "Lib", "steamapps", "common", "Gone");
        Assert.False(ScsKiller.SteamUninstalled(SteamId, Path.Combine(offline, "Bin", "game.exe"), offline));
    }

    /// <summary>A collection's game: its app id is the one before the suffix, and one in a folder of its own (Mass Effect
    /// Legendary Edition's Game\ME3) is in the app's folder.</summary>
    [Fact]
    public void Steam_uninstalled_takes_a_collection_games_app_id_and_the_apps_folder()
    {
        var apps = Path.Combine(_root, "Lib", "steamapps");
        var uncharted = Directory.CreateDirectory(Path.Combine(apps, "common", "Uncharted")).FullName;
        Assert.True(ScsKiller.SteamUninstalled("steam:1659420:tll", Path.Combine(uncharted, "tll.exe"), uncharted));
        Assert.True(ScsKiller.SteamUninstalled("steam_1659420_tll", Path.Combine(uncharted, "tll.exe"), uncharted));   // a record's folder name
        foreach (var (id, me) in new[] { ("steam:1328670", "ME3"), ("steam_1328670_me1", "ME1") })
        {
            var dir = Directory.CreateDirectory(Path.Combine(apps, "common", "Mass Effect Legendary Edition", "Game", me)).FullName;
            var exe = Path.Combine(dir, "Binaries", "Win64", $"MassEffect{me[^1]}.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            Assert.True(ScsKiller.SteamUninstalled(id, exe, dir));
            Assert.False(ScsKiller.SteamUninstalled("steam:4242", exe, dir));   // not in a collection: Game\ME3 isn't the library's own game folder
        }
        var app = Path.Combine(apps, "common", "Mass Effect Legendary Edition");   // a record from before: the recorder's install root is the app's
        Assert.True(ScsKiller.SteamUninstalled("steam:1328670", Path.Combine(app, "Game", "ME3", "Binaries", "Win64", "MassEffect3.exe"), app));
        File.WriteAllText(Path.Combine(apps, "appmanifest_1328670.acf"), "");
        var me3 = Path.Combine(apps, "common", "Mass Effect Legendary Edition", "Game", "ME3");
        Assert.False(ScsKiller.SteamUninstalled("steam:1328670", Path.Combine(me3, "Binaries", "Win64", "MassEffect3.exe"), me3));
    }
}
