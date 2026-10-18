using System.Text;
using SCSKiller.Core;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

public class AmdAgsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-ags-test-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly EngineInfo Ue56 = new("Unreal", "5.6", null, "D3D12", false, null);

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    Game Layout(string install, string exe, params string[] dirs)
    {
        var root = Path.Combine(_dir, install);
        foreach (var d in dirs) Directory.CreateDirectory(Path.Combine(root, d));
        var path = Path.Combine(root, exe);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x4D, 0x5A]);
        return new Game("test:ags", install, Store.Steam, root, path);
    }

    [Theory]
    [InlineData("Townfall", @"Townfall\Binaries\Win64\Townfall-Win64-Shipping.exe", "Townfall")]   // the Steam layout
    [InlineData("Hogwarts Legacy", @"Phoenix\Binaries\Win64\HogwartsLegacy.exe", "Phoenix")]      // exe named apart from its project
    [InlineData("Game", @"MyProj\Binaries\WinGDK\MyProj-WinGDK-Shipping.exe", "MyProj")]
    public void The_project_name_is_the_folder_holding_Binaries(string install, string exe, string expected) =>
        Assert.Equal(expected, AmdAgs.ProjectName(Layout(install, exe, @"Engine\Binaries\Win64")));

    [Fact]
    public void Without_a_Binaries_layout_the_only_project_with_paks_names_it()
    {
        Assert.Equal("Proj", AmdAgs.ProjectName(Layout("a", "Game.exe", @"Proj\Content\Paks", @"Engine\Content\Paks", "Other")));
        Assert.Null(AmdAgs.ProjectName(Layout("b", "Game.exe", @"One\Content\Paks", @"Two\Content\Paks")));   // which one is unknown
        Assert.Null(AmdAgs.ProjectName(Layout("c", "Game.exe", "Proj")));
    }

    [Fact]
    public void An_Unreal_game_that_uses_AGS_registers_its_project_and_engine()
    {
        var g = Layout("Townfall", @"Townfall\Binaries\Win64\Townfall-Win64-Shipping.exe");
        Assert.False(AmdAgs.UsesAgs(g.ExePath));   // neither the DLL next to it nor the export
        Assert.Null(AmdAgs.Of(g, Ue56));
        File.WriteAllBytes(Path.Combine(Path.GetDirectoryName(g.ExePath)!, AmdAgs.DllName), []);
        Assert.Equal(new AgsRegistration("Townfall", "UnrealEngine5.6"), AmdAgs.Of(g, Ue56));
        Assert.Equal("UnrealEngine4.27", AmdAgs.Of(g, Ue56 with { Version = "4.27" })!.Engine);
        Assert.Equal("UnrealEngine4.25", AmdAgs.Of(g, Ue56 with { Version = "4.25" })!.Engine);
        Assert.Null(AmdAgs.Of(g, Ue56 with { Version = "4.24" }));   // 4.20-4.24 link AGS but create the D3D12 device without it
        Assert.Null(AmdAgs.Of(g, Ue56 with { Version = "4.20" }));
        Assert.Null(AmdAgs.Of(g, Ue56 with { Version = "GAME_Oak" }));
        Assert.Null(AmdAgs.Of(g, null));
        Assert.Null(AmdAgs.Of(g, Ue56 with { Family = "Carved" }));
    }

    [Fact]
    public void An_exe_that_cannot_be_read_may_use_AGS()
    {
        var g = Layout("9554CD53", @"NWD\Binaries\WinGDK\NWD-WinGDK-Shipping.exe");
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ReadData,
            System.Security.AccessControl.AccessControlType.Deny);
        var exe = new FileInfo(g.ExePath);
        var acl = exe.GetAccessControl();
        acl.AddAccessRule(deny);
        exe.SetAccessControl(acl);
        try
        {
            Assert.Null(AmdAgs.UsesAgs(g.ExePath));
            Assert.Equal(new AgsRegistration("NWD", "UnrealEngine5.1", ExeUnread: true), AmdAgs.Of(g, Ue56 with { Version = "5.1" }));
            Assert.Null(AmdAgs.Of(g, Ue56 with { Version = "4.24" }));
        }
        finally
        {
            acl.RemoveAccessRule(deny);
            exe.SetAccessControl(acl);
        }
    }

    [Fact]
    public void AGS_linked_into_the_exe_shows_in_its_exports()
    {
        var dll = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release", AmdAgs.DllName);   // exports the AGS API; built with the proxy
        if (!File.Exists(dll)) return;
        var g = Layout("Linked", @"Proj\Binaries\Win64\Proj-Win64-Shipping.exe");
        File.Copy(dll, g.ExePath, true);
        Assert.True(AmdAgs.UsesAgs(g.ExePath));
        Assert.False(AmdAgs.UsesAgs(Environment.ProcessPath!));
        Assert.False(AmdAgs.UsesAgs(Path.Combine(_dir, "missing.exe")));
    }

    [Fact]
    public void The_games_own_AGS_6_DLL_is_used_else_the_bundled_one()
    {
        var g = Layout("Own", @"Proj\Binaries\Win64\Proj-Win64-Shipping.exe");
        Assert.Equal(@"C:\app\native\amd_ags_x64.dll", AmdAgs.DllFor(g, @"C:\app\native\amd_ags_x64.dll"));
        var own = Path.Combine(Path.GetDirectoryName(g.ExePath)!, AmdAgs.DllName);
        File.WriteAllBytes(own, []);   // no version resource: not AGS 6
        Assert.Equal(@"C:\app\native\amd_ags_x64.dll", AmdAgs.DllFor(g, @"C:\app\native\amd_ags_x64.dll"));
        var dll = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release", AmdAgs.DllName);
        if (!File.Exists(dll)) return;
        File.Copy(dll, own, true);
        Assert.Equal(own, AmdAgs.DllFor(g, @"C:\app\native\amd_ags_x64.dll"));
    }

    static readonly EngineInfo Red = new("REDengine 3", "DX12", null, "D3D12", false, null);
    static byte[] Ascii(string s) => Encoding.ASCII.GetBytes(s + "\0");
    static byte[] Wide(string s) => Encoding.Unicode.GetBytes(s + "\0");
    static readonly byte[] Imports = Ascii("agsDriverExtensionsDX12_CreateDevice");

    Game NonUnreal(params byte[][] payload)
    {
        var g = Layout("W3-" + Guid.NewGuid().ToString("N")[..8], @"bin\x64_dx12\witcher3.exe");
        File.WriteAllBytes(g.ExePath, Planning.MiddlewarePackTests.Pe(null, payload));
        return g;
    }

    [Fact]
    public void A_non_Unreal_game_registers_the_exe_string_that_hashes_to_the_key_its_process_held()
    {
        var g = NonUnreal(Imports, Wide("Witcher 3"), Wide("RedEngine"));
        string[] held = [AmdAppCache.AppNameKey("Witcher 3")];
        Assert.Null(AmdAgs.Of(g, Red));
        Assert.Equal(new AgsRegistration("Witcher 3", "REDengine 3"), AmdAgs.Recovered(g, Red, "witcher3.exe", held));
        Assert.Null(AmdAgs.Recovered(g, Red, "witcher3.exe", []));                                          // not seen yet
        Assert.Null(AmdAgs.Recovered(g, Red, "witcher3.exe", [.. held, AmdAppCache.DxcKey("witcher3.exe")]));   // it held its exe name's key
        Assert.Null(AmdAgs.Recovered(g, Red, "witcher3.exe", [AmdAppCache.AppNameKey("Other")]));            // no string gives it: a profile
        Assert.Null(AmdAgs.Recovered(g, Ue56, "witcher3.exe", held));                                        // Unreal: Of only
        Assert.Null(AmdAgs.Recovered(g, null, "witcher3.exe", held));
    }

    [Fact]
    public void Without_the_AGS_device_create_or_with_two_matches_nothing_is_registered()
    {
        string[] held = [AmdAppCache.AppNameKey("Witcher 3")];
        Assert.Null(AmdAgs.Recovered(NonUnreal(Ascii("agsInitialize"), Wide("Witcher 3")), Red, "witcher3.exe", held));   // agsInitialize names no app
        Assert.Null(AmdAgs.Recovered(NonUnreal(Imports, Wide("Witcher 3"), Wide("Other")), Red, "witcher3.exe", [.. held, AmdAppCache.AppNameKey("Other")]));
        Assert.Null(AmdAgs.Recovered(NonUnreal(Imports, Wide("witcher3.exe")), Red, "Witcher3.exe", [AmdAppCache.DxcKey("witcher3.exe")]));   // its exe name in another case
        Assert.Null(AmdAgs.Recovered(NonUnreal(Imports, Wide(new string('a', 65))), Red, "witcher3.exe", [AmdAppCache.AppNameKey(new string('a', 65))]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Wide_strings_at_either_parity_and_ASCII_ones_hash_as_UTF_16(int shift)
    {
        HashSet<uint> Of(params string[] s) => s.Select(AmdAppCache.Fnv1a).ToHashSet();
        byte[] bytes = [.. new byte[shift], .. Wide("GoWR"), .. new byte[3], .. Wide("SMS2023"), .. Ascii("Cyberpunk 2077"), .. Imports, .. "GoWR"u8];
        Assert.Equal(["GoWR", "SMS2023"], AmdAgs.StringsHashing(new MemoryStream(bytes), Of("GoWR", "SMS2023")));
        Assert.Equal(["Cyberpunk 2077"], AmdAgs.StringsHashing(new MemoryStream(bytes), Of("Cyberpunk 2077")));
        Assert.Equal(2, AmdAgs.StringsHashing(new MemoryStream(bytes), Of("GoWR", "SMS2023", "Cyberpunk 2077")).Count);   // two are enough to rule it out
        Assert.Empty(AmdAgs.StringsHashing(new MemoryStream(bytes[..^(Imports.Length + 4)]), Of("GoWR")));   // no AGS device create
    }

    [Fact]
    public void Strings_split_across_reads_are_found()
    {
        var hashes = new[] { AmdAppCache.Fnv1a("GoWR") }.ToHashSet();
        byte[] bytes = [.. new byte[(1 << 20) - 3], .. Wide("GoWR"), .. Imports];
        Assert.Equal(["GoWR"], AmdAgs.StringsHashing(new MemoryStream(bytes), hashes));
    }

    /// <summary>The installed games, read-only. The names are the wide strings their AGS device create passes as the app.</summary>
    [Theory]
    [InlineData(@"The Witcher 3\bin\x64_dx12", "witcher3.exe", "Witcher 3")]
    [InlineData("God of War Ragnarok", "GoWR.exe", "GoWR")]
    [InlineData(@"ELDEN RING NIGHTREIGN\Game", "nightreign.exe", null)]   // agsInitialize only: no app registered
    public void Installed_games_recover_their_AGS_app_name(string dir, string exe, string? app)
    {
        if (TestEnv.GameRoots.Select(r => Path.Combine(r, dir, exe)).FirstOrDefault(File.Exists) is not { } path) return;
        using var s = File.OpenRead(path);
        string[] expected = app == null ? [] : [app];
        Assert.Equal(expected, AmdAgs.StringsHashing(s, new HashSet<uint> { AmdAppCache.Fnv1a(app ?? "NIGHTREIGN") }));
    }

    /// <summary>The installed game, read-only.</summary>
    [Fact]
    public void Townfall_registers_Townfall()
    {
        var install = TestEnv.GameDir("Townfall");
        var exe = Path.Combine(install, "Townfall", "Binaries", "Win64", "Townfall-Win64-Shipping.exe");
        if (!File.Exists(exe)) return;
        var g = new Game("steam:1636440", "SILENT HILL: Townfall", Store.Steam, install, exe);
        Assert.Equal("Townfall", AmdAgs.ProjectName(g));
        Assert.True(AmdAgs.UsesAgs(exe));   // AGS is linked statically: no DLL in the install
        Assert.Equal("dxc:dc72f790", AmdAppCache.AgsKey(Path.GetFileName(exe), AmdAgs.Of(g, Ue56)!.App));
    }
    /// <summary>The recorder's #ags line: the names exactly as AGS got them, of the game exe's last launch that made one.</summary>
    [Fact]
    public void The_recorders_AGS_line_gives_the_app_and_engine_names_of_the_games_last_launch_that_registered_them()
    {
        Directory.CreateDirectory(_dir);
        var csv = Path.Combine(_dir, "scskiller_creates.csv");
        File.WriteAllText(csv, "#session,1000,game.exe\n#clock,1.0\n#ags,1,ffffad00,ffffad00,Old,E\n1.0,G,0,0,5.0\n#end,2000\n"
            + "#session,3000,game.exe\n#ags,?,10203,40506,Kamui%0025 2%2160,Kamui%002CEngine\n1.0,G,0,0,5.0\n#end,4000\n"
            + "#session,5000,game.exe\n1.0,G,0,0,5.0\n#end,6000\n"   // no AGS device in the latest launch: the last registration stands
            + "#session,7000,launcher.exe\n#ags,1,0,0,Launcher,E\n#end,8000\n");   // another exe of the folder
        Assert.Equal(new AgsRegistration("Kamui% 2Ⅰ", "Kamui,Engine"), Core.App.SessionLog.LastAgs(csv, "Game.exe"));
        Assert.Equal(new AgsRegistration("Launcher", "E"), Core.App.SessionLog.LastAgs(csv, null));
        File.WriteAllText(csv, "#session,1000,game.exe\n#ags,1,0,0,,E\n#ags,1,0,0,Bad%00G,E\n#ags,1,0,0,Short,E,extra\n#end,2000\n");
        Assert.Null(Core.App.SessionLog.LastAgs(csv, "game.exe"));   // empty, a bad escape, a field too many
    }

    [Theory]
    [InlineData("Kamui",   "dxc:fbc062ce", true)]    // FNV-1a "Kamui": the key the game held
    [InlineData("Kamui",   "dxc:12345678", false)]   // another key: an app profile or no app registration
    [InlineData("OakGame", "dxc:f2f80824", true)]    // an app-name profile's key
    [InlineData("",        "dxc:811c9dc5", false)]
    public void A_registered_app_name_is_proof_only_when_its_key_is_one_the_game_held(string app, string held, bool used)
    {
        Assert.Equal("dxc:fbc062ce", AmdAppCache.AppNameKey("Kamui"));
        Assert.Equal(used ? new AgsRegistration(app, "Kamui Engine") : null, AmdAgs.Learned(app, "Kamui Engine", "game.exe", [held, "dx:0000000000000001"]));
        Assert.Null(AmdAgs.Learned(null, null, "game.exe", [held]));
    }
}
