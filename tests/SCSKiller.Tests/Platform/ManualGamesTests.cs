using System.Buffers.Binary;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

public sealed class ManualGamesTests : IDisposable
{
    readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "scskiller-manual-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_root, true);

    /// <summary>A minimal x64 exe; <paramref name="imports"/>: the one DLL its import table names (a graphics API makes it the game).</summary>
    internal static byte[] Exe(string? imports = null, int padding = 0)
    {
        var pe = Planning.MiddlewarePackTests.Pe(null);
        if (imports != null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x58 + 120), 0x1100);          // import directory RVA
            BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x200 + 0x100 + 12), 0x1180);  // its one descriptor's Name RVA
            System.Text.Encoding.ASCII.GetBytes(imports).CopyTo(pe, 0x200 + 0x180);
        }
        return [.. pe, .. new byte[padding]];
    }

    /// <summary>&lt;root&gt;\Game.exe (Unreal's launcher stub), Game\Binaries\Win64\Game-Win64-Shipping.exe, Engine\.</summary>
    internal static (string Root, string Stub, string Shipping) UnrealLayout(string root)
    {
        var bin = Directory.CreateDirectory(Path.Combine(root, "Game", "Binaries", "Win64")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "Engine", "Binaries", "Win64"));
        File.WriteAllBytes(Path.Combine(root, "Engine", "Binaries", "Win64", "CrashReportClient.exe"), Exe(padding: 20_000));
        var shipping = Path.Combine(bin, "Game-Win64-Shipping.exe");
        File.WriteAllBytes(shipping, Exe(padding: 8192));
        var stub = Path.Combine(root, "Game.exe");
        File.WriteAllBytes(stub, Exe());
        return (root, stub, shipping);
    }

    [Fact]
    public void An_unreal_launcher_stub_resolves_to_its_shipping_exe_and_install_root()
    {
        var (root, stub, shipping) = UnrealLayout(Path.Combine(_root, "Some Game"));
        Assert.Equal(new ManualEntry(shipping, root, "Some Game"), ManualSource.Resolve(stub));
        Assert.Equal(new ManualEntry(shipping, root, "Some Game"), ManualSource.Resolve(shipping));   // the root from <Project>\Binaries\Win64
        Assert.Equal(ManualSource.IdOf(shipping), ManualSource.IdOf(shipping.ToUpperInvariant()));
    }

    [Fact]
    public void A_launcher_resolves_to_the_one_exe_that_loads_d3d_and_several_are_ambiguous()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Plain")).FullName;
        var launcher = Path.Combine(dir, "Launcher.exe");
        File.WriteAllBytes(launcher, Exe(padding: 50_000));   // the largest, importing nothing graphic
        File.WriteAllBytes(Path.Combine(dir, "game_dx12.exe"), Exe("d3d12.dll"));
        Assert.Equal(Path.Combine(dir, "game_dx12.exe"), ManualSource.Resolve(launcher).Exe);
        Assert.Equal(Path.Combine(dir, "game_dx12.exe"), ManualSource.Resolve(Path.Combine(dir, "game_dx12.exe")).Exe);   // the game itself: kept

        File.WriteAllBytes(Path.Combine(dir, "game_dx11.exe"), Exe("d3d11.dll"));
        var e = Assert.Throws<ArgumentException>(() => ManualSource.Resolve(launcher));
        Assert.Contains("can't tell which", e.Message);
        Assert.Contains("game_dx11.exe", e.Message);
    }

    /// <summary>Returnal: a modular Unreal build whose Shipping exe imports its RHI module, not a graphics API, beside a
    /// 32-bit Launcher.exe that does. The pick is the game.</summary>
    [Fact]
    public void A_modular_unreal_shipping_exe_is_kept_over_a_32_bit_launcher_beside_it()
    {
        var (root, stub, shipping) = UnrealLayout(Path.Combine(_root, "Modular"));
        File.WriteAllBytes(shipping, Exe("Game-RHI-Win64-Shipping.dll", padding: 8192));
        var launcher = Exe("d3d11.dll");
        BinaryPrimitives.WriteUInt16LittleEndian(launcher.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(launcher.AsSpan(0x3C)) + 4), 0x14C);   // i386
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(shipping)!, "Launcher.exe"), launcher);
        Assert.Equal(new ManualEntry(shipping, root, "Modular"), ManualSource.Resolve(shipping));
        Assert.Equal(shipping, ManualSource.Resolve(stub).Exe);
        File.WriteAllBytes(shipping, Exe(padding: 8192));   // one whose imports name nothing graphic (a packed exe): still not the launcher
        Assert.Equal(shipping, ManualSource.Resolve(shipping).Exe);
    }

    /// <summary>Returnal's Launcher.exe is 32-bit and larger than its Shipping exe: discovery and the launcher stub still find
    /// the game, also with anti-cheat in the install, where no exe is read and the Shipping name decides.</summary>
    [Fact]
    public void A_larger_32_bit_launcher_beside_the_shipping_exe_is_never_the_game()
    {
        var (root, stub, shipping) = UnrealLayout(Path.Combine(_root, "Returnal"));
        var launcher = Exe("d3d11.dll", padding: 50_000);
        BinaryPrimitives.WriteUInt16LittleEndian(launcher.AsSpan(BinaryPrimitives.ReadInt32LittleEndian(launcher.AsSpan(0x3C)) + 4), 0x14C);   // i386
        var launcherPath = Path.Combine(Path.GetDirectoryName(shipping)!, "Launcher.exe");
        File.WriteAllBytes(launcherPath, launcher);
        Assert.Equal(shipping, GameFiles.FindExe(root));
        Assert.Equal(new ManualEntry(shipping, root, "Returnal"), ManualSource.Resolve(stub));
        Directory.CreateDirectory(Path.Combine(root, "EasyAntiCheat"));
        Assert.Equal(shipping, GameFiles.FindExe(root));
    }

    /// <summary>Returnal from Steam: the 64-bit Epic Online Services installer is the largest exe in Binaries\Win64.</summary>
    [Fact]
    public void Installers_and_engine_helpers_beside_an_unreal_exe_are_never_the_game()
    {
        var (root, stub, shipping) = UnrealLayout(Path.Combine(_root, "Returnal"));
        var bin = Path.GetDirectoryName(shipping)!;
        foreach (var helper in new[] { "EpicOnlineServicesInstaller.exe", "CrashReportClient.exe", "UnrealCEFSubProcess.exe",
                     "EasyAntiCheat_EOS_Setup.exe", "vc_redist.x64.exe", "dxsetup.exe", "UE4PrereqSetup_x64.exe" })
            File.WriteAllBytes(Path.Combine(bin, helper), Exe("d3d11.dll", padding: 50_000));
        Assert.Equal(shipping, GameFiles.FindExe(root));
        Assert.Equal(shipping, ManualSource.Resolve(stub).Exe);

        var game = Path.Combine(bin, "Game.exe");   // a game exe not named for Shipping: the largest that is no helper
        File.Move(shipping, game);
        Assert.Equal(game, GameFiles.FindExe(root));
    }

    [Fact]
    public void A_suggested_folder_sees_anti_cheat_in_the_folders_above_it_and_a_confirmed_folder_is_checked_whole()
    {
        var game = Directory.CreateDirectory(Path.Combine(_root, "Shooter")).FullName;
        Directory.CreateDirectory(Path.Combine(game, "EasyAntiCheat"));
        var retail = Directory.CreateDirectory(Path.Combine(game, "bin64", "retail")).FullName;
        var exe = Path.Combine(retail, "shooter.exe");
        File.WriteAllBytes(exe, Exe("d3d12.dll"));
        var e = ManualSource.Resolve(exe);
        Assert.Equal((exe, retail), (e.Exe, e.InstallDir));   // a suggestion: the user confirms or changes it
        Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(ManualSource.ToGame(e)));   // in the folder above
        Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(ManualSource.ToGame(e with { InstallDir = game })));
        Assert.Null(ManualSource.RootProblem(game, exe, []));
    }

    [Fact]
    public void A_folder_of_many_games_or_a_drive_is_never_a_game_folder()
    {
        string Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;
        var exe = Path.Combine(_root, "x.exe");
        Assert.Contains("whole drive", ManualSource.RootProblem(Path.GetPathRoot(_root)!, exe, []));
        Assert.Contains("doesn't exist", ManualSource.RootProblem(Path.Combine(_root, "gone"), exe, []));
        Assert.Contains("many programs", ManualSource.RootProblem(Dir("SteamLibrary", "steamapps", "common"), exe, []));
        Assert.Contains("many programs", ManualSource.RootProblem(Dir("XboxGames"), exe, []));
        Assert.Contains("many programs", ManualSource.RootProblem(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), exe, []));
        var library = Dir("Library");
        foreach (var g in new[] { "One", "Two" }) File.WriteAllBytes(Path.Combine(Dir("Library", g), g + ".exe"), Exe());
        Assert.Contains("several games", ManualSource.RootProblem(library, exe, []));
        var dcs = Dir("DCS World");   // two builds of one program: bin\DCS.exe and bin-mt\DCS.exe
        foreach (var b in new[] { "bin", "bin-mt" }) File.WriteAllBytes(Path.Combine(Dir("DCS World", b), "DCS.exe"), Exe("d3d11.dll"));
        Assert.Null(ManualSource.RootProblem(dcs, Path.Combine(dcs, "bin-mt", "DCS.exe"), []));
        File.WriteAllBytes(Path.Combine(Dir("DCS World", "Other"), "other.exe"), Exe());
        File.WriteAllBytes(Path.Combine(Dir("DCS World", "Third"), "third.exe"), Exe());
        Assert.Contains("several games", ManualSource.RootProblem(dcs, Path.Combine(dcs, "bin-mt", "DCS.exe"), []));
        var listed = new Game("steam:1", "Listed Game", Store.Steam, Dir("Shelf", "Listed"), Path.Combine(_root, "Shelf", "Listed", "l.exe"));
        Assert.Contains("Listed Game", ManualSource.RootProblem(Path.Combine(_root, "Shelf"), exe, [listed]));

        var (unreal, _, shipping) = UnrealLayout(Path.Combine(_root, "Unreal Game"));
        File.WriteAllBytes(Path.Combine(Dir("Unreal Game", "Engine", "Binaries", "ThirdParty"), "helper.exe"), Exe());
        Assert.Null(ManualSource.RootProblem(unreal, shipping, [listed]));
        var red = Dir("Red Game");
        File.WriteAllBytes(Path.Combine(Dir("Red Game", "bin", "x64"), "game.exe"), Exe("d3d12.dll"));
        File.WriteAllBytes(Path.Combine(Dir("Red Game", "_CommonRedist"), "vcredist.exe"), Exe());
        Dir("Red Game", "content");
        Assert.Null(ManualSource.RootProblem(red, Path.Combine(red, "bin", "x64", "game.exe"), []));
        Assert.Equal(red, ManualSource.Resolve(Path.Combine(red, "bin", "x64", "game.exe")).InstallDir);
    }

    [Fact]
    public void Picks_that_are_no_64_bit_program_are_refused()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "Bad")).FullName;
        string Put(string name, byte[] bytes) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, bytes); return p; }
        Assert.Contains("isn't a program", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Put("game.lnk", Exe()))).Message);
        Assert.Contains("isn't a Windows program", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Put("text.exe", "hello"u8.ToArray()))).Message);
        Assert.Contains("doesn't exist", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Path.Combine(dir, "gone.exe"))).Message);
        var wow = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "cmd.exe");
        Assert.Contains("32-bit", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(Put("old.exe", File.ReadAllBytes(wow)))).Message);
    }

    [Fact]
    public void The_exe_a_pick_resolves_to_is_checked_too()
    {
        var (_, stub, shipping) = UnrealLayout(Path.Combine(_root, "Broken"));
        File.WriteAllBytes(shipping, new byte[50_000]);   // still the largest exe under Binaries\Win64
        Assert.Contains("Game-Win64-Shipping.exe isn't a Windows program", Assert.Throws<ArgumentException>(() => ManualSource.Resolve(stub)).Message);
    }

    [Fact]
    public void Malformed_entries_are_left_out_and_the_rest_kept()
    {
        var (root, _, shipping) = UnrealLayout(Path.Combine(_root, "Some Game"));
        var data = Directory.CreateDirectory(Path.Combine(_root, "data")).FullName;
        var good = System.Text.Json.JsonSerializer.Serialize(new ManualEntry(shipping, root, "Some Game"));
        File.WriteAllText(Path.Combine(data, "manual-games.json"),
            """[null, 7, "x", {}, {"Exe": "relative.exe", "InstallDir": "dir"}, {"Exe": "C:/a\u0000b.exe", "InstallDir": "C:/a"}, """ + good + "]");
        var source = new ManualSource(new AppStore(data));
        Assert.Equal(shipping, Assert.Single(source.Discover()).ExePath);

        var other = UnrealLayout(Path.Combine(_root, "Other Game"));
        Assert.False(source.Add(new ManualEntry(other.Shipping, other.Root, "Other Game")).Existed);
        Assert.Equal(2, new ManualSource(new AppStore(data)).Discover().Count);
    }

    [Fact]
    public void Adds_from_two_sources_at_once_lose_nothing()
    {
        var data = Path.Combine(_root, "data");
        ManualSource a = new(new AppStore(data)), b = new(new AppStore(data));
        Parallel.For(0, 40, i => (i % 2 == 0 ? a : b).Add(new ManualEntry(Path.Combine(_root, $"g{i}", "g.exe"), Path.Combine(_root, $"g{i}"), $"g{i}")));
        Assert.Equal(40, a.Entries().Count);
        Parallel.For(0, 40, i => Assert.True((i % 2 == 0 ? b : a).Remove(ManualSource.IdOf(Path.Combine(_root, $"g{i}", "g.exe")))));
        Assert.Empty(b.Entries());
    }

    [Fact]
    public void Added_games_persist_in_the_data_folder_and_are_removed_by_id()
    {
        var (root, _, shipping) = UnrealLayout(Path.Combine(_root, "Some Game"));
        var data = Path.Combine(_root, "data");
        var entry = new ManualEntry(shipping, root, "Some Game");
        var (added, existed) = new ManualSource(new AppStore(data)).Add(entry);
        Assert.False(existed);
        Assert.True(File.Exists(Path.Combine(data, "manual-games.json")));

        var source = new ManualSource(new AppStore(data));   // as the next start reads it
        var g = Assert.Single(source.Discover());
        Assert.Equal(new Game(added.Id, "Some Game", Store.Manual, root, shipping), g);
        Assert.StartsWith("manual:", g.Id);
        Assert.True(source.Add(entry).Existed);

        File.Move(shipping, shipping + ".away");   // a drive unplugged: not listed, still kept
        Assert.Empty(source.Discover());
        File.Move(shipping + ".away", shipping);
        Assert.Single(source.Discover());

        Assert.True(source.Remove(g.Id));
        Assert.Empty(new ManualSource(new AppStore(data)).Discover());
        Assert.False(source.Remove(g.Id));
        Assert.True(File.Exists(shipping));
    }

    /// <summary>Folder\Some Game: an Unreal launcher stub at its root; Folder\Xbox Game\Content: an Xbox app game's bootstrap stub
    /// beside its WinGDK Shipping exe. Both stubs load D3D and outweigh the game.</summary>
    (string Folder, string Unreal, string Shipping, string Content, string Gdk) GameFolder()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, "Games")).FullName;
        var (unreal, stub, shipping) = UnrealLayout(Path.Combine(folder, "Some Game"));
        File.WriteAllBytes(stub, Exe("d3d12.dll", padding: 60_000));
        var content = Directory.CreateDirectory(Path.Combine(folder, "Xbox Game", "Content")).FullName;
        Directory.CreateDirectory(Path.Combine(content, "Engine"));
        File.WriteAllBytes(Path.Combine(content, "gamelaunchhelper.exe"), Exe("d3d12.dll", padding: 60_000));
        File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Game configVersion="1"><Identity Name="Sample.XGame" Publisher="CN=Sample" Version="1.0.0.0" />
            <ExecutableList><Executable Name="gamelaunchhelper.exe" Id="Game" /></ExecutableList></Game>
            """);
        var gdk = Path.Combine(Directory.CreateDirectory(Path.Combine(content, "XGame", "Binaries", "WinGDK")).FullName, "XGame-WinGDK-Shipping.exe");
        File.WriteAllBytes(gdk, Exe(padding: 8192));
        Directory.CreateDirectory(Path.Combine(folder, "Not a game"));
        return (folder, unreal, shipping, content, gdk);
    }

    [Fact]
    public void A_drive_roots_windows_folders_are_never_a_game_and_a_hidden_game_folder_is_found()
    {
        string Dir(params string[] parts) => Directory.CreateDirectory(Path.Combine([_root, .. parts])).FullName;
        void Hide(string path, FileAttributes a) => File.SetAttributes(path, File.GetAttributes(path) | a);
        var drive = Dir("Drive");
        var game = Path.Combine(Dir("Drive", "Hidden Game", "bin"), "game.exe");
        File.WriteAllBytes(game, Exe("d3d12.dll"));
        Hide(Path.Combine(drive, "Hidden Game"), FileAttributes.Hidden);
        Hide(game, FileAttributes.Hidden);
        foreach (var w in new[] { Path.Combine("$RECYCLE.BIN", "S-1-5-21-1000"), "System Volume Information" })
        {
            File.WriteAllBytes(Path.Combine(Dir(["Drive", .. w.Split(Path.DirectorySeparatorChar)]), "deleted.exe"), Exe("d3d12.dll", padding: 100_000));
            Hide(Path.Combine(drive, w.Split(Path.DirectorySeparatorChar)[0]), FileAttributes.Hidden | FileAttributes.System);
        }

        var source = new ManualSource(new AppStore(Path.Combine(_root, "data")));
        source.AddFolder(drive);
        Assert.Equal(game, Assert.Single(source.Discover()).ExePath);
        // a game whose folder is the drive itself: the larger exes in Windows' folders are never picked or offered
        Assert.Equal(game, GameFiles.FindExe(drive));
        Assert.Equal([game], GameFiles.GraphicsExes(drive));
    }

    [Fact]
    public void A_folder_lists_each_game_in_it_by_its_shipping_exe_never_a_launcher_or_bootstrap_stub()
    {
        var (folder, unreal, shipping, content, gdk) = GameFolder();
        File.WriteAllBytes(Path.Combine(folder, "Setup.exe"), Exe());   // a program beside the games: still a folder of games
        var source = new ManualSource(new AppStore(Path.Combine(_root, "data")));
        source.AddFolder(folder);
        source.AddFolder(folder + Path.DirectorySeparatorChar);   // kept once
        Assert.Equal([folder], source.Folders);
        var games = source.Discover().OrderBy(g => g.InstallDir).ToList();
        Assert.Equal([(unreal, shipping, "Some Game"), (content, gdk, "Xbox Game")], games.Select(g => (g.InstallDir, g.ExePath, g.Name)));
        Assert.All(games, g => Assert.Equal((Store.Manual, ManualSource.IdOf(g.ExePath)), (g.Store, g.Id)));
        Assert.Equal(shipping, ManualSource.Resolve(shipping).Exe);   // a pick of the Shipping exe stays it too
        Assert.Empty(source.Entries());   // looked for at every scan, nothing saved per game

        source.Known = games.ToDictionary(g => g.Id);   // the next scan reuses what the last one found
        Assert.Equal(games, source.Discover().OrderBy(g => g.InstallDir));
        source.RemoveFolder(folder);
        Assert.Empty(source.Discover());

        source.AddFolder(unreal);   // one game's own folder
        Assert.Equal((unreal, shipping), Assert.Single(source.Discover()) is var one ? (one.InstallDir, one.ExePath) : default);
    }

    [Fact]
    public void A_game_found_in_a_folder_and_removed_stays_removed_until_added_by_its_exe()
    {
        var (folder, unreal, shipping, _, _) = GameFolder();
        var source = new ManualSource(new AppStore(Path.Combine(_root, "data")));
        source.AddFolder(folder);
        var id = ManualSource.IdOf(shipping);
        Assert.False(source.Remove(id));   // no entry: remembered as removed
        Assert.DoesNotContain(source.Discover(), g => g.Id == id);
        Assert.False(source.Add(ManualSource.Resolve(shipping)).Existed);
        Assert.Equal(unreal, Assert.Single(source.Discover(), g => g.Id == id).InstallDir);
    }

    [Fact]
    public void Renaming_an_added_game_changes_its_name_and_never_its_id_exe_or_folder()
    {
        var (folder, _, _, _, gdk) = GameFolder();
        var data = Path.Combine(_root, "data");
        var source = new ManualSource(new AppStore(data));
        var (root, _, shipping) = UnrealLayout(Path.Combine(_root, "Picked"));
        var picked = source.Add(new ManualEntry(shipping, root, "Picked")).Game;
        source.AddFolder(folder);
        var before = source.Discover();
        var entries = File.ReadAllText(Path.Combine(data, "manual-games.json"));
        var found = ManualSource.IdOf(gdk);

        source.Rename(picked.Id, "  My Game ");
        source.Rename(found, "My Xbox Game");
        Assert.Throws<ArgumentException>(() => source.Rename(picked.Id, " "));
        var after = new ManualSource(new AppStore(data)).Discover();   // as the next start reads it
        Assert.Equal(before.Select(g => g with { Name = "" }), after.Select(g => g with { Name = "" }));
        Assert.Equal(["My Game", "Some Game", "My Xbox Game"], after.Select(g => g.Name));
        Assert.Equal(entries, File.ReadAllText(Path.Combine(data, "manual-games.json")));
        Assert.Equal(picked.Id, ManualSource.IdOf(Assert.Single(source.Entries()).Exe));

        // confirming the folder of a renamed game keeps the name
        Assert.Equal((found, "My Xbox Game"), source.Add(ManualSource.Resolve(gdk) with { Confirmed = true }).Game is var g ? (g.Id, g.Name) : default);
    }
}
