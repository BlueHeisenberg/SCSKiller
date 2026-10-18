using SCSKiller.Core;
using SCSKiller.Core.Games;

namespace SCSKiller.Tests.Platform;

// Builds the commands only: nothing here starts a process.
public sealed class StoreLaunchTests : IDisposable
{
    readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "scskiller-launch-" + Guid.NewGuid().ToString("N")[..8])).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    static Game G(string id, Store store, string dir = @"X:\Games\Some Game") => new(id, "Some Game", store, dir, Path.Combine(dir, "game.exe"));

    string Manifests()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "Manifests")).FullName;
        File.WriteAllText(Path.Combine(dir, "A.item"), """{ "AppName": "Other", "CatalogNamespace": "x", "CatalogItemId": "y" }""");
        File.WriteAllText(Path.Combine(dir, "B.item"), "{ half written");
        File.WriteAllText(Path.Combine(dir, "C.item"), """
            { "DisplayName": "Detroit Become Human", "AppName": "Columbine", "CatalogNamespace": "columbine",
              "CatalogItemId": "6d2dcb9ed0bd42dea4b2f23cc43ba92f", "MainGameAppName": "" }
            """);
        return dir;
    }

    [Fact]
    public void Steam_opens_its_run_uri()
    {
        var c = StoreLaunch.Command(G("steam:2909400", Store.Steam))!;
        Assert.Equal(("steam://rungameid/2909400", true), (c.FileName, c.UseShellExecute));
        Assert.True(StoreLaunch.Supported(G("steam:2909400", Store.Steam)));
    }

    [Fact]
    public void Epic_opens_the_launcher_uri_with_the_manifests_catalog_ids()
    {
        var manifests = Manifests();
        var c = StoreLaunch.Command(G("epic:Columbine", Store.Epic), epicManifests: manifests)!;
        Assert.Equal("com.epicgames.launcher://apps/columbine%3A6d2dcb9ed0bd42dea4b2f23cc43ba92f%3AColumbine?action=launch&silent=true", c.FileName);
        Assert.True(c.UseShellExecute);
        Assert.Null(StoreLaunch.Command(G("epic:Gone", Store.Epic), epicManifests: manifests));   // no manifest names it
        Assert.Null(StoreLaunch.Command(G("epic:Columbine", Store.Epic), epicManifests: Path.Combine(_dir, "missing")));
    }

    [Fact]
    public void Epic_takes_the_catalog_ids_of_a_game_only_the_new_launcher_lists()
    {
        var launcher = Directory.CreateDirectory(Path.Combine(_dir, "eglv2")).FullName;
        File.WriteAllText(Path.Combine(launcher, "installations.jsonl"), """
            {"version":1,"writtenAt":"2026-10-14T12:00:00Z","entries":1}
            {"installationId":"A","complete":true,"namespace":"columbine","itemId":"6d2dcb9ed0bd42dea4b2f23cc43ba92f","artifactId":"Columbine","dir":"X:\\Games\\Some Game"}
            """);
        var c = StoreLaunch.Command(G("epic:Columbine", Store.Epic), epicManifests: Path.Combine(_dir, "missing"), epicLauncher: launcher)!;
        Assert.Equal("com.epicgames.launcher://apps/columbine%3A6d2dcb9ed0bd42dea4b2f23cc43ba92f%3AColumbine?action=launch&silent=true", c.FileName);
    }

    [Fact]
    public void Xbox_opens_the_package_app_through_the_shell()
    {
        File.WriteAllText(Path.Combine(_dir, "appxmanifest.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications><Application Id="AppUEGameShipping" Executable="GameLaunchHelper.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>
            </Package>
            """);
        var c = StoreLaunch.Command(G("xbox:Publisher.Game_3275kfvn8vcwc", Store.Xbox, _dir))!;
        Assert.Equal((@"shell:AppsFolder\Publisher.Game_3275kfvn8vcwc!AppUEGameShipping", true), (c.FileName, c.UseShellExecute));
        Assert.Null(StoreLaunch.Command(G("xbox:Publisher.Game_3275kfvn8vcwc", Store.Xbox, Path.Combine(_dir, "missing"))));   // no manifest
    }

    [Fact]
    public void Ubisoft_opens_its_launch_uri()
    {
        Assert.Equal("uplay://launch/635/0", StoreLaunch.Command(G("ubisoft:635", Store.Other))!.FileName);
        Assert.True(StoreLaunch.Supported(G("ubisoft:635", Store.Other)));
    }

    [Fact]
    public void Gog_runs_galaxys_own_shortcut_command()
    {
        var c = StoreLaunch.Command(G("gog:1207664643", Store.Other, @"C:\GOG Games\The Witcher 3\"), galaxyExe: @"C:\GOG Galaxy\GalaxyClient.exe")!;
        Assert.Equal(@"C:\GOG Galaxy\GalaxyClient.exe", c.FileName);
        Assert.Equal(@"/command=runGame /gameId=1207664643 /path=""C:\GOG Games\The Witcher 3""", c.Arguments);   // no \" at the end
        Assert.False(c.UseShellExecute);
    }

    [Fact]
    public void Purple_runs_its_own_desktop_shortcut_command()
    {
        const string launcher = @"C:\Program Files (x86)\NC\Purple\PurpleLauncher.exe";
        var c = StoreLaunch.Command(G("purple:A2_WW_L_GA_PURPLE", Store.Other), purpleLauncher: launcher)!;
        Assert.Equal((launcher, "--game-id A2_WW_L_GA_PURPLE", @"C:\Program Files (x86)\NC\Purple"), (c.FileName, c.Arguments, c.WorkingDirectory));
        Assert.False(c.UseShellExecute);
        Assert.Null(StoreLaunch.Command(G("purple:A2 --uninstall", Store.Other), purpleLauncher: launcher));   // never an argument of its own
    }

    [Fact]
    public void Purple_handler_command_names_purple_launcher_only()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_dir, "NC Purple")).FullName;
        var launcher = Path.Combine(dir, "PurpleLauncher.exe");
        var other = Path.Combine(dir, "notepad.exe");
        File.WriteAllBytes(launcher, [0]);
        File.WriteAllBytes(other, [0]);
        Assert.Equal(launcher, PurpleSource.LauncherIn($"\"{launcher}\" \"%1\""));
        Assert.Equal(launcher, PurpleSource.LauncherIn($"{launcher} \"%1\""));   // unquoted, with a space in the path, as PURPLE registers it
        Assert.Null(PurpleSource.LauncherIn($"\"{other}\" \"%1\""));
        Assert.Null(PurpleSource.LauncherIn($"{other} \"%1\""));
        Assert.Null(PurpleSource.LauncherIn($"\"{Path.Combine(dir, "gone", "PurpleLauncher.exe")}\" \"%1\""));
        Assert.Null(PurpleSource.LauncherIn($"\"{launcher}.evil.exe\" \"%1\""));
    }

    [Fact]
    public void A_game_the_user_added_runs_its_exe_from_its_folder()
    {
        var g = G("manual:0123456789abcdef", Store.Manual, @"X:\Games\Some Game");
        Assert.True(StoreLaunch.Supported(g));
        var c = StoreLaunch.Command(g)!;
        Assert.Equal((@"X:\Games\Some Game\game.exe", @"X:\Games\Some Game", ""), (c.FileName, c.WorkingDirectory, c.Arguments));
    }

    [Fact]
    public void A_game_the_user_added_with_anti_cheat_is_started_from_its_own_launcher()
    {
        static GameState S(Game g, AntiCheat ac) => new(g, null, ac, GameStatus.Ready, "", null, null, null, null, null, null, null, false, null);
        Assert.True(StoreLaunch.OwnLauncherOnly(S(G("manual:0123456789abcdef", Store.Manual), AntiCheat.BattlEye)));
        Assert.False(StoreLaunch.OwnLauncherOnly(S(G("manual:0123456789abcdef", Store.Manual), AntiCheat.None)));
        Assert.False(StoreLaunch.OwnLauncherOnly(S(G("steam:2909400", Store.Steam), AntiCheat.BattlEye)));   // its store's launch starts the launcher
    }

    [Fact]
    public void Ea_battlenet_and_unknown_stores_have_no_launch()
    {
        foreach (var g in new[] { G("ea:1234567", Store.EA), G("battlenet:prometheus", Store.Other), G("manual:x", Store.Other) })
        {
            Assert.False(StoreLaunch.Supported(g));
            Assert.Null(StoreLaunch.Command(g, galaxyExe: @"C:\GOG Galaxy\GalaxyClient.exe"));
        }
    }
}
