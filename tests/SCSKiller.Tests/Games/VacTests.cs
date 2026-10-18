using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Games;

/// <summary>VAC (Games.Vac): Steam's flag in appinfo.vdf, Valve's own multiplayer titles, a game in a Steam library whatever
/// store found it, and VAC whenever Steam's data can't say.</summary>
public class VacTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("scsk-vac").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    static Game Steam(uint id) => new($"steam:{id}", $"App {id}", Store.Steam, @"C:\nowhere", @"C:\nowhere\game.exe");

    /// <summary>An appinfo.vdf (v28: inline keys). Each app: its info state (2 = Steam has its info) and its categories;
    /// null categories = no common section at all.</summary>
    public static void WriteAppInfo(string path, params (uint Id, uint State, string[]? Categories)[] apps) =>
        WriteAppInfo(path, [.. apps.Select(a => (a.Id, a.State, a.Categories, (string[]?)null))]);

    /// <summary>The same, with the executables of each app's launch options.</summary>
    public static void WriteAppInfo(string path, params (uint Id, uint State, string[]? Categories, string[]? Launches)[] apps)
    {
        using var w = new BinaryWriter(File.Create(path));
        w.Write(0x07564428u);
        w.Write(1u);   // universe
        foreach (var (id, state, cats, launches) in apps)
        {
            var kv = new MemoryStream();
            var k = new BinaryWriter(kv);
            void Key(byte type, string name) { k.Write(type); k.Write(Encoding.UTF8.GetBytes(name)); k.Write((byte)0); }
            Key(0, "appinfo");
            if (cats != null)
            {
                Key(0, "common");
                Key(1, "type");
                k.Write("Game"u8);
                k.Write((byte)0);
                if (cats.Length > 0)
                {
                    Key(0, "category");
                    foreach (var c in cats) { Key(2, c); k.Write(1); }
                    k.Write((byte)8);
                }
                k.Write((byte)8);
            }
            if (launches != null)
            {
                Key(0, "config");
                Key(0, "launch");
                for (var i = 0; i < launches.Length; i++)
                {
                    Key(0, i.ToString());
                    Key(1, "executable");
                    k.Write(Encoding.UTF8.GetBytes(launches[i]));
                    k.Write((byte)0);
                    k.Write((byte)8);
                }
                k.Write((byte)8); k.Write((byte)8);   // launch, config
            }
            k.Write((byte)8); k.Write((byte)8);   // appinfo, root
            var body = kv.ToArray();
            w.Write(id);
            w.Write((uint)(60 + body.Length));
            w.Write(state);
            w.Write(new byte[56]);   // lastUpdated, picsToken, sha1, changeNumber, binary sha1
            w.Write(body);
        }
        w.Write(0u);
    }

    string AppInfo(params (uint Id, uint State, string[]? Categories)[] apps)
    {
        var path = Path.Combine(_dir, $"appinfo{Guid.NewGuid():N}.vdf");
        WriteAppInfo(path, apps);
        return path;
    }

    [Fact]
    public void Steam_flags_Valve_titles_and_unknown_data_are_VAC()
    {
        var info = AppInfo((239140, 2, ["category_1", "category_8"]), (374320, 2, ["category_1", "category_2"]), (1422450, 2, ["category_1"]),
            (500000, 1, null), (500001, 2, null), (500002, 2, []), (500003, 1, []));
        var root = Path.Combine(_dir, "no-steam");
        bool Is(uint id) => Vac.Is(Steam(id), info, root);
        Assert.True(Is(239140));    // Steam's "Valve Anti-Cheat enabled"
        Assert.False(Is(374320));   // flagged otherwise
        Assert.True(Is(1422450));   // Deadlock: Valve's, no flag
        Assert.True(Is(730));       // Counter-Strike 2, not in this file
        Assert.True(Is(999));       // not in Steam's data: can't tell
        Assert.True(Is(500000));    // Steam's "no info" entry
        Assert.True(Is(500001));    // info but no common section
        Assert.False(Is(500002));   // complete info, no categories
        Assert.True(Is(500003));    // no categories and no complete info
        Assert.True(Vac.Is(Steam(374320), Path.Combine(_dir, "missing.vdf"), root));
        Assert.All(new uint[] { 730, 570, 440, 1422450 }, id => Assert.Contains(id, Vac.Valve));
    }

    /// <summary>A game another store (or the user) added is Steam's when its folder is in a Steam library: the manifest that
    /// names the folder gives the app; none naming it, or anything else in the library, is VAC. Outside every library: not.</summary>
    [Fact]
    public void A_game_in_a_Steam_library_is_Steam_s_whatever_found_it()
    {
        var steam = Directory.CreateDirectory(Path.Combine(_dir, "Steam")).FullName;
        var lib = Directory.CreateDirectory(Path.Combine(_dir, "Lib")).FullName;
        Directory.CreateDirectory(Path.Combine(steam, "steamapps"));
        File.WriteAllText(Path.Combine(steam, "steamapps", "libraryfolders.vdf"),
            $"\"libraryfolders\"\n{{\n\t\"0\"\n\t{{\n\t\t\"path\"\t\t\"{steam.Replace("\\", "\\\\")}\"\n\t}}\n\t\"1\"\n\t{{\n\t\t\"path\"\t\t\"{lib.Replace("\\", "\\\\")}\"\n\t}}\n}}\n");
        Directory.CreateDirectory(Path.Combine(lib, "steamapps", "common"));
        void Manifest(uint id, string dir) => File.WriteAllText(Path.Combine(lib, "steamapps", $"appmanifest_{id}.acf"),
            $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n");
        Manifest(239140, "Dying Light");
        Manifest(374320, "DARK SOULS III");
        var info = AppInfo((239140, 2, ["category_8"]), (374320, 2, ["category_1"]));
        Game Manual(string folder) => new("manual:" + folder, folder, Store.Manual, folder, Path.Combine(folder, "game.exe"));
        Assert.True(Vac.Is(Manual(Path.Combine(lib, "steamapps", "common", "Dying Light")), info, steam));
        Assert.False(Vac.Is(Manual(Path.Combine(lib, "steamapps", "common", "DARK SOULS III", "Game")), info, steam));
        Assert.True(Vac.Is(Manual(Path.Combine(lib, "steamapps", "common", "Unlisted")), info, steam));   // no manifest names it
        Assert.True(Vac.Is(Manual(Path.Combine(lib, "elsewhere")), info, steam));                         // in the library, not a game folder
        Assert.False(Vac.Is(Manual(Path.Combine(_dir, "Other", "Game")), info, steam));                   // outside every library
        Assert.Equal(374320u, Vac.AppId(Manual(Path.Combine(lib, "steamapps", "common", "DARK SOULS III")), steam, out var isSteam));
        Assert.True(isSteam);
    }

    /// <summary>A steamapps\common folder is in a Steam library even when libraryfolders.vdf doesn't list it (or can't be
    /// read); a game clearly outside any steamapps folder isn't Steam's when the list can't be read.</summary>
    [Fact]
    public void A_steamapps_path_is_Steam_s_without_the_library_list()
    {
        var steam = Directory.CreateDirectory(Path.Combine(_dir, "Steam2", "steamapps")).Parent!.FullName;
        var lib = Directory.CreateDirectory(Path.Combine(_dir, "Unlisted", "steamapps", "common", "Dying Light")).FullName;
        File.WriteAllText(Path.Combine(_dir, "Unlisted", "steamapps", "appmanifest_239140.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"239140\"\n\t\"installdir\"\t\t\"Dying Light\"\n}\n");
        var info = AppInfo((239140, 2, ["category_8"]));
        Game Gog(string folder) => new("gog:" + folder, folder, Store.Other, folder, Path.Combine(folder, "game.exe"));
        Assert.True(Vac.Is(Gog(lib), info, steam));
        var vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        File.WriteAllText(vdf, "\"libraryfolders\" {}");
        using (File.Open(vdf, FileMode.Open, FileAccess.Read, FileShare.None))   // locked: membership unknown
        {
            Assert.False(Vac.Is(Gog(Path.Combine(_dir, "GOG Games", "Witcher")), info, steam));
            Assert.True(Vac.Is(Gog(lib), info, steam));
        }
    }

    /// <summary>A Steam app whose launch options start an anti-cheat launcher in any branch (WARDOGS: its default starts its
    /// own launcher, older branches start_protected_game.exe) is anti-cheat with a clean install, found by its id or its
    /// library folder. The anti-cheat an offline session ignores is ignored here too. Steam's data missing or unreadable
    /// leaves the folder's verdict as it is (VAC decides the recorder then, Vac.Is).</summary>
    [Fact]
    public void A_Steam_launch_option_that_starts_an_anti_cheat_marks_a_clean_install()
    {
        var steam = Directory.CreateDirectory(Path.Combine(_dir, "Steam3", "appcache")).Parent!.FullName;
        var common = Directory.CreateDirectory(Path.Combine(steam, "steamapps", "common")).FullName;
        File.WriteAllText(Path.Combine(steam, "steamapps", "appmanifest_1867240.acf"), "\"AppState\"\n{\n\t\"appid\"\t\t\"1867240\"\n\t\"installdir\"\t\t\"Wardogs\"\n}\n");
        WriteAppInfo(Path.Combine(steam, "appcache", "appinfo.vdf"),
            (1867240, 2, ["category_1"], ["WardogsLauncher-Shipping.exe", "Wardogs/Binaries/Win64/WardogsClient.exe", "start_protected_game.exe"]),
            (578080, 2, ["category_1"], [@"TslGame\Binaries\Win64\TslGame_BE.exe"]),
            (374320, 2, ["category_1"], [@"Game\DarkSoulsIII.exe"]),
            (500000, 2, ["category_1"], null));
        var install = Directory.CreateDirectory(Path.Combine(common, "Wardogs")).FullName;
        File.WriteAllBytes(Path.Combine(install, "WardogsLauncher-Shipping.exe"), [0]);
        Game At(string id, Store store = Store.Steam) => new(id, id, store, install, Path.Combine(install, "WardogsLauncher-Shipping.exe"));
        AntiCheat Detect(Game g, AntiCheat ignore = AntiCheat.None, string? root = null) => GameFiles.DetectAntiCheat(g, ignore: ignore, steamRoot: root ?? steam);
        Assert.Equal(AntiCheat.Other, Detect(At("steam:1867240")));
        Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(At("steam:1867240"), quick: true, steamRoot: steam));
        Assert.Equal(AntiCheat.Other, Detect(At("manual:wardogs", Store.Manual)));   // by its folder
        Assert.Equal(AntiCheat.None, Detect(At("steam:1867240"), ignore: AntiCheat.EasyAntiCheat));
        Assert.Equal(AntiCheat.Other, Detect(At("steam:578080")));
        Assert.Equal(AntiCheat.None, Detect(At("steam:374320")));
        Assert.Equal(AntiCheat.None, Detect(At("steam:500000")));
        Assert.Equal(AntiCheat.None, Detect(At("steam:999")));
        var none = Path.Combine(_dir, "no-steam");
        Assert.Equal(AntiCheat.None, Detect(At("steam:1867240"), root: none));
        Assert.True(Vac.Is(At("steam:1867240"), steamRoot: none));
        var broken = Directory.CreateDirectory(Path.Combine(_dir, "Steam4", "appcache")).Parent!.FullName;
        File.WriteAllBytes(Path.Combine(broken, "appcache", "appinfo.vdf"), [1, 2, 3]);   // a format Steam may change to
        Assert.Equal(AntiCheat.None, Detect(At("steam:1867240"), root: broken));   // the folder's verdict, unchanged
    }

    /// <summary>Under Wine (Proton) Steam's data is the one of the Steam that started the game (STEAM_COMPAT_CLIENT_INSTALL_PATH),
    /// even when a Steam in the prefix has its own copy, which can be stale. Its file missing: VAC, never the prefix's copy.
    /// The variable empty: the registry's Steam's (Proton's stand-in has none: VAC). A variable with no Windows path: VAC.
    /// Never the game's library, whose appinfo.vdf can be an abandoned Steam's. Outside Wine nothing changes.</summary>
    [Fact]
    public void Under_Wine_Steam_s_data_is_the_Linux_Steam_s()
    {
        static string? Same(string s) => s;
        var prefix = Directory.CreateDirectory(Path.Combine(_dir, "PrefixSteam")).FullName;
        var linux = Directory.CreateDirectory(Path.Combine(_dir, "LinuxSteam", "appcache")).Parent!.FullName;
        var info = Path.Combine(linux, "appcache", "appinfo.vdf");
        WriteAppInfo(info, (374320, 2, ["category_1"]));
        var own = Path.Combine(prefix, "appcache", "appinfo.vdf");
        var noSteam = Path.Combine(_dir, "NoSteam");
        Assert.Equal(info, SteamSource.AppInfoPath(prefix, true, linux, Same));
        Assert.Equal(info, SteamSource.AppInfoPath(prefix, true, linux + @"\", Same));
        Assert.Equal(own, SteamSource.AppInfoPath(prefix, true, null, Same));
        Assert.Equal(own, SteamSource.AppInfoPath(prefix, true, "", Same));
        Assert.Equal(Path.Combine(noSteam, "appcache", "appinfo.vdf"), SteamSource.AppInfoPath(prefix, true, noSteam, Same));
        Assert.Equal("", SteamSource.AppInfoPath(prefix, true, linux, _ => null));
        Assert.Equal(own, SteamSource.AppInfoPath(prefix, false, linux, Same));
        Assert.True(Vac.Is(Steam(374320), own, prefix));                           // the stand-in has no copy
        Assert.True(Vac.Is(Steam(374320), "", prefix));
        Assert.False(Vac.Is(Steam(374320), SteamSource.AppInfoPath(prefix, true, linux, Same), prefix));
        // the game in the Linux Steam's own folder, the variable empty: its library's non-VAC appinfo.vdf isn't trusted
        var install = Directory.CreateDirectory(Path.Combine(linux, "steamapps", "common", "Game")).FullName;
        var inLibrary = new Game("steam:374320", "Game", Store.Steam, install, Path.Combine(install, "game.exe"));
        Assert.True(Vac.Is(inLibrary, SteamSource.AppInfoPath(prefix, true, "", Same), prefix));
        // a Steam in the prefix with a stale copy that says no VAC, Steam that started the game says VAC: VAC
        Directory.CreateDirectory(Path.Combine(prefix, "appcache"));
        WriteAppInfo(own, (374320, 2, ["category_1"]));
        WriteAppInfo(info, (374320, 2, ["category_1", "category_8"]));
        Assert.Equal(info, SteamSource.AppInfoPath(prefix, true, linux, Same));
        Assert.True(Vac.Is(Steam(374320), SteamSource.AppInfoPath(prefix, true, linux, Same), prefix));
        Assert.True(Vac.Is(Steam(374320), SteamSource.AppInfoPath(prefix, true, noSteam, Same), prefix));   // its file missing
        Assert.False(Vac.Is(Steam(374320), SteamSource.AppInfoPath(prefix, true, "", Same), prefix));        // no variable: the prefix's
    }

    /// <summary>An appinfo.vdf that can't be read blocks, and is read again once readable (after the retry interval or a change).</summary>
    [Fact]
    public void A_failed_read_is_tried_again()
    {
        var path = Path.Combine(_dir, "appinfo.vdf");
        File.WriteAllBytes(path, [1, 2, 3]);
        var root = Path.Combine(_dir, "no-steam");
        Assert.True(Vac.Is(Steam(374320), path, root));
        WriteAppInfo(path, (374320, 2, ["category_1"]));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.False(Vac.Is(Steam(374320), path, root));
    }

    /// <summary>Why appinfo.vdf can't be read names the file and the reason, for the scan's one log line.</summary>
    [Fact]
    public void A_read_error_names_the_file_and_why()
    {
        var steam = Directory.CreateDirectory(Path.Combine(_dir, "Steam5", "appcache")).Parent!.FullName;
        var path = Path.Combine(steam, "appcache", "appinfo.vdf");
        Assert.Equal($"{path}: missing", Vac.ReadError(steam));
        File.WriteAllBytes(path, [1, 2, 3, 4, 5, 6, 7, 8]);
        Assert.Equal($"{path}: unknown format 0x04030201", Vac.ReadError(steam));
        WriteAppInfo(path, (374320, 2, ["category_1"]));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        Assert.Null(Vac.ReadError(steam));
    }
}
