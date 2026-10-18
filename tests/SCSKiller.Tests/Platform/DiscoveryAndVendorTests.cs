using System.Runtime.InteropServices;
using SCSKiller.Core;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Platform;

// These read the real machine (Steam libraries, NVIDIA driver). Read-only: nothing here writes a driver setting.
public class DiscoveryAndVendorTests(ITestOutputHelper output)
{
    [Trait("Needs", "Game")]
    [Fact]
    public void Steam_finds_ff7_rebirth_with_the_real_game_exe()
    {
        var games = new SteamSource().Discover();
        foreach (var g in games) output.WriteLine($"{g.Id,-16} {g.Name,-45} {GameFiles.DetectAntiCheat(g),-14} {g.ExePath}");
        var ff7 = Assert.Single(games, g => g.Id == "steam:2909400");
        Assert.Equal("FINAL FANTASY VII REBIRTH", ff7.Name);
        Assert.EndsWith(@"\End\Binaries\Win64\ff7rebirth_.exe", ff7.ExePath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(ff7));
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Steam_leaves_out_tools_and_applications_by_their_app_type()
    {
        if (Microsoft.Win32.Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) is not string root) return;
        var types = SteamSource.AppTypes(Path.Combine(root, "appcache", "appinfo.vdf"), new HashSet<uint> { 250820, 993090, 2909400, 3564860 });
        Assert.NotNull(types);
        Assert.Equal(("Tool", "Application", "Game", "Demo"), (types[250820], types[993090], types[2909400], types[3564860]));
        var ids = new SteamSource().Discover().Select(g => g.Id).ToList();
        Assert.DoesNotContain("steam:250820", ids);   // SteamVR
        Assert.DoesNotContain("steam:993090", ids);   // Lossless Scaling
        Assert.Contains("steam:2909400", ids);
        var libraries = SteamSource.Values(File.ReadAllText(Path.Combine(root, "steamapps", "libraryfolders.vdf")), "path");
        if (libraries.Any(l => File.Exists(Path.Combine(l, "steamapps", "appmanifest_3564860.acf"))))   // installed on this machine
            Assert.Contains("steam:3564860", ids);    // a demo is played like a game
    }

    [Fact]
    public void Steam_keeps_everything_when_appinfo_is_missing_or_unreadable()
    {
        var root = Path.Combine(Path.GetTempPath(), "scskiller-steam-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var apps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
            foreach (var (id, dir) in new[] { (7u, "SomeTool"), (8u, "SomeGame") })
            {
                Directory.CreateDirectory(Path.Combine(apps, "common", dir));
                File.WriteAllBytes(Path.Combine(apps, "common", dir, dir + ".exe"), new byte[1024]);
                File.WriteAllText(Path.Combine(apps, $"appmanifest_{id}.acf"),
                    $"\"AppState\"\n{{\n\t\"appid\"\t\t\"{id}\"\n\t\"name\"\t\t\"{dir}\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"{dir}\"\n}}\n");
            }
            var appinfo = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "appcache")).FullName, "appinfo.vdf");
            string[] Found() => new SteamSource(root).Discover().Select(g => g.Id).Order().ToArray();

            Assert.Equal(["steam:7", "steam:8"], Found());   // no appinfo.vdf
            File.WriteAllBytes(appinfo, [1, 2, 3, 4, 5, 6, 7, 8, 9]);
            Assert.Equal(["steam:7", "steam:8"], Found());   // not understood
            File.WriteAllBytes(appinfo, AppInfoV28((7, "Tool"), (8, "Game")));
            Assert.Equal(["steam:8"], Found());
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A game listed before keeps its exe while its build is the same, without its folders being looked through
    /// again; another build looks again.</summary>
    [Fact]
    public void Steam_reuses_a_games_exe_while_its_build_is_the_same()
    {
        var root = Path.Combine(Path.GetTempPath(), "scskiller-steam-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var apps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
            var install = Directory.CreateDirectory(Path.Combine(apps, "common", "SomeGame")).FullName;
            File.WriteAllBytes(Path.Combine(install, "SomeGame.exe"), new byte[1024]);
            void Manifest(string build) => File.WriteAllText(Path.Combine(apps, "appmanifest_9.acf"),
                $"\"AppState\"\n{{\n\t\"appid\"\t\t\"9\"\n\t\"name\"\t\t\"SomeGame\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"SomeGame\"\n\t\"buildid\"\t\t\"{build}\"\n}}\n");
            Manifest("100");
            var source = new SteamSource(root);
            var first = Assert.Single(source.Discover());
            Assert.EndsWith("SomeGame.exe", first.ExePath);

            File.WriteAllBytes(Path.Combine(install, "Bigger.exe"), new byte[4096]);   // what a look through the folders takes now
            source.Known = new Dictionary<string, Game> { [first.Id] = first };
            Assert.Equal(first.ExePath, Assert.Single(source.Discover()).ExePath);
            Manifest("101");
            Assert.EndsWith("Bigger.exe", Assert.Single(source.Discover()).ExePath);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>EA SPORTS WRC from the EA app: a larger WRC_Trial.exe beside WRC.exe. A trial alone is still the game.</summary>
    [Fact]
    public void FindExe_passes_over_a_trial_build_beside_the_game_only()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-trial-test-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "WRC_Trial.exe"), new byte[8192]);
            Assert.Equal("WRC_Trial.exe", Path.GetFileName(GameFiles.FindExe(root)));
            File.WriteAllBytes(Path.Combine(root, "WRC.exe"), new byte[4096]);
            Assert.Equal("WRC.exe", Path.GetFileName(GameFiles.FindExe(root)));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Once Human's folder: its ffmpeg.exe is larger than the game's exe.</summary>
    [Fact]
    public void Steam_takes_the_games_exe_over_a_larger_ffmpeg_beside_it_also_over_a_listed_one()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-ffmpeg-test-").FullName;
        try
        {
            var apps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
            var install = Directory.CreateDirectory(Path.Combine(apps, "common", "Once Human")).FullName;
            File.WriteAllBytes(Path.Combine(install, "ONCE_HUMAN.exe"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(install, "ffmpeg.exe"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(install, "SecureEngineSDK64.dll"), new byte[2048]);
            File.WriteAllText(Path.Combine(apps, "appmanifest_2139460.acf"),
                "\"AppState\"\n{\n\t\"appid\"\t\t\"2139460\"\n\t\"name\"\t\t\"Once Human\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"Once Human\"\n\t\"buildid\"\t\t\"100\"\n}\n");
            var source = new SteamSource(root);
            var game = Assert.Single(source.Discover());
            Assert.Equal(Path.Combine(install, "ONCE_HUMAN.exe"), game.ExePath);

            source.Known = new Dictionary<string, Game> { [game.Id] = game with { ExePath = Path.Combine(install, "ffmpeg.exe") } };   // an earlier scan's pick, same build
            Assert.Equal(Path.Combine(install, "ONCE_HUMAN.exe"), Assert.Single(source.Discover()).ExePath);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>The Last of Us Part II Remastered's folder: Sony's crash reporter beside the game's exe.</summary>
    [Fact]
    public void Steam_takes_the_games_exe_over_a_larger_crash_reporter_beside_it_also_over_a_listed_one()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-crs-test-").FullName;
        try
        {
            var apps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
            var install = Directory.CreateDirectory(Path.Combine(apps, "common", "The Last of Us Part II")).FullName;
            File.WriteAllBytes(Path.Combine(install, "tlou-ii.exe"), new byte[1024]);
            foreach (var crs in new[] { "crs-handler.exe", "crs-uploader.exe", "crs-video.exe" }) File.WriteAllBytes(Path.Combine(install, crs), new byte[4096]);
            File.WriteAllText(Path.Combine(apps, "appmanifest_2531310.acf"),
                "\"AppState\"\n{\n\t\"appid\"\t\t\"2531310\"\n\t\"name\"\t\t\"The Last of Us Part II Remastered\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"The Last of Us Part II\"\n\t\"buildid\"\t\t\"100\"\n}\n");
            var source = new SteamSource(root);
            var game = Assert.Single(source.Discover());
            Assert.Equal(Path.Combine(install, "tlou-ii.exe"), game.ExePath);

            source.Known = new Dictionary<string, Game> { [game.Id] = game with { ExePath = Path.Combine(install, "crs-handler.exe") } };   // an earlier scan's pick, same build
            Assert.Equal(Path.Combine(install, "tlou-ii.exe"), Assert.Single(source.Discover()).ExePath);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Xbox_takes_the_games_exe_over_a_larger_ffmpeg_beside_it_also_over_a_listed_one()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-xbox-ffmpeg-test-").FullName;
        try
        {
            var content = Directory.CreateDirectory(Path.Combine(root, "XboxGames", "Once Human", "Content")).FullName;
            File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"), """<Game><Identity Name="Pub.OnceHuman" Version="1.0.0.0"/></Game>""");   // no ExecutableList: the fallback scan
            File.WriteAllBytes(Path.Combine(content, "ONCE_HUMAN.exe"), new byte[1024]);
            File.WriteAllBytes(Path.Combine(content, "ffmpeg.exe"), new byte[4096]);
            var source = new XboxSource([root]);
            var game = Assert.Single(source.Discover());
            Assert.Equal(Path.Combine(content, "ONCE_HUMAN.exe"), game.ExePath);

            source.Known = new Dictionary<string, Game> { [game.Id] = game with { ExePath = Path.Combine(content, "ffmpeg.exe") } };   // an earlier scan's pick, same version
            Assert.Equal(Path.Combine(content, "ONCE_HUMAN.exe"), Assert.Single(source.Discover()).ExePath);
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>appinfo.vdf v28: header, then per app id + size + 60 bytes of state/hashes + KeyValues, then app id 0.</summary>
    static byte[] AppInfoV28(params (uint Id, string Type)[] apps)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write(0x07564428u);
        w.Write(1u);
        foreach (var (id, type) in apps)
        {
            var kv = new MemoryStream();
            var k = new BinaryWriter(kv);
            void S(string s) { k.Write(System.Text.Encoding.UTF8.GetBytes(s)); k.Write((byte)0); }
            k.Write(new byte[60]);
            k.Write((byte)0); S("appinfo");
            k.Write((byte)2); S("appid"); k.Write((int)id);
            k.Write((byte)0); S("common");
            k.Write((byte)1); S("name"); S("x");
            k.Write((byte)1); S("type"); S(type);
            k.Write((byte)8); k.Write((byte)8); k.Write((byte)8);
            w.Write(id);
            w.Write((uint)kv.Length);
            w.Write(kv.ToArray());
        }
        w.Write(0u);
        return ms.ToArray();
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Epic_discovery_reads_manifests()
    {
        foreach (var g in new EpicSource().Discover()) output.WriteLine($"{g.Id} {g.Name} {g.ExePath}");
    }

    [Fact]
    public void Vdf_values_are_unescaped()
    {
        var vdf = "\"libraryfolders\"\n{\n\t\"0\"\n\t{\n\t\t\"path\"\t\t\"C:\\\\Program Files (x86)\\\\Steam\"\n\t\t\"name\"\t\"a \\\"quoted\\\" b\"\n\t}\n}";
        Assert.Equal(@"C:\Program Files (x86)\Steam", SteamSource.Values(vdf, "path").Single());
        Assert.Equal("a \"quoted\" b", SteamSource.Values(vdf, "name").Single());
    }

    [Fact]
    public void Experimental_intel_backend_has_the_measured_caps_and_plans_from_a_recording()
    {
        var gpu = new GpuInfo(GpuVendor.Intel, "Intel(R) Arc(TM) Graphics", "32.0.101.6979", 1, 12UL << 30);
        Assert.IsType<UnsupportedVendor>(GpuBackends.For(gpu));   // Settings.ExperimentalIntel off
        var intel = Assert.IsType<IntelBackend>(GpuBackends.For(gpu, intel: true));
        Assert.Equal(("intel-1", true, false, true, RtCacheGranularity.WholeObject, (long?)(512L << 20)),
            (intel.Caps.Profile, intel.Caps.CacheKeyedByExeName, intel.Caps.StateIndependentCache, intel.Caps.PerStageCache, intel.Caps.RtCacheGranularity,
             intel.Caps.PerGameCacheCap));
        Assert.Null(((IGpuVendorBackend)intel).AppCache);
        Assert.Null(intel.GetCacheLimit());
        Assert.Throws<NotSupportedException>(() => intel.SetCacheLimit(new(1L << 30, false)));
        Assert.True(intel.Refresh(intel.Gpu with { DriverVersion = "32.0.101.7026" }));
        Assert.Equal("32.0.101.7026", intel.Gpu.DriverVersion);

        // shaders with embedded root signatures plan without a recording on NVIDIA's state-independent cache, never on Intel's
        var game = new Game("test:intel", "intel", Store.Other, @"C:\nowhere", @"C:\nowhere\game.exe");
        var engine = new EngineInfo(Core.Carved.CarvedReader.Family, "DXBC" + Core.Carved.CarvedReader.EmbeddedRootSignatures, null, "D3D12", false, null);
        var planner = new Core.Planning.Planner();
        Assert.Equal(Readiness.Ready, planner.Check(game, engine, null, new VendorCaps("nvidia-1", true, true, true)).Readiness);
        Assert.Equal(Readiness.NeedsRecording, planner.Check(game, engine, null, intel.Caps).Readiness);
        Assert.Equal(Readiness.Unsupported, planner.Check(game, engine, null, new UnsupportedVendor(intel.Gpu).Caps).Readiness);
    }

    [Trait("Needs", "Gpu")]
    [Fact]
    public void Nvidia_backend_reads_driver_usage_and_cache_limit()
    {
        var v = GpuBackends.Detect();
        output.WriteLine($"{v.Gpu.Name} vendor={v.Vendor} driver={v.Gpu.DriverVersion} luid={v.Gpu.AdapterLuid:X16} vram={v.Gpu.DedicatedVideoMemory >> 20} MiB caps={v.Caps}");
        if (v.Vendor != GpuVendor.Nvidia) return;   // not an NVIDIA machine
        var nv = Assert.IsType<NvidiaBackend>(v);
        Assert.Matches(@"^\d{3}\.\d{2}$", nv.Gpu.DriverVersion);
        Assert.True(nv.Gpu.DedicatedVideoMemory > 1UL << 30);
        Assert.Equal(new VendorCaps("nvidia-1", true, true, true, PerStageCache: true, RtCacheGranularity: RtCacheGranularity.Collection, PackageKeyed: true), nv.Caps);
        var dxgi = GpuBackends.PrimaryAdapter()!;
        Assert.Equal(NvidiaBackend.ReadDriverVersion(), nv.Gpu.DriverVersion);   // earlier builds stored NvAPI's: the same string
        Assert.Equal(nv.Gpu.DriverVersion, NvidiaBackend.FromUserModeVersion(dxgi.DriverVersion));
        var before = nv.Gpu;
        Assert.True(nv.Refresh(dxgi));
        Assert.Equal(before, nv.Gpu);
        // this process's NvAPI keeps answering the running driver: two updates in a row follow DXGI's version
        Assert.True(nv.Refresh(dxgi with { DriverVersion = "32.0.16.1800", AdapterLuid = 1 }));
        Assert.Equal(("618.00", 1L), (nv.Gpu.DriverVersion, nv.Gpu.AdapterLuid));
        Assert.True(nv.Refresh(dxgi with { DriverVersion = "32.0.16.1905" }));
        Assert.Equal("619.05", nv.Gpu.DriverVersion);
        Assert.False(nv.Refresh(dxgi with { DriverVersion = "" }));   // no DXGI version: the last one stays, not NvAPI's older one
        Assert.Equal("619.05", nv.Gpu.DriverVersion);
        Assert.True(nv.Refresh(dxgi));
        Assert.Equal(before, nv.Gpu);
        Assert.Equal(before.DriverVersion, nv.FallbackVersion(dxgi.DriverVersion));

        var usage = nv.GetCacheUsage();
        output.WriteLine($"usage: {usage.Path} {usage.BytesOnDisk / 1048576.0:0} MiB (upper bound {usage.UpperBound})");
        Assert.True(usage.BytesOnDisk > 0);

        var (id, raw, def) = nv.ReadCacheSetting();
        var limit = nv.GetCacheLimit();
        output.WriteLine($"setting 0x{id:X8} raw={(raw is { } r ? $"0x{r:X8}" : "not set")} driver default=0x{def:X8} -> limit {limit}");
        Assert.NotNull(limit);
        Assert.Equal(raw == null, limit.IsDriverDefault);
    }

    [Trait("Needs", "Gpu")]
    [Fact]
    public void Amd_backend_reads_adrenalin_version_and_cache_usage()
    {
        var v = GpuBackends.Detect();
        if (v.Vendor != GpuVendor.Amd) return;   // not an AMD machine
        output.WriteLine($"{v.Gpu.Name} vendor={v.Vendor} driver={v.Gpu.DriverVersion} luid={v.Gpu.AdapterLuid:X16} caps={v.Caps}");
        var amd = Assert.IsType<AmdBackend>(v);
        Assert.Equal(new VendorCaps("amd-1", CacheKeyedByExeName: true, StateIndependentCache: false, CacheSizeConfigurable: false,
            PerStageCache: true), amd.Caps);
        // Adrenalin + driver store: "26.8.1 (32.0.31041.1004)", so a hotfix with the same Adrenalin number is a new driver
        Assert.Matches(@"^\d{2}\.\d{1,2}\.\d{1,2} \(\d+\.\d+\.\d+\.\d+\)$", amd.Gpu.DriverVersion);

        var usage = amd.GetCacheUsage();
        long d3d12 = AmdBackend.Bytes(AmdBackend.CacheDir), d3d11 = AmdBackend.Bytes(AmdBackend.D3D11CacheDir);
        output.WriteLine($"usage: {usage.Path} {usage.BytesOnDisk / 1048576.0:0} MiB = D3D12 {d3d12 / 1048576.0:0} + D3D11 {d3d11 / 1048576.0:0} MiB (upper bound {usage.UpperBound})");
        Assert.Equal(AmdBackend.CacheDir, usage.Path);
        Assert.True(usage.UpperBound);
        Assert.True(usage.BytesOnDisk > 0);
        Assert.InRange(usage.BytesOnDisk, Math.Max(d3d12, d3d11), d3d12 + d3d11 + (64L << 20));   // both folders (they may grow meanwhile)
        Assert.Equal(new CacheLimit(16L << 30, false), amd.GetCacheLimit());
        Assert.Throws<NotSupportedException>(() => amd.SetCacheLimit(new CacheLimit(null, true)));
    }

    [Fact]
    public void Amd_the_version_stays_the_registrys_whatever_DXGIs_format_and_waits_for_the_registry_after_an_update()
    {
        (string?, string?) registry = ("26.8.1", "32.0.31041.1004");
        var dxgi = new GpuInfo(GpuVendor.Amd, "AMD Radeon RX 9070 XT", "31.0.24033.1003", 7, 16UL << 30);   // not the store's format
        var amd = new AmdBackend(dxgi, _ => registry);
        Assert.Equal(AmdBackend.FormatVersion("26.8.1", "32.0.31041.1004"), amd.Gpu.DriverVersion);   // what earlier builds stored
        Assert.True(amd.Refresh(dxgi));
        Assert.Equal("26.8.1 (32.0.31041.1004)", amd.Gpu.DriverVersion);

        dxgi = dxgi with { DriverVersion = "31.0.24033.2001", AdapterLuid = 8 };   // installed; the registry not yet
        Assert.False(amd.Refresh(dxgi));
        Assert.False(amd.Refresh(dxgi));
        Assert.Equal(("26.8.1 (32.0.31041.1004)", 8L), (amd.Gpu.DriverVersion, amd.Gpu.AdapterLuid));
        registry = ("26.9.1", "32.0.31051.1001");
        Assert.True(amd.Refresh(dxgi));
        Assert.Equal("26.9.1 (32.0.31051.1001)", amd.Gpu.DriverVersion);
        Assert.True(amd.Refresh(dxgi));

        registry = ("26.9.1", null);   // a read during an install: the last complete one stays until the registry is back
        Assert.False(amd.Refresh(dxgi));
        Assert.Equal("26.9.1 (32.0.31051.1001)", amd.Gpu.DriverVersion);
        registry = (null, null);
        Assert.False(amd.Refresh(dxgi));
        Assert.Equal("26.9.1 (32.0.31051.1001)", amd.Gpu.DriverVersion);
        registry = ("26.9.1", "32.0.31051.1001");
        Assert.True(amd.Refresh(dxgi));
        Assert.Equal("26.9.1 (32.0.31051.1001)", amd.Gpu.DriverVersion);
    }

    [Fact]
    public void Amd_without_a_store_version_in_the_registry_the_version_takes_DXGIs_as_before()
    {
        (string?, string?) registry = ("26.9.1", null);
        var dxgi = new GpuInfo(GpuVendor.Amd, "AMD Radeon RX 9070 XT", "31.0.24033.2001", 7, 16UL << 30);
        var amd = new AmdBackend(dxgi, _ => registry);
        Assert.Equal("26.9.1 (31.0.24033.2001)", amd.Gpu.DriverVersion);
        Assert.True(amd.Refresh(dxgi with { DriverVersion = "31.0.24033.3001" }));
        Assert.Equal("26.9.1 (31.0.24033.3001)", amd.Gpu.DriverVersion);

        registry = (null, null);   // nothing in the registry yet, from the start: DXGI's version, read again at every check
        amd = new AmdBackend(dxgi, _ => registry);
        Assert.Equal("31.0.24033.2001", amd.Gpu.DriverVersion);
        Assert.False(amd.Refresh(dxgi));
        registry = ("26.9.1", "32.0.31051.1001");
        Assert.True(amd.Refresh(dxgi));
        Assert.Equal("26.9.1 (32.0.31051.1001)", amd.Gpu.DriverVersion);
    }

    [Fact]
    public void Amd_driver_version_is_adrenalin_plus_driver_store()
    {
        Assert.Equal("26.8.1 (32.0.31041.1004)", AmdBackend.FormatVersion("26.8.1", "32.0.31041.1004"));
        Assert.Equal("26.8.1", AmdBackend.FormatVersion("26.8.1", null));
        Assert.Equal("32.0.31041.1004", AmdBackend.FormatVersion(null, "32.0.31041.1004"));
        Assert.Equal("", AmdBackend.FormatVersion("", ""));
        // a hotfix keeps the Adrenalin number but not the store version: a different driver for staleness
        Assert.NotEqual(AmdBackend.FormatVersion("26.8.1", "32.0.31041.1004"), AmdBackend.FormatVersion("26.8.1", "32.0.31041.2001"));
    }

    [Fact]
    public void Nvidia_driver_version_from_the_DXGI_user_mode_version()
    {
        Assert.Equal("617.14", NvidiaBackend.FromUserModeVersion("32.0.16.1714"));
        Assert.Equal("552.22", NvidiaBackend.FromUserModeVersion("31.0.15.5222"));
        Assert.Equal("610.08", NvidiaBackend.FromUserModeVersion("32.0.16.1008"));
        Assert.Equal("560.09", NvidiaBackend.FromUserModeVersion("32.0.15.6009"));
        Assert.Null(NvidiaBackend.FromUserModeVersion(""));
        Assert.Null(NvidiaBackend.FromUserModeVersion("26.8.1"));
    }

    [Fact]
    public void Under_wine_the_driver_version_is_the_host_drivers_from_Vulkan()
    {
        Assert.Equal("615.71.09", Wine.FormatDriverVersion(0x10de, 615u << 22 | 71u << 14 | 9u << 6));   // NVIDIA's own packing
        Assert.Equal("580.105.08", Wine.FormatDriverVersion(0x10de, 580u << 22 | 105u << 14 | 8u << 6));
        Assert.Equal("26.2.2", Wine.FormatDriverVersion(0x1002, 26u << 22 | 2u << 12 | 2u));   // Mesa: VK_MAKE_API_VERSION
        Assert.Equal("615.71.09", NvidiaBackend.DriverVersionOf("615.71.09", wine: true));
        // DXVK's own user-mode version when Vulkan had no match: read as before
        Assert.Equal("999.99", NvidiaBackend.DriverVersionOf("35.0.19.9999", wine: true));
        Assert.Equal("617.14", NvidiaBackend.DriverVersionOf("32.0.16.1714", wine: false));
        Assert.Null(NvidiaBackend.DriverVersionOf("615.71.09", wine: false));
    }

    [Fact]
    public void Under_wine_the_game_reads_every_file_of_its_warms_app_name_from_the_folder_they_share()
    {
        var (warm, game) = Wine.CacheEnvironment("/home/u/prefix/drive_c/SCSKiller/data/games/manual_1/proton-cache", "My Game-Win64-Shipping.exe");
        Assert.Contains(("__GL_SHADER_DISK_CACHE_APP_NAME", "scskiller_My_Game-Win64-Shipping"), warm);
        Assert.Contains(("__GL_SHADER_DISK_CACHE_APP_NAME", "My_Game-Win64-Shipping"), game);
        Assert.Equal(Enumerable.Range(0, Wine.WarmFiles).Select(i => $"scskiller_My_Game-Win64-Shipping{i}"),
            game.Single(e => e.Name == "__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME").Value.Split(';'));
        Assert.Contains(("__GL_SHADER_DISK_CACHE_PATH", "/home/u/prefix/drive_c/SCSKiller/data/games/manual_1/proton-cache/nvidia"), warm);
        Assert.Contains(("VKD3D_SHADER_CACHE_PATH", "/home/u/prefix/drive_c/SCSKiller/data/games/manual_1/proton-cache/vkd3d"), warm);
        foreach (var name in new[] { "__GL_SHADER_DISK_CACHE_PATH", "__GL_SHADER_DISK_CACHE_SIZE", "VKD3D_SHADER_CACHE_PATH" })
            Assert.Equal(warm.Single(e => e.Name == name), game.Single(e => e.Name == name));
    }

    [Fact]
    public void Outside_wine_no_host_driver_version_or_Linux_path_is_read()
    {
        if (Wine.Running) return;
        Assert.Empty(Wine.HostDriverVersions());
        Assert.Null(Wine.UnixPath(@"C:\Windows"));
    }

    [Fact]
    public void Amd_an_AGS_app_name_keys_the_cache_by_its_FNV_1a()
    {
        Assert.Equal("dxc:dc72f790", AmdAppCache.AppNameKey("Townfall"));   // SILENT HILL: Townfall's own process, handle-verified
        Assert.NotEqual(AmdAppCache.AppNameKey("Townfall"), AmdAppCache.AppNameKey("townfall"));
        Assert.Equal("dxc:12bba8ae", AmdAppCache.DxcKey("Townfall-Win64-Shipping.exe"));   // its exe name's, where a plain device lands
        Assert.Equal(AmdAppCache.DxcAppHash("selftest.exe"), AmdAppCache.Fnv1a("selftest.exe"));
        // handle-verified: an app-name profile wins, then an exe-name profile, then the app name's hash
        Assert.Equal("dxc:dc72f790", AmdAppCache.AgsKey("Townfall-Win64-Shipping.exe", "Townfall"));
        Assert.Equal("dxc:d32786a7", AmdAppCache.AgsKey("HogwartsLegacy.exe", "Phoenix"));
        Assert.Equal("dxc:f2f80824", AmdAppCache.AgsKey("Wonderlands.exe", "OakGame"));
        Assert.Equal("dxc:f2f80824", AmdAppCache.AgsKey("agsprobe.exe", "OakGame"));
        Assert.Equal("dxc:38810766", AmdAppCache.AgsKey("SWGR-Win64-Shipping.exe", "Griffin"));
        Assert.Equal("dxc:85c2b2e5", AmdAppCache.AgsKey("Wonderlands.exe", "scskAgsA"));
        Assert.Equal("dxc:6b2fcd83", AmdAppCache.AgsKey("ff7rebirth_.exe", "scskAgsA"));
        Assert.Equal("dxc:9832c88a", AmdAppCache.AgsKey("agsprobe.exe", "End"));
        Assert.Equal("dxc:15d4f9d9", AmdAppCache.AgsKey("agsprobe.exe", "scskAgsA"));
        Assert.True(AmdAppCache.ProvenAgsApp("Townfall"));
        Assert.False(AmdAppCache.ProvenAgsApp("townfall"));
        Assert.False(AmdAppCache.ProvenAgsApp("OakGame"));
    }

    [Fact]
    public void Amd_cache_file_names_map_to_app_keys()
    {
        // FNV-1a-32 of the UTF-16LE exe name, case-sensitive: keys observed in DxcCache on AMD
        Assert.Equal(0x207c35a9u, AmdAppCache.DxcAppHash("selftest.exe"));
        Assert.Equal(0x3c98ec7du, AmdAppCache.DxcAppHash("chrome.exe"));
        Assert.Equal(0x7d588db3u, AmdAppCache.DxcAppHash("steamwebhelper.exe"));
        Assert.Equal(0xab5f8ae4u, AmdAppCache.DxcAppHash(@"C:\x\amdtestA.exe"));   // the path doesn't matter
        Assert.Equal(0xcff591a4u, AmdAppCache.DxcAppHash("AMDTESTA.EXE"));          // the case does
        Assert.Equal("dxc:207c35a9", AmdAppCache.DxcKey("selftest.exe"));

        // Driver app profiles override the name hash (measured): the hash is only a hint
        Assert.Equal(0xf63a0641u, AmdAppCache.DxcAppHash("ff7rebirth_.exe"));        // what the name would give...
        Assert.Equal("dxc:6b2fcd83", AmdAppCache.HintKey("ff7rebirth_.exe"));        // ...and the profile's fixed key
        Assert.Equal("dxc:6b2fcd83", AmdAppCache.HintKey(@"D:\x\FF7REBIRTH_.EXE"));  // any case, any "ff7rebirth*" name
        Assert.Equal("dxc:6b2fcd83", AmdAppCache.ProfileKey("ff7rebirth_demo.exe"));
        Assert.Equal("dxc:81de6978", AmdAppCache.HintKey("Cyberpunk2077.exe"));
        Assert.Equal("dxc:b73b6179", AmdAppCache.HintKey("eldenring.exe"));
        Assert.Null(AmdAppCache.ProfileKey("ff7remake_.exe"));                       // name-hashed
        Assert.Null(AmdAppCache.ProfileKey("bg3.exe"));
        Assert.Equal(AmdAppCache.DxcKey("bg3.exe"), AmdAppCache.HintKey("bg3.exe"));
        Assert.Equal(true, AmdAppCache.IsNameHashed(["dxc:207c35a9", "dx:0d67622373f61b94"], "selftest.exe"));
        Assert.Equal(false, AmdAppCache.IsNameHashed(["dxc:6b2fcd83"], "ff7rebirth_.exe"));
        Assert.Null(AmdAppCache.IsNameHashed(["dx:0d67622373f61b94", "11111111"], "selftest.exe"));   // no D3D12 key learned

        Assert.Equal("dxc:207c35a9", AmdAppCache.Key("207c35a9.dfac411e.71efbc0e.2b1a674a.0.parc", d3d12: true));
        Assert.Equal("dxc:031116bd", AmdAppCache.Key("31116bd.caaa38cb.a4a986a3.cef507b2.1.parc", d3d12: true));   // no leading zeros on disk
        Assert.Equal("dx:0d67622373f61b94", AmdAppCache.Key("d67622373f61b94.f2d9161043adafe8.7b1f799a.dad1858.0.parc", d3d12: false));
        Assert.Null(AmdAppCache.Key("1b60938d3b6cb660.f2d9161043adafe8.7b1f799a.dad1858.0.parc", d3d12: true));   // 64-bit: not a DxcCache key
        Assert.Null(AmdAppCache.Key("207c35a9.dfac411e.71efbc0e.2b1a674a.parc", d3d12: true));
        Assert.Null(AmdAppCache.Key("0002a91d69d04596.nvph", d3d12: true));

        var root = Path.Combine(Path.GetTempPath(), "scsk-amd-" + Guid.NewGuid().ToString("N"));
        var (dxc, dx) = (Path.Combine(root, "DxcCache"), Path.Combine(root, "DxCache"));
        Directory.CreateDirectory(dxc);
        Directory.CreateDirectory(dx);
        try
        {
            foreach (var n in new[] { "ab5f8ae4.dfac411e.71efbc0e.2b1a674a.0.parc", "ab5f8ae4.dfac411e.a4a986a3.2b1a674a.0.parc",
                                      "ab5f8ae4.dfac411e.a4a986a3.2b1a674a.1.parc", "6528721f.dfac411e.71efbc0e.2b1a674a.0.parc" })
                File.WriteAllBytes(Path.Combine(dxc, n), new byte[4096]);
            File.WriteAllBytes(Path.Combine(dx, "1b60938d3b6cb660.f2d9161043adafe8.7b1f799a.dad1858.0.parc"), new byte[16384]);
            var cache = new AmdAppCache(dxc, dx);
            Assert.Equal(3, cache.FilesOf([AmdAppCache.HintKey("amdtestA.exe")]).Count);   // both kinds, both slots
            Assert.Empty(cache.FilesOf([AmdAppCache.HintKey("AMDTESTA.EXE")]));
            string[] keys = ["dxc:ab5f8ae4", "dx:1b60938d3b6cb660"];
            Assert.Equal(3 * 4096 + 16384, cache.SizeOf(keys));
            Assert.Empty(cache.KeysOpenBy("scsk-no-such-process.exe"));

            using (File.Open(Path.Combine(dxc, "ab5f8ae4.dfac411e.a4a986a3.2b1a674a.1.parc"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var e = Assert.Throws<InvalidOperationException>(() => cache.Delete(keys));   // one file in use: nothing is deleted
                output.WriteLine(e.Message);
                Assert.Equal(4, cache.FilesOf(keys).Count);
            }
            Assert.Equal(4, cache.Delete(keys));
            Assert.Equal(["6528721f.dfac411e.71efbc0e.2b1a674a.0.parc"], Directory.GetFiles(dxc).Select(Path.GetFileName));
            Assert.Empty(Directory.GetFiles(dx));
        }
        finally { Directory.Delete(root, true); }
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Xbox_finds_the_gdk_titles_with_the_real_game_exe_not_launchers()
    {
        var games = new XboxSource().Discover();
        foreach (var g in games) output.WriteLine($"{g.Id,-55} {g.Name,-40} {g.Version,-16} {GameFiles.DetectAntiCheat(g),-14} {g.ExePath}");
        if (games.Count == 0) return;   // this machine's Xbox app has no games (or C:/D:\XboxGames don't exist)

        // Atomfall's config declares Launcher/Atomfall.exe as Id="Game", an API-picking launcher stub (the "launch" helper
        // hint rejects it); the real D3D12 exe is Atomfall_dx12.exe. Each title is checked only
        // where it is installed.
        if (games.SingleOrDefault(g => g.Name == "Atomfall") is { } atomfall)
        {
            Assert.EndsWith(@"\bin\Atomfall_dx12.exe", atomfall.ExePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("xbox:Rebellion.Windscale_2vbwqmt31j4mr", atomfall.Id);
        }

        // Darktide's config lists only launcher\launcher.exe -> the fallback scan must skip it for the real binary
        if (games.SingleOrDefault(g => g.Name.Contains("Darktide")) is { } darktide)
            Assert.EndsWith(@"\binaries\Darktide.exe", darktide.ExePath, StringComparison.OrdinalIgnoreCase);

        // Halo MCC lists mcclauncher.exe (a launcher by name) and the direct Shipping exe -> pick the latter
        if (games.SingleOrDefault(g => g.Name.Contains("Master Chief Collection")) is { } mcc)
            Assert.EndsWith("MCCWinStore-Win64-Shipping.exe", mcc.ExePath, StringComparison.OrdinalIgnoreCase);

        // configs that name Unreal's bootstrap stub in Content: discovery takes the Shipping exe it starts (GameExe)
        foreach (var (title, shipping) in new[] { ("Hellblade 2", @"\Hellblade2\Binaries\WinGDK\Hellblade2-WinGDK-Shipping.exe"),
                     ("Avowed", @"\Alabama\Binaries\WinGDK\Avowed-WinGDK-Shipping.exe"), ("Dead Island 2", @"\DeadIsland\Binaries\WinGDK\DeadIsland-WinGDK-Shipping.exe") })
            if (games.SingleOrDefault(g => g.Name.Contains(title)) is { } stubbed)
                Assert.EndsWith(shipping, GameFiles.GameExe(stubbed.InstallDir, stubbed.ExePath), StringComparison.OrdinalIgnoreCase);

        if (games.SingleOrDefault(g => g.Name == "Starfield") is { } starfield)
        {
            Assert.EndsWith(@"\Starfield.exe", starfield.ExePath, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("1.14.74.0", starfield.Version);
        }
    }

    /// <summary>A PE that imports <paramref name="dll"/> (one import descriptor at RVA 0x1100, its name at 0x1180), padded by
    /// <paramref name="size"/> bytes.</summary>
    internal static byte[] Exe(string? dll, int size = 0)
    {
        var pe = Planning.MiddlewarePackTests.Pe(null, new byte[size]);
        if (dll == null) return pe;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x58 + 120), 0x1100);   // import directory
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x58 + 124), 40);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(0x300 + 12), 0x1180);
        System.Text.Encoding.ASCII.GetBytes(dll).CopyTo(pe, 0x380);
        return pe;
    }

    /// <summary>An exe importing sl.interposer.dll and <c>agsDriverExtensionsDX12_CreateDevice</c> from amd_ags_x64.dll by name
    /// (Cyberpunk 2077's).</summary>
    internal static byte[] ExeWithAgs()
    {
        var pe = Planning.MiddlewarePackTests.Pe(null);
        void U32(int at, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(at), v);
        U32(0x58 + 120, 0x1100);                                    // import directory: two descriptors and the terminator
        U32(0x58 + 124, 60);
        U32(0x300 + 12, 0x1140);                                    // 1st: sl.interposer.dll, no functions
        U32(0x300 + 20, 0x1180);                                    // 2nd: amd_ags_x64.dll, its thunks
        U32(0x300 + 20 + 12, 0x1160);
        U32(0x300 + 20 + 16, 0x1180);
        U32(0x380, 0x1190);                                         // one thunk: the hint/name entry's RVA
        System.Text.Encoding.ASCII.GetBytes("sl.interposer.dll").CopyTo(pe, 0x340);
        System.Text.Encoding.ASCII.GetBytes("amd_ags_x64.dll").CopyTo(pe, 0x360);
        System.Text.Encoding.ASCII.GetBytes("agsDriverExtensionsDX12_CreateDevice").CopyTo(pe, 0x392);   // past the 2-byte hint
        return pe;
    }

    /// <summary>launcher-configuration.json as CD PROJEKT RED's launcher ships it with The Witcher 3 (Steam).</summary>
    internal static string RedConfig(string fallback = "DirectX 12", params (string Description, string Dir)[] entries) =>
        $$"""
        { "revision": 3, "executables": [{{string.Join(",", (entries.Length > 0 ? entries : [("DirectX 12", @"bin\\x64_dx12")]).Select(e =>
            $$"""{ "description": "{{e.Description}}", "executable": { "directoryPath": "{{e.Dir}}", "fileName": "witcher3.exe" } }"""))}}],
          "gameId": "witcher3", "platform": "steam", "fallback": "{{fallback}}", "editions": [{ "name": "remasteredEdition" }] }
        """;

    /// <summary>Cyberpunk 2077's launcher-configuration.json names the game in one executablePath (GOG's copy:
    /// "bin\x64\Cyberpunk2077.exe"); Steam's launch target is REDprelauncher.exe. A larger exe whose imports can't be read
    /// (a mod's tool) keeps the import check on the launcher, so only the config names the game.</summary>
    [Fact]
    public void Steam_finds_cyberpunk_2077_by_its_launcher_configuration_executable_path()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-steam-test-").FullName;
        try
        {
            var apps = Directory.CreateDirectory(Path.Combine(root, "steamapps")).FullName;
            var install = Path.Combine(apps, "common", "Cyberpunk 2077");
            void Put(string rel, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(install, rel))!); File.WriteAllBytes(Path.Combine(install, rel), bytes); }
            Put("REDprelauncher.exe", Exe("Qt5Core.dll", 1600));
            Put(@"bin\x64\Cyberpunk2077.exe", Exe("sl.interposer.dll", 6000));
            Put(@"engine\tools\modtool.exe", new byte[3000]);
            Put(@"..\Other\Cyberpunk2077.exe", Exe("d3d12.dll", 100));
            File.WriteAllText(Path.Combine(apps, "appmanifest_1091500.acf"),
                "\"AppState\"\n{\n\t\"appid\"\t\t\"1091500\"\n\t\"name\"\t\t\"Cyberpunk 2077\"\n\t\"StateFlags\"\t\t\"4\"\n\t\"installdir\"\t\t\"Cyberpunk 2077\"\n}\n");
            File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(root, "appcache")).FullName, "appinfo.vdf"), AppInfoV28((1091500, "Game")));
            var config = Path.Combine(install, "launcher-configuration.json");
            string Found() => new SteamSource(root).Discover().Single().ExePath;
            static string Config(string path) => $$"""{ "platform": "steam", "executablePath": "{{path}}", "gameId": "cyberpunk2077" }""";
            var launcher = Path.Combine(install, "REDprelauncher.exe");

            Assert.Equal(launcher, Found());   // no config
            foreach (var path in new[] { @"bin\\x64\\Cyberpunk2077.exe", "bin/x64/Cyberpunk2077.exe" })
            {
                File.WriteAllText(config, Config(path));
                Assert.Equal(Path.Combine(install, @"bin\x64\Cyberpunk2077.exe"), Found());
            }
            Put(@"bin\x64\unins000.exe", Exe("kernel32.dll", 100));
            File.WriteAllText(Path.Combine(install, @"bin\x64\notes.txt"), "");
            // not an exe, missing, outside the install, rooted, a share, a stream, an uninstaller, a name that isn't a string
            foreach (var text in new[] { Config("bin/x64/Cyberpunk2077"), Config("launcher-configuration.json"), Config("bin/x64/gone.exe"),
                Config("../Other/Cyberpunk2077.exe"), Config(Path.Combine(apps, "common", "Other", "Cyberpunk2077.exe").Replace(@"\", @"\\")),
                Config(@"\\bin\\x64\\Cyberpunk2077.exe"), Config(@"\\\\server\\share\\Cyberpunk2077.exe"), Config("bin/x64/notes.txt:s.exe"),
                Config("bin/x64/unins000.exe"), """{ "executablePath": 7 }""" })
            {
                File.WriteAllText(config, text);
                Assert.Equal(launcher, Found());
            }
            File.WriteAllText(config, Config("bin/x64/Cyberpunk2077.exe") + new string(' ', 64 * 1024));   // over 64 KiB: not read
            Assert.Equal(launcher, Found());
            foreach (var bad in new[] { "bin/x64/gone.exe", @"..\\OTHER~1\\Cyberpunk2077.exe", @"bin/x64/Cyberpunk2077\u0000.exe" })   // a path that doesn't do: the list still counts
            {
                File.WriteAllText(config, $$"""{ "executablePath": "{{bad}}", "executables": [{ "executable": { "directoryPath": "bin\\x64", "fileName": "Cyberpunk2077.exe" } }] }""");
                Assert.Equal(Path.Combine(install, @"bin\x64\Cyberpunk2077.exe"), Found());
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Exe_discovery_finds_cyberpunk_2077_s_own_exe_in_its_install()
    {
        var install = TestEnv.GameDir("Cyberpunk 2077");
        if (!File.Exists(Path.Combine(install, "REDprelauncher.exe"))) return;   // not installed here
        Assert.Equal(Path.Combine(install, @"bin\x64\Cyberpunk2077.exe"), GameFiles.FindExe(install), ignoreCase: true);
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Steam_finds_the_witcher_3_with_the_real_exe_not_the_prelauncher()
    {
        if (new SteamSource().Discover().FirstOrDefault(g => g.Id == "steam:292030") is not { } w3) return;   // not installed here
        Assert.EndsWith(@"\bin\x64_dx12\witcher3.exe", w3.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Exe_discovery_takes_the_game_cdpr_s_launcher_configuration_names()
    {
        var tmp = Directory.CreateTempSubdirectory("scskiller-launcher-test-").FullName;
        var root = Path.Combine(tmp, "The Witcher 3");
        try
        {
            void Put(string rel, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllBytes(Path.Combine(root, rel), bytes); }
            Put(@"..\Other\witcher3.exe", Exe("d3d12.dll", 100));
            Put("REDprelauncher.exe", Exe("Qt5Core.dll", 100));
            Put(@"bin\x64_dx12\witcher3.exe", Exe("sl.interposer.dll", 5000));
            Put(@"bin\x64_dx12\D3D12_0\d3dconfig.exe", Exe(null, 200));
            Put(@"bin\x64_dx12\crashreporter\CrashReporter.exe", Exe("mscoree.dll", 300));
            Put(@"bin\x64\witcher3.exe", Exe("d3d11.dll", 4000));
            var config = Path.Combine(root, "launcher-configuration.json");
            string Dx(string dir) => Path.Combine(root, dir, "witcher3.exe");

            File.WriteAllText(config, RedConfig());
            Assert.Equal(Dx(@"bin\x64_dx12"), GameFiles.FindExe(root));
            Assert.Equal(Dx(@"bin\x64_dx12"), GameFiles.FindExe(root, "REDprelauncher.exe"));   // a store naming the launcher
            File.WriteAllText(config, RedConfig("DirectX 12", ("DirectX 11", @"bin\\x64"), ("DirectX 12", @"bin\\x64_dx12")));
            Assert.Equal(Dx(@"bin\x64_dx12"), GameFiles.FindExe(root));
            File.WriteAllText(config, RedConfig("Vulkan", ("DirectX 11", @"bin\\x64"), ("DirectX 12", @"bin\\x64_dx12")));
            Assert.Equal(Dx(@"bin\x64"), GameFiles.FindExe(root));   // no entry is the fallback: the first

            // a target that's missing or outside the install, or a config that doesn't parse: the launcher stays (two exes import a graphics API)
            // a junction inside the install that leads to another folder: as outside
            using (var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
                       $"/c mklink /J \"{Path.Combine(root, "linked")}\" \"{Path.Combine(tmp, "Other")}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!)
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
            foreach (var bad in new[] { RedConfig("DirectX 12", ("DirectX 12", @"bin\\gone")), RedConfig("DirectX 12", ("DirectX 12", @"..\\Other")),
                         RedConfig("DirectX 12", ("DirectX 12", "linked")), "{ not json" })
            {
                File.WriteAllText(config, bad);
                Assert.Equal(Path.Combine(root, "REDprelauncher.exe"), GameFiles.FindExe(root));
            }
        }
        finally
        {
            if (Directory.Exists(Path.Combine(root, "linked"))) Directory.Delete(Path.Combine(root, "linked"));   // the junction, not its target
            Directory.Delete(tmp, true);
        }
    }

    [Fact]
    public void Exe_discovery_takes_no_patchers_copy_and_the_nearest_of_exes_named_alike()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-staging-test-").FullName;
        try
        {
            void Put(string rel, int size) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllBytes(Path.Combine(root, rel), Exe("d3d12.dll", size)); }
            string At(string rel) => Path.Combine(root, rel);
            // Stellar Blade: the patcher's copy is larger
            Put(@"SB\Binaries\Win64\SB-Win64-Shipping.exe", 4000);
            Put(@"PatchData\SB\Binaries\Win64\SB-Win64-Shipping.exe", 5000);
            Assert.Equal(At(@"SB\Binaries\Win64\SB-Win64-Shipping.exe"), GameFiles.FindExe(root));
            // a deeper copy named alike, in a folder no rule names: the one nearest the root
            Put(@"Copy\SB\Binaries\Win64\SB-Win64-Shipping.exe", 6000);
            Assert.Equal(At(@"SB\Binaries\Win64\SB-Win64-Shipping.exe"), GameFiles.FindExe(root));
            // a larger exe named differently: the Shipping exe still
            Put(@"SB\Binaries\Win64\SB-Win64-Test.exe", 7000);
            Assert.Equal(At(@"SB\Binaries\Win64\SB-Win64-Shipping.exe"), GameFiles.FindExe(root));
        }
        finally { Directory.Delete(root, true); }

        root = Directory.CreateTempSubdirectory("scskiller-staging-test-").FullName;
        try
        {
            void Put(string rel, int size) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllBytes(Path.Combine(root, rel), Exe("d3d12.dll", size)); }
            Put("game.exe", 1000);
            Put(@"__Installer\Touchup.exe", 9000);
            Put(@"PatchData\game.exe", 9000);
            Put(@"backup\big.exe", 9000);
            Assert.Equal(Path.Combine(root, "game.exe"), GameFiles.FindExe(root));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Neverness to Everness: the game three levels down in Client\WindowsNoEditor, a larger launcher at the root.
    /// Other projects that deep (a plugin's Binaries) stay unread.</summary>
    [Fact]
    public void FindExe_finds_the_game_three_levels_down_only_through_a_WindowsNoEditor_folder()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-noeditor-test-").FullName;
        try
        {
            void Put(string rel, int size) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllBytes(Path.Combine(root, rel), Exe("d3d12.dll", size)); }
            Put("NTEGlobalLauncher.exe", 9000);
            Put(@"Client\WindowsNoEditor\HT\Binaries\Win64\HTGame.exe", 4000);
            Put(@"Client\WindowsNoEditor\Engine\Binaries\Win64\CrashReportClient.exe", 5000);
            Assert.Equal(Path.Combine(root, @"Client\WindowsNoEditor\HT\Binaries\Win64\HTGame.exe"), GameFiles.FindExe(root));
        }
        finally { Directory.Delete(root, true); }

        root = Directory.CreateTempSubdirectory("scskiller-noeditor-test-").FullName;
        try
        {
            void Put(string rel, int size) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllBytes(Path.Combine(root, rel), Exe("d3d12.dll", size)); }
            Put(@"Game\Binaries\Win64\Game.exe", 4000);
            Put(@"Game\Plugins\Tool\Binaries\Win64\Tool.exe", 9000);
            Assert.Equal(Path.Combine(root, @"Game\Binaries\Win64\Game.exe"), GameFiles.FindExe(root));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Immortals of Aveum (EA): a stub at x64\Windows, the Unreal build three levels down beside x64\Windows\Engine,
    /// its Agility SDK in D3D12 beside the Shipping exe. Engine's exes, a patcher's copy and a runtime beside the stub stay unpicked.</summary>
    [Fact]
    public void Unreal_project_beside_an_Engine_folder_two_levels_down_wins_over_the_stub()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-deepue-test-").FullName;
        try
        {
            string Put(string rel, int size) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, Exe("d3d12.dll", size)); return p; }
            var stub = Put(@"x64\Windows\ImmortalsOfAveum.exe", 9000);
            Put(@"x64\Windows\Engine\Binaries\Win64\CrashReportClient.exe", 8000);
            Put(@"x64\Windows\Engine\Binaries\Win64\Big-Win64-Shipping.exe", 8000);
            var game = Put(@"x64\Windows\P3\Binaries\Win64\P3-Win64-Shipping.exe", 4000);
            var d3d12 = Path.Combine(root, @"x64\Windows\P3\Binaries\Win64\D3D12");
            Directory.CreateDirectory(d3d12);
            File.WriteAllBytes(Path.Combine(d3d12, "D3D12Core.dll"), new byte[64]);
            Put(@"x64\PatchData\Engine\Binaries\Win64\CrashReportClient.exe", 100);
            Put(@"x64\PatchData\P3\Binaries\Win64\P3-Win64-Shipping.exe", 9500);
            // a runtime beside the stub, loose and in a D3D12 folder, changes neither the pick nor its runtime
            File.WriteAllBytes(Path.Combine(root, @"x64\Windows\D3D12Core.dll"), new byte[64]);
            Directory.CreateDirectory(Path.Combine(root, @"x64\Windows\D3D12"));
            File.WriteAllBytes(Path.Combine(root, @"x64\Windows\D3D12\D3D12Core.dll"), new byte[64]);

            Assert.Equal(game, GameFiles.FindExe(root));
            Assert.Equal(game, GameFiles.FindExe(root, @"x64\Windows\ImmortalsOfAveum.exe"));   // EA's launch exe
            Assert.Equal(game, GameFiles.ShippingExe(root, stub));
            Assert.Equal(game, GameFiles.GameExe(root, stub));
            Assert.Equal(d3d12, SCSKiller.Core.Warming.Warmer.AgilityDir(game));
        }
        finally { Directory.Delete(root, true); }

        // that deep, a server build is never the game, and a second project never makes a nearer one ambiguous
        root = Directory.CreateTempSubdirectory("scskiller-deepue-test-").FullName;
        try
        {
            string Put(string rel, int size) { var p = Path.Combine(root, rel); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllBytes(p, Exe("d3d12.dll", size)); return p; }
            var server = Path.Combine(root, "Server");
            var game = Put(@"Server\ActualGame.exe", 1000);
            Directory.CreateDirectory(Path.Combine(server, @"Extras\Windows\Engine"));
            Put(@"Server\Extras\Windows\Dedicated\Binaries\Win64\DedicatedServer-Win64-Shipping.exe", 9000);
            Assert.Equal(game, GameFiles.FindExe(server, "ActualGame.exe"));

            var collection = Path.Combine(root, "Collection");
            var start = Put(@"Collection\Start.exe", 1000);
            Directory.CreateDirectory(Path.Combine(collection, "Engine"));
            var alpha = Put(@"Collection\Main\Binaries\Win64\Alpha-Win64-Shipping.exe", 4000);
            Directory.CreateDirectory(Path.Combine(collection, @"Bonus\Windows\Engine"));
            Put(@"Collection\Bonus\Windows\Extra\Binaries\Win64\Beta-Win64-Shipping.exe", 9000);
            Assert.Equal(alpha, GameFiles.GameExe(collection, start));
            Assert.Equal(alpha, GameFiles.FindExe(collection));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A Ubisoft+ build beside the game's exe (Valhalla's, at their real sizes): the plain exe, though smaller.</summary>
    [Fact]
    public void Exe_discovery_takes_the_exe_beside_a_ubisoft_plus_build_whatever_the_sizes()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-plus-test-").FullName;
        try
        {
            void Put(string name, long size)
            {
                using var f = File.Create(Path.Combine(root, name));
                f.SetLength(size);
            }
            Put("ACValhalla.exe", 544_294_936);
            Put("ACValhalla_Plus.exe", 601_040_408);
            Assert.Equal(Path.Combine(root, "ACValhalla.exe"), GameFiles.FindExe(root));
            File.Delete(Path.Combine(root, "ACValhalla.exe"));
            Assert.Equal(Path.Combine(root, "ACValhalla_Plus.exe"), GameFiles.FindExe(root));   // no plain build beside it
            File.Delete(Path.Combine(root, "ACValhalla_Plus.exe"));
            Put("game.exe", 100);
            Put("GAME_PLUS.EXE", 200);
            Assert.Equal(Path.Combine(root, "game.exe"), GameFiles.FindExe(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_folder_name_comes_from_a_path_ending_in_either_separator()
    {
        Assert.Equal("Assassin's Creed Valhalla", GameFiles.FolderName("C:/Program Files (x86)/Ubisoft/Ubisoft Game Launcher/games/Assassin's Creed Valhalla/"));
        Assert.Equal("Valhalla", GameFiles.FolderName(@"D:\Games\Valhalla\"));
        Assert.Equal("Valhalla", GameFiles.FolderName(@"D:\Games\Valhalla"));
    }

    [Fact]
    public void Exe_discovery_takes_the_exe_battleye_s_launcher_starts()
    {
        var tmp = Directory.CreateTempSubdirectory("scskiller-battleye-test-").FullName;
        var root = Path.Combine(tmp, "Game");
        try
        {
            void Put(string rel, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllBytes(Path.Combine(root, rel), bytes); }
            void Ini(string rel, string exe) => Put(rel, System.Text.Encoding.ASCII.GetBytes($"[Launcher]\r\nGameID=g\r\n32BitExe=win32\\game.exe\r\n64BitExe={exe}\r\n"));
            string At(string rel) => Path.Combine(root, rel);
            // War Thunder's layout: the launcher is the largest exe at the root, the game a level down
            Put("launcher.exe", Exe(null, 9000));
            Put("game_BE.exe", Exe(null, 100));
            Put(@"win64\game.exe", Exe("d3d12.dll", 5000));
            Put(@"win32\game.exe", Exe("d3d11.dll", 5000));
            Assert.Equal(At("launcher.exe"), GameFiles.FindExe(root));   // no ini: as before

            Ini(@"BattlEye\BELauncher.ini", @"win64\game.exe");
            Assert.Equal(At(@"win64\game.exe"), GameFiles.FindExe(root));
            Assert.Equal(At(@"win64\game.exe"), GameFiles.FindExe(root, "launcher.exe"));   // a store naming the launcher

            // relative to the folder holding BattlEye\ (a *_BE.exe beside the game, a level down)
            Directory.Delete(At("BattlEye"), true);
            Ini(@"win64\BattlEye\BELauncher.ini", "game.exe");
            Assert.Equal(At(@"win64\game.exe"), GameFiles.FindExe(root));

            // a second ini naming the same exe agrees; one naming another makes it ambiguous: as before
            Ini(@"BattlEye\BELauncher.ini", @"win64\game.exe");
            Assert.Equal(At(@"win64\game.exe"), GameFiles.FindExe(root));
            Ini(@"BattlEye\BELauncher.ini", @"win32\game.exe");
            Assert.Equal(At("launcher.exe"), GameFiles.FindExe(root));
            Directory.Delete(At(@"win64\BattlEye"), true);

            // a target that's missing, outside the install, rooted, or an ini outside a BattlEye folder: as before
            Put(@"..\Other\game.exe", Exe("d3d12.dll", 100));
            foreach (var bad in new[] { @"win64\gone.exe", @"..\Other\game.exe", Path.Combine(tmp, "Other", "game.exe"), "" })
            {
                Ini(@"BattlEye\BELauncher.ini", bad);
                Assert.Equal(At("launcher.exe"), GameFiles.FindExe(root));
            }
            Directory.Delete(At("BattlEye"), true);
            Ini(@"Other\BELauncher.ini", @"..\win64\game.exe");
            Assert.Equal(At("launcher.exe"), GameFiles.FindExe(root));

            // Unreal keeps its Binaries\Win64 pick
            Ini(@"BattlEye\BELauncher.ini", @"win64\game.exe");
            Put(@"Game\Binaries\Win64\Game-Win64-Shipping.exe", Exe("d3d12.dll", 200));
            Assert.Equal(At(@"Game\Binaries\Win64\Game-Win64-Shipping.exe"), GameFiles.FindExe(root));
        }
        finally { Directory.Delete(tmp, true); }
    }

    [Fact]
    public void Exe_discovery_takes_the_one_exe_that_imports_a_graphics_api_over_a_launcher_that_doesn_t()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-launcher-test-").FullName;
        try
        {
            void Put(string rel, byte[] bytes) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllBytes(Path.Combine(root, rel), bytes); }
            var launcher = Path.Combine(root, "Launcher.exe");
            var game = Path.Combine(root, "Game", "Bin", "Game.exe");
            Put("Launcher.exe", Exe("user32.dll", 1000));
            Put(@"Game\Bin\Game.exe", Exe("d3d12.dll", 5000));
            Put(@"Game\Bin\Small.exe", Exe("dxgi.dll", 10));             // smaller than the launcher: not the game
            Put(@"Game\Bin\CrashHandler.exe", Exe("d3d11.dll", 9000));   // a crash handler is never the game
            Assert.Equal(game, GameFiles.FindExe(root));

            Put(@"Tools\Editor.exe", Exe("d3d11.dll", 6000));
            Assert.Equal(launcher, GameFiles.FindExe(root));   // two candidates: the guess stays
            File.Delete(Path.Combine(root, "Tools", "Editor.exe"));
            Put(@"Tools\Locked.exe", new byte[3000]);
            Assert.Equal(launcher, GameFiles.FindExe(root));   // a larger exe whose imports can't be read may be the game
            File.Delete(Path.Combine(root, "Tools", "Locked.exe"));

            Put(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", Exe(null, 10));
            Assert.Equal(launcher, GameFiles.FindExe(root));   // anti-cheat in the install: no other binary is read
            Directory.Delete(Path.Combine(root, "EasyAntiCheat"), true);

            Put("Launcher.exe", Exe("dxgi.dll", 1000));
            Assert.Equal(launcher, GameFiles.FindExe(root));   // the guess imports one itself
            Put("Launcher.exe", new byte[1000]);
            Assert.Equal(launcher, GameFiles.FindExe(root));   // not a readable PE: unknown, kept
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Xbox_package_family_name_matches_the_real_appx_identity()
    {
        // Known-good values read from Get-AppxPackage on this machine: PackageFamilyNameFromId must reproduce them
        // from just the manifest's Identity name + publisher, with no package-manager query.
        Assert.Equal("Rebellion.Windscale_2vbwqmt31j4mr", XboxSource.PackageFamilyName("Rebellion.Windscale", "CN=9136491E-6A28-4C39-989B-12D6D89FB1B3"));
        Assert.Equal("Microsoft.SeaofThieves_8wekyb3d8bbwe", XboxSource.PackageFamilyName("Microsoft.SeaofThieves",
            "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US"));
        Assert.Equal("FocusHomeInteractiveSA.APlagueTaleFelons_4hny5m903y3g0",
            XboxSource.PackageFamilyName("FocusHomeInteractiveSA.APlagueTaleFelons", "CN=244B08DA-6A27-4DCD-99EF-F6DCBE2A0C28"));
    }

    static byte[] GamingRoot(uint count, params string[] paths) =>
        [.. "RGBX"u8, .. BitConverter.GetBytes(count), .. paths.SelectMany(p => System.Text.Encoding.Unicode.GetBytes(p + "\0"))];

    [Fact]
    public void Xbox_gaming_root_lists_the_install_folders_and_rejects_anything_else()
    {
        Assert.Equal(["XboxGames"], XboxSource.ParseGamingRoot(GamingRoot(1, "XboxGames")));
        Assert.Equal(28, GamingRoot(1, "XboxGames").Length);   // the size the Xbox app writes
        Assert.Equal(["XboxGames", @"Games\XboxGames"], XboxSource.ParseGamingRoot(GamingRoot(2, "XboxGames", @"Games\XboxGames")));
        Assert.Equal([@"Games\XboxGames"], XboxSource.ParseGamingRoot(GamingRoot(1, @"Games\XboxGames")));

        Assert.Null(XboxSource.ParseGamingRoot([.. "RGBY"u8, .. GamingRoot(1, "XboxGames")[4..]]));
        Assert.Null(XboxSource.ParseGamingRoot(GamingRoot(1, "XboxGames")[..20]));   // no terminator
        Assert.Null(XboxSource.ParseGamingRoot(GamingRoot(1, "XboxGames")[..6]));
        Assert.Null(XboxSource.ParseGamingRoot(GamingRoot(2, "XboxGames")));         // fewer paths than the count
        Assert.Null(XboxSource.ParseGamingRoot(GamingRoot(uint.MaxValue, "XboxGames")));
        Assert.Null(XboxSource.ParseGamingRoot(GamingRoot(0)));
        Assert.Null(XboxSource.ParseGamingRoot(GamingRoot(1, "")));
    }

    [Fact]
    public void Xbox_finds_games_in_the_folders_the_gaming_root_lists()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-xbox-test-").FullName;
        try
        {
            void Title(string library, string name)
            {
                var content = Directory.CreateDirectory(Path.Combine(root, library, name, "Content")).FullName;
                File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"),
                    $"""<Game><Identity Name="Pub.{name}" Version="1.0.0.0"/><ExecutableList><Executable Name="{name}.exe"/></ExecutableList></Game>""");
            }
            Title(@"Games\XboxGames", "Custom");
            Title("XboxGames", "Default");

            // the custom folder listed twice is scanned once, and XboxGames still is
            File.WriteAllBytes(Path.Combine(root, ".GamingRoot"), GamingRoot(2, @"Games\XboxGames", @"\Games\XboxGames"));
            Assert.Equal(["xbox:Pub.Custom", "xbox:Pub.Default"], new XboxSource([root]).Discover().Select(g => g.Id));
            File.WriteAllBytes(Path.Combine(root, ".GamingRoot"), GamingRoot(1, "XboxGames"));
            Assert.Equal(["xbox:Pub.Default"], new XboxSource([root]).Discover().Select(g => g.Id));

            File.WriteAllBytes(Path.Combine(root, ".GamingRoot"), [.. "RGBX"u8, 0xFF, 0xFF]);
            Assert.Equal(["xbox:Pub.Default"], new XboxSource([root]).Discover().Select(g => g.Id));
            File.Delete(Path.Combine(root, ".GamingRoot"));
            Assert.Equal(["xbox:Pub.Default"], new XboxSource([root]).Discover().Select(g => g.Id));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>For every store and the manual add, an Unreal game's &lt;Name&gt;-&lt;Platform&gt;-Shipping.exe in a
    /// &lt;Project&gt;\Binaries\&lt;Platform&gt; folder wins over the stub or launcher the store names, when it is the game's: the
    /// one there is, or of several the one tied to the stub's name. Tools and servers, and installs that aren't Unreal, never.</summary>
    [Fact]
    public void Unreal_shipping_exe_wins_over_the_stub_or_launcher_a_store_names()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-shipping-test-").FullName;
        try
        {
            string Put(string dir, string f, int size)
            {
                var path = Path.Combine(root, dir, f);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[size]);
                return path;
            }
            string Unreal(string dir) => Directory.CreateDirectory(Path.Combine(root, dir, "Engine")).Parent!.FullName;

            // Dead Island 2 from the Xbox app: MicrosoftGame.config names Content\DeadIsland.exe, a 1 MB bootstrap stub
            var content = Unreal(@"XboxGames\Dead Island 2\Content");
            File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"),
                """<Game><Identity Name="DeepSilver.578840CD64788" Version="1.124.4948.0"/><ExecutableList><Executable Name="DeadIsland.exe" TargetDeviceFamily="PC"/></ExecutableList></Game>""");
            var stub = Put(@"XboxGames\Dead Island 2\Content", "DeadIsland.exe", 1_098_752);
            var gdk = Put(@"XboxGames\Dead Island 2\Content\DeadIsland\Binaries\WinGDK", "DeadIsland-WinGDK-Shipping.exe", 4096);
            Put(@"XboxGames\Dead Island 2\Content\EpicOnlineServices", "EpicOnlineServicesInstaller.exe", 8192);
            var xbox = new XboxSource([root]).Discover().Single();
            Assert.Equal(stub, xbox.ExePath);   // what the config names
            Assert.Equal(gdk, GameFiles.GameExe(xbox.InstallDir, xbox.ExePath));
            Assert.Equal(gdk, GameFiles.FindExe(content));   // the manual add and the stores without a configured exe

            // a Shipping build the config marks dev-only or for another device family is never the game; others are
            var dev = Unreal(@"Excluded\Content");
            var devStub = Put(@"Excluded\Content", "Game.exe", 100);
            Put(@"Excluded\Content\Game\Binaries\WinGDK", "Game-WinGDK-Shipping.exe", 4096);
            Put(@"Excluded\Content\Console\Binaries\WinGDK", "Console-WinGDK-Shipping.exe", 4096);
            File.WriteAllText(Path.Combine(dev, "MicrosoftGame.config"), """<Game><Identity Name="Pub.Excluded" Version="1.0.0.0"/><ExecutableList><Executable Name="Game.exe" TargetDeviceFamily="PC"/>"""
                + """<Executable Name="Game\Binaries\WinGDK\Game-WinGDK-Shipping.exe" IsDevOnly="true"/>"""
                + """<Executable Name="Console\Binaries\WinGDK\Console-WinGDK-Shipping.exe" TargetDeviceFamily="Scarlett"/></ExecutableList></Game>""");
            Assert.Equal(devStub, GameFiles.GameExe(dev, devStub));
            Assert.Equal(devStub, GameFiles.FindExe(dev, devStub));   // nor in the scan after the Shipping lookup
            Assert.Equal(devStub, GameFiles.FindExe(dev));
            var pc = Put(@"Excluded\Content\Pc\Binaries\WinGDK", "Game-WinGDK-Shipping.exe", 4096);   // not in the config: the game's
            Assert.Equal(pc, GameFiles.GameExe(dev, devStub));

            // Steam's Win64 layout, with a launcher a launch option names, Engine's helpers, a 32-bit build and a patcher's copy
            var steam = Unreal(@"steamapps\common\Game");
            var launcher = Put(@"steamapps\common\Game", "Game.exe", 300_000);
            var win64 = Put(@"steamapps\common\Game\Game\Binaries\Win64", "Game-Win64-Shipping.exe", 4096);
            Put(@"steamapps\common\Game\Engine\Binaries\Win64", "CrashReportClient-Win64-Shipping.exe", 8192);
            Put(@"steamapps\common\Game\Game\Binaries\Win32", "Game-Win32-Shipping.exe", 8192);
            Put(@"steamapps\common\Game\PatchData\Game\Binaries\Win64", "Game-Win64-Shipping.exe", 8192);
            Assert.Equal(win64, GameFiles.GameExe(steam, launcher));
            Assert.Equal(win64, GameFiles.FindExe(steam, "Game.exe"));
            Assert.Equal(win64, GameFiles.GameExe(steam, win64));

            // the project folder needn't be named like the exe (Avowed: Alabama), and the platform folder names the suffix
            var avowed = Unreal("Avowed");
            var alabama = Put(@"Avowed\Alabama\Binaries\WinGDK", "Avowed-WinGDK-Shipping.exe", 4096);
            Put(@"Avowed\Alabama\Binaries\WinGDK", "Avowed-Win64-Shipping.exe", 8192);   // not its platform folder's
            Assert.Equal(alabama, GameFiles.GameExe(avowed, Put("Avowed", "Avowed.exe", 100)));
            var grts = Put(@"Grts\Proj\Binaries\WinGRTS", "Proj-WinGRTS-Shipping.exe", 4096);
            Assert.Equal(grts, GameFiles.GameExe(Unreal("Grts"), Put("Grts", "Proj.exe", 100)));

            // a larger tool or server build beside the game is never it
            var game = Put(@"Tools\Game\Binaries\WinGDK", "Game-WinGDK-Shipping.exe", 4096);
            Put(@"Tools\Tool\Binaries\WinGDK", "Tool-WinGDK-Shipping.exe", 8192);
            Put(@"Tools\Game\Binaries\Win64", "GameServer-Win64-Shipping.exe", 8192);
            Assert.Equal(game, GameFiles.GameExe(Unreal("Tools"), Put("Tools", "Launcher.exe", 100)));

            // several game builds: the one the stub's name ties, by the exe's name or its project's; none tied keeps the store's
            var two = Unreal("Pair");
            var tied = Put(@"Pair\Two\Binaries\WinGDK", "Main-WinGDK-Shipping.exe", 4096);
            Put(@"Pair\Other\Binaries\Win64", "Other-Win64-Shipping.exe", 8192);
            Assert.Equal(tied, GameFiles.GameExe(two, Put("Pair", "Two.exe", 100)));
            var untied = Put("Pair", "Neither.exe", 100);
            var said = new List<string>();
            Assert.Equal(untied, GameFiles.GameExe(two, untied, said.Add));
            Assert.Contains("Other-Win64-Shipping.exe", Assert.Single(said));

            // no Engine folder: not an Unreal install, its exe stays whatever is named like a Shipping build
            var other = Put("Other", "Other.exe", 100);
            Put(@"Other\Other\Binaries\Win64", "Other-Win64-Shipping.exe", 4096);
            Assert.Equal(other, GameFiles.GameExe(Path.Combine(root, "Other"), other));
            Assert.Equal(other, GameFiles.GameExe(Path.Combine(root, "Missing"), other));
            Assert.False(GameFiles.IsShipping(@"D:\Game-Win64-Shipping.exe"));   // a drive's root has no Binaries folder above
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>An EasyAntiCheat bootstrap a store names gives way to the exe its EasyAntiCheat\Settings.json starts, and the
    /// Unreal rule then applies to that exe. A missing or broken file keeps the named exe.</summary>
    [Fact]
    public void An_EasyAntiCheat_bootstrap_gives_way_to_the_exe_its_settings_start()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-eac-exe-test-").FullName;
        try
        {
            string Put(string dir, string f, int size = 100)
            {
                var path = Path.Combine(root, dir, f);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, new byte[size]);
                return path;
            }
            void Settings(string dir, string json, string folder = "EasyAntiCheat", string file = "Settings.json") =>
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(root, dir, folder)).FullName, file), json);
            string Unreal(string dir) => Directory.CreateDirectory(Path.Combine(root, dir, "Engine")).Parent!.FullName;

            // Gears of War: E-Day from the Xbox app: the config names the bootstrap, the game ships no Shipping exe
            var content = Unreal(@"XboxGames\EDay\Content");
            File.WriteAllText(Path.Combine(content, "MicrosoftGame.config"),
                """<Game><Identity Name="Microsoft.EDay" Version="1.0.0.0"/><ExecutableList><Executable Name="start_protected_game.exe" TargetDeviceFamily="PC"/></ExecutableList></Game>""");
            var stub = Put(@"XboxGames\EDay\Content", "start_protected_game.exe");
            var game = Put(@"XboxGames\EDay\Content\Fairlight\Binaries\WinGDK", "GoWEDay.exe", 4096);
            Settings(@"XboxGames\EDay\Content", """{ "title": "E-Day", "executable": "Fairlight\\Binaries\\WinGDK\\GoWEDay.exe", "hide_gui": "false" }""");
            var xbox = new XboxSource([root]).Discover().Single();
            Assert.Equal(stub, xbox.ExePath);
            Assert.Equal(game, GameFiles.GameExe(xbox.InstallDir, xbox.ExePath));
            Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(xbox with { ExePath = game }, quick: true));
            Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(xbox with { ExePath = game }));

            // a Shipping exe still wins, whichever exe the settings name; their exe ties one of several
            var proj = Unreal("Proj");
            var bootstrap = Put("Proj", "start_protected_game.exe");
            Put(@"Proj\Proj\Binaries\Win64", "Proj.exe");
            var shipping = Put(@"Proj\Proj\Binaries\Win64", "Proj-Win64-Shipping.exe", 4096);
            Settings("Proj", """{ "executable": "Proj/Binaries/Win64/Proj.exe" }""");
            Assert.Equal(shipping, GameFiles.GameExe(proj, bootstrap));
            Settings("Proj", """{ "executable": "Proj/Binaries/Win64/Proj-Win64-Shipping.exe" }""");
            Assert.Equal(shipping, GameFiles.GameExe(proj, bootstrap));
            var two = Unreal("Two");
            var twoStub = Put("Two", "start_protected_game.exe");
            var real = Put(@"Two\Hemingway\Binaries\Win64", "Hemingway-Win64-Shipping.exe", 4096);
            Put(@"Two\Hemingway\Binaries\Win64", "Hemingway.exe");
            Put(@"Two\Other\Binaries\Win64", "Other-Win64-Shipping.exe", 4096);
            Assert.Equal(twoStub, GameFiles.GameExe(two, twoStub));   // none tied to the bootstrap
            Settings("Two", """{ "executable": "Hemingway\\Binaries\\Win64\\Hemingway.exe" }""");
            Assert.Equal(real, GameFiles.GameExe(two, twoStub));

            // Elden Ring Nightreign's shape: the bootstrap in a subfolder, a lower-case folder, a BOM; the file's own spelling
            var nr = Put(@"Nightreign\Game", "nightreign.exe", 4096);
            var nrStub = Put(@"Nightreign\Game", "start_protected_game.exe");
            Settings(@"Nightreign\Game", "\uFEFF{\n\t\"executable\"\t: \"NightReign.exe\",\n}", "easyanticheat", "settings.json");
            Assert.Equal(nr, GameFiles.GameExe(Path.Combine(root, "Nightreign"), nrStub));
            Assert.Equal(nr, GameFiles.GameExe(Path.Combine(root, "Nightreign"), nr));
            Assert.Equal(nr, GameFiles.FindExe(Path.Combine(root, "Nightreign")));
            // its Artbook/OST DLC: a Unity app (exe + _Data + UnityPlayer.dll) in its own folder, which discovery took for the game
            var artbook = Put(@"Nightreign\ArtbookOST", "ELDEN RING NIGHTREIGN DIGITAL ARTBOOK & MINI SOUNDTRACK.exe");
            Put(@"Nightreign\ArtbookOST", "UnityPlayer.dll");
            Put(@"Nightreign\ArtbookOST\ELDEN RING NIGHTREIGN DIGITAL ARTBOOK & MINI SOUNDTRACK_Data", "globalgamemanagers");
            Assert.Equal(nr, GameFiles.FindExe(Path.Combine(root, "Nightreign")));
            // a second bootstrap naming another exe: neither is trusted, discovery guesses as before
            Put(@"Nightreign\Mods", "start_protected_game.exe");
            Put(@"Nightreign\Mods", "Other.exe");
            Settings(@"Nightreign\Mods", """{ "executable": "Other.exe" }""");
            Assert.Equal(artbook, GameFiles.FindExe(Path.Combine(root, "Nightreign")));
            Directory.Delete(Path.Combine(root, @"Nightreign\Mods"), true);
            // settings naming an uninstaller, a launcher or a backup copy: not the game, here or through GameExe
            foreach (var (dir, exe) in new[] { ("", "unins000.exe"), ("", "Launcher.exe"), ("Backup", "nightreign.exe") })
            {
                Put(Path.Combine(@"Nightreign\Game", dir), exe);
                Settings(@"Nightreign\Game", $$"""{ "executable": "{{Path.Combine(dir, exe).Replace(@"\", @"\\")}}" }""", "easyanticheat", "settings.json");
                Assert.Equal(artbook, GameFiles.FindExe(Path.Combine(root, "Nightreign")));
                Assert.Equal(nrStub, GameFiles.GameExe(Path.Combine(root, "Nightreign"), nrStub));
            }
            Settings(@"Nightreign\Game", """{ "executable": "nightreign.exe" }""", "easyanticheat", "settings.json");
            Assert.Equal(nr, GameFiles.FindExe(Path.Combine(root, "Nightreign")));

            // another exe beside the one the settings start is a variant the user picked, not a bootstrap
            var dx12 = Put("Variant", "GameDX12.exe", 4096);
            Put("Variant", "Game.exe", 4096);
            Settings("Variant", """{ "executable": "Game.exe" }""");
            Assert.Equal(dx12, GameFiles.GameExe(Path.Combine(root, "Variant"), dx12));

            // nothing to go by: the named exe, no throw
            var bare = Put("Bare", "start_protected_game.exe");
            Put("Bare", "Game.exe", 4096);
            Put("", "Outside.exe");
            Assert.Equal(bare, GameFiles.GameExe(Path.Combine(root, "Bare"), bare));   // no settings
            foreach (var broken in new[] { "{ \"executable\": ", "[\"Game.exe\"]", "{ \"executable\": 5 }", "{ \"title\": \"Game\" }", "{ \"executable\": \"\" }",
                         "{ \"executable\": \"Missing.exe\" }", "{ \"executable\": \"..\\\\Outside.exe\" }", "{ \"executable\": \"Game*.exe\" }",
                         "{ \"executable\": \"Game.txt\" }", "{ \"executable\": \"Game\\u0000.exe\" }", "{ \"executable\": \"Game\\uD800.exe\" }", "" })
            {
                Settings("Bare", broken);
                Assert.Equal(bare, GameFiles.GameExe(Path.Combine(root, "Bare"), bare));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Gog_finds_cyberpunk_with_the_real_exe_not_the_prelauncher()
    {
        var games = new GogSource().Discover();
        foreach (var g in games) output.WriteLine($"{g.Id,-16} {g.Name,-40} {g.Version,-16} {g.ExePath}");
        if (games.Count == 0) return;   // GOG isn't installed on this machine

        var cp = Assert.Single(games, g => g.Id == "gog:1423049311");
        Assert.Equal("Cyberpunk 2077", cp.Name);
        Assert.EndsWith(@"\bin\x64\Cyberpunk2077.exe", cp.ExePath, StringComparison.OrdinalIgnoreCase);
        // the Phantom Liberty DLC registers its own id in the same folder but has no "game" playTask -> not a Game
        Assert.DoesNotContain(games, g => g.Id == "gog:1256837418");
    }

    [Fact]
    public void Ea_finds_finished_installs_with_the_exe_the_manifest_names()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-ea-test-").FullName;
        try
        {
            var reg = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string Install(string name, bool staged = false, bool touchedUp = true)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                Directory.CreateDirectory(Path.Combine(dir, "__Installer"));
                Directory.CreateDirectory(Path.Combine(dir, "bin"));
                File.WriteAllBytes(Path.Combine(dir, "bin", "Game.exe"), new byte[100]);
                File.WriteAllBytes(Path.Combine(dir, "Tool.exe"), new byte[5000]);   // bigger, at the root: FindExe's own guess
                var key = $@"HKEY_LOCAL_MACHINE\SOFTWARE\Fake\{name}\Install Dir";
                File.WriteAllText(Path.Combine(dir, "__Installer", "installerdata.xml" + (staged ? "_DiP_Staged" : "")), $"""
                    <?xml version='1.0' encoding='utf-8'?>
                    <DiPManifest version="4.0">
                      <buildMetaData><gameVersion version="1.0.0.11" /></buildMetaData>
                      <contentIDs><contentID>{name.Length}00</contentID></contentIDs>
                      <gameTitles><gameTitle locale="fr_FR">Le {name}</gameTitle><gameTitle locale="en_US">{name}™</gameTitle></gameTitles>
                      <runtime><launcher uid="1-1"><filePath>[{key}]bin\Game.exe</filePath><trial>0</trial></launcher></runtime>
                    </DiPManifest>
                    """);
                if (touchedUp) reg[key] = dir + @"\";
                return dir;
            }
            var done = Install("Done");
            Install("Downloading", staged: true, touchedUp: false);   // mid-download: every file is *_DiP_Staged
            Install("Destaged", touchedUp: false);                    // files in place, the installer's touchup not run yet
            reg[@"HKEY_LOCAL_MACHINE\SOFTWARE\Fake\Moved\Install Dir"] = @"X:\Elsewhere";
            Install("Moved", touchedUp: false);                       // a stale copy: the registry names another folder

            var games = new EaSource(Directory.EnumerateDirectories(root), k => reg.GetValueOrDefault(k)).Discover();
            Assert.Equal(new Game("ea:400", "Done", Store.EA, done, Path.Combine(done, "bin", "Game.exe"), "1.0.0.11"), Assert.Single(games));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A walk that runs out of time is not known to be clean, and says it ran out of time; a missing install is
    /// clean. A finding, or an Other no clock gave, never says so.</summary>
    [Fact]
    public void An_anti_cheat_check_out_of_time_is_other()
    {
        var dir = Directory.CreateTempSubdirectory("scskiller-anticheat-budget-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Game.exe"), new byte[100]);
            var game = new Game("test:budget", "Budget", Store.Other, dir, Path.Combine(dir, "Game.exe"));
            var late = 0;
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game, late: () => late++));
            Assert.Equal(0, late);
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game, budget: TimeSpan.Zero, late: () => late++));
            Assert.Equal(1, late);
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game with { Id = "battlenet:budget" }, budget: TimeSpan.Zero, late: () => late++));
            Directory.CreateDirectory(Path.Combine(dir, "EasyAntiCheat"));
            Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(game, late: () => late++));
            Assert.Equal(1, late);
            var gone = game with { InstallDir = Path.Combine(dir, "gone"), ExePath = Path.Combine(dir, "gone", "Game.exe") };
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(gone));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Hidden_and_system_anti_cheat_markers_are_found()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-test-").FullName;
        try
        {
            Game Install(string name, Action<string> mark)
            {
                var dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
                File.WriteAllBytes(Path.Combine(dir, "Game.exe"), new byte[100]);
                mark(dir);
                return new Game($"test:{name}", name, Store.Other, dir, Path.Combine(dir, "Game.exe"));
            }
            var eac = Install("eac", d => File.SetAttributes(Directory.CreateDirectory(Path.Combine(d, "EasyAntiCheat")).FullName, FileAttributes.Directory | FileAttributes.Hidden));
            var ricochet = Install("ricochet", d =>
            {
                File.WriteAllBytes(Path.Combine(d, "randgrid.sys"), [0]);
                File.SetAttributes(Path.Combine(d, "randgrid.sys"), FileAttributes.System | FileAttributes.Hidden);
            });
            var below = Install("below", d =>   // inside a hidden folder one level down
            {
                var hidden = Directory.CreateDirectory(Path.Combine(d, "bin")).FullName;
                File.WriteAllBytes(Path.Combine(hidden, "BEService_x64.exe"), [0]);
                File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);
            });
            var deep = Install("deep", d =>   // <install>\support\security, away from the exe's folder
                File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.Combine(d, "support", "security")).FullName, "BEService_x64.exe"), [0]));
            var clean = Install("clean", _ => { });
            var exeElsewhere = clean with { Id = "test:elsewhere", ExePath = Path.Combine(root, "eac", "Game.exe") };   // the exe's folder is outside the install

            Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(eac));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(ricochet));
            Assert.Equal(AntiCheat.BattlEye, GameFiles.DetectAntiCheat(below));
            Assert.Equal(AntiCheat.BattlEye, GameFiles.DetectAntiCheat(deep));
            Assert.Equal(AntiCheat.EasyAntiCheat, GameFiles.DetectAntiCheat(exeElsewhere));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(clean));
        }
        finally
        {
            foreach (var f in Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }))
                File.SetAttributes(f, File.GetAttributes(f) & FileAttributes.Directory);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NCGuard_in_a_plugin_folder_and_suffix_markers_are_found()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-test-").FullName;
        try
        {
            Game Install(string name, params string[] files)
            {
                var dir = Path.Combine(root, name);
                foreach (var f in files.Append(@"Game\Binaries\Win64\Game.exe"))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, f))!);
                    File.WriteAllBytes(Path.Combine(dir, f), [0]);
                }
                return new Game($"test:{name}", name, Store.Other, dir, Path.Combine(dir, @"Game\Binaries\Win64\Game.exe"));
            }
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install("ncguard", @"Game\Plugins\NCGuardSDK\Libraries\Win64\bb64.dll")));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install("xigncode3", @"Game\Binaries\Win64\x3.xem")));
            Assert.Equal(AntiCheat.BattlEye, GameFiles.DetectAntiCheat(Install("battleye", "Game_BE.exe")));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(Install("clean", @"Game\Plugins\NCGuardSDKTools\readme.txt", "notes.xem.txt")));
            Assert.Equal(AntiCheat.BattlEye, GameFiles.DetectAntiCheat(Install("beclient", @"Game\Binaries\Win64\BEClient_x64.dll")));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install("warframe", "Warframe.x64.exe")));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install("aniimo", @"Game\Binaries\Win64\NEP2.dll")));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install("aniimo-cleaner", "NEPCleaner.exe")));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install("halo", "arbiter.dll")));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(Install("not-arbiter", "arbiter.dll.bak", @"Game\myarbiter.dll", "nep2.txt")));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install("once-human", "ONCE_HUMAN.exe")));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install("wardogs", @"Elytra\Elytra-Setup.exe"), quick: true));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install("elytra-exe", @"Game\Binaries\Win64\Elytra.exe")));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(Install("not-elytra", "ElytraWings.pak", @"Game\Content\Elytra.uasset")));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install("forza-horizon-6", "ForzaHorizon6.exe")));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>NetEase's, Elytra's and Playground's anti-cheat leave no file in the install: these titles count by store id,
    /// with nothing else in their folder.</summary>
    [Theory]
    [InlineData("steam:2139460")]   // Once Human
    [InlineData("steam:2767030")]   // Marvel Rivals
    [InlineData("steam:2943650")]   // FragPunk
    [InlineData("steam:2782500")]   // Sword of Justice
    [InlineData("steam:1203220")]   // NARAKA: BLADEPOINT
    [InlineData("steam:2073850")]   // THE FINALS (Elytra)
    [InlineData("steam:2483190")]   // Forza Horizon 6
    [InlineData("steam:2016590")]   // Dark and Darker (IronShield)
    [InlineData("xbox:Microsoft.ForteBaseGame_8wekyb3d8bbwe")]   // Forza Horizon 6
    [InlineData("xbox:microsoft.fortebasegame_8WEKYB3D8BBWE")]   // the same, in another casing
    public void A_title_whose_anti_cheat_leaves_no_file_counts_by_its_store_id(string id)
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-test-").FullName;
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Game.exe"), [0]);
            var clean = new Game("steam:1", "clean", Store.Steam, root, Path.Combine(root, "Game.exe"));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(clean));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(clean with { Id = id }));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>TruckersMP leaves nothing in the game's folder: its folders or its uninstall entry make ETS2 and ATS anti-cheat games.</summary>
    [Fact]
    public void TruckersMp_marks_the_truck_simulators_by_its_folders_or_its_uninstall_entry()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-truckersmp-test-").FullName;
        try
        {
            var mp = Path.Combine(root, "TruckersMP");
            Assert.False(GameFiles.TruckersMp([mp], ["Steam", "Euro Truck Simulator 2"]));
            Assert.True(GameFiles.TruckersMp([mp], ["TruckersMP Launcher 1.3.19"]));
            Directory.CreateDirectory(Path.Combine(mp, "installation"));
            Assert.True(GameFiles.TruckersMp([mp], []));

            Game Install(string name, string exe)
            {
                var dir = Path.Combine(root, name);
                Directory.CreateDirectory(Path.Combine(dir, @"bin\win_x64"));
                File.WriteAllBytes(Path.Combine(dir, "effect.scs"), [0]);
                File.WriteAllBytes(Path.Combine(dir, @"bin\win_x64", exe), [0]);
                return new Game($"steam:{name}", name, Store.Steam, dir, Path.Combine(dir, @"bin\win_x64", exe));
            }
            var here = GameFiles.TruckersMpHere() ? AntiCheat.Other : AntiCheat.None;
            Assert.Equal(here, GameFiles.DetectAntiCheat(Install("227300", "eurotrucks2.exe")));
            Assert.Equal(here, GameFiles.DetectAntiCheat(Install("270880", "amtrucks.exe"), quick: true));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(Install("other", "game.exe")));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A Blizzard game installed by Battle.net but listed by another store, or added by hand, has no battlenet: id:
    /// Battle.net's own files in its folder mark it, and so does the static container a Steam install carries (Overwatch:
    /// data\.build.config, no Battle.net file anywhere).</summary>
    [Theory]
    [InlineData(".build.info")]
    [InlineData(".product.db")]
    [InlineData(@"data\.build.config")]
    public void BattleNet_install_files_mark_a_game_listed_elsewhere(string marker)
    {
        var dir = Directory.CreateTempSubdirectory("scskiller-anticheat-test-").FullName;
        try
        {
            var exe = Path.Combine(dir, "_retail_", "Game.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, [0]);
            var game = new Game("steam:1", "Blizzard game", Store.Steam, dir, exe);
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, marker))!);
            File.WriteAllText(Path.Combine(dir, marker), "");
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>Blizzard's static container counts only where Blizzard puts it, data\.build.config under a folder taken as the
    /// install (Overwatch on Steam; a hand-added copy whose folder is a subfolder of the game's): anywhere else in another
    /// game the file is just a name.</summary>
    [Fact]
    public void A_build_config_counts_only_in_the_install_roots_data_folder()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-test-").FullName;
        try
        {
            Game Install(string dir, string exe, Store store, params string[] files)
            {
                var path = Path.Combine(root, dir, exe);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [0]);
                foreach (var f in files)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, dir, f))!);
                    File.WriteAllText(Path.Combine(root, dir, f), "");
                }
                return store == Store.Manual ? new Game(ManualSource.IdOf(path), dir, store, Path.GetDirectoryName(path)!, path)
                    : new Game("steam:1", dir, store, Path.Combine(root, dir), path);
            }
            AntiCheat[] Both(Game g) => [GameFiles.DetectAntiCheat(g), GameFiles.DetectAntiCheat(g, quick: true)];

            var stray = Install("Modded", "Game.exe", Store.Steam, @"Content\mods\x\.build.config", @"Content\data\.build.config", ".build.config");
            Assert.Equal([AntiCheat.None, AntiCheat.None], Both(stray));
            var overwatch = Install("Overwatch", "Overwatch.exe", Store.Steam, @"data\.build.config");
            Assert.Equal([AntiCheat.Other, AntiCheat.Other], Both(overwatch));
            var added = Install("Library", @"Overwatch\_retail_\Overwatch.exe", Store.Manual, @"Overwatch\data\.build.config");
            Assert.Equal([AntiCheat.Other, AntiCheat.Other], Both(added));   // the folder above the one confirmed holds the container
            var sibling = Install("Shelf", @"Game\bin\Game.exe", Store.Manual, @"Other\data\.build.config");
            Assert.Equal([AntiCheat.None, AntiCheat.None], Both(sibling));   // another game's container beside it
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>War Thunder's standalone layout: BattlEye sits in the root, the exe in win64, which is the folder suggested.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_hand_added_game_sees_anti_cheat_in_the_folders_above_its_own(bool marked)
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-test-").FullName;
        try
        {
            var game = Path.Combine(root, "Games", "WarThunder");
            foreach (var f in marked ? new[] { @"BattlEye\BEService_x64.dll.txt", "aces_BE.exe", @"win64\aces.exe" } : [@"win32\aces.exe", @"win64\aces.exe"])
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(game, f))!);
                File.WriteAllBytes(Path.Combine(game, f), [0]);
            }
            var exe = Path.Combine(game, "win64", "aces.exe");
            var manual = new Game(ManualSource.IdOf(exe), "War Thunder", Store.Manual, Path.Combine(game, "win64"), exe);
            Assert.Equal(marked ? AntiCheat.BattlEye : AntiCheat.None, GameFiles.DetectAntiCheat(manual));
            Assert.Equal(marked ? AntiCheat.BattlEye : AntiCheat.None, GameFiles.DetectAntiCheat(manual, quick: true));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(manual with { Store = Store.Other }));   // a store's install folder is its root
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Delta Force (Anti-Cheat Expert in the exe's folder) and Zenless Zone Zero (a HoYoverse game: its exe names
    /// it, whatever its driver is called) as Steam lays them out, and each other marker family on its own.</summary>
    [Fact]
    public void Ace_hoyoverse_javelin_and_vanguard_installs_are_anti_cheat()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-test-").FullName;
        try
        {
            Game Install(string name, string exe, params string[] files)
            {
                var dir = Path.Combine(root, name);
                foreach (var f in files.Append(exe))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, f))!);
                    File.WriteAllBytes(Path.Combine(dir, f), [0]);
                }
                return new Game($"steam:{name}", name, Store.Steam, dir, Path.Combine(dir, exe));
            }
            const string ue = @"Game\Binaries\Win64\Game-Win64-Shipping.exe";
            var deltaForce = Install("Delta Force", @"Game\DeltaForce\Binaries\Win64\DeltaForceClient-Win64-Shipping.exe",
                @"Game\DeltaForce\Binaries\Win64\AntiCheatExpert\SGuard\x64\Plugins\ACE-DFS64.dll", @"Game\DeltaForce\Binaries\Win64\AntiCheatExpert\ACE-Setup64.exe");
            var zzz = Install("Zenless Zone Zero", @"games\ZenlessZoneZero Game\ZenlessZoneZero.exe",
                @"games\ZenlessZoneZero Game\ZenlessZoneZero_Data\globalgamemanagers", @"games\ZenlessZoneZero Game\UnityPlayer.dll");
            foreach (var g in new[] { deltaForce, zzz })
            {
                Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(g));
                Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(g, quick: true));
            }
            foreach (var marker in new[] { @"SGuard\x64\ACE-DFS64.dll", "SGuard64.exe", "SGuardSvc64.exe", "ACE-Base64.dll", "ACE-Base.dat", "ACE-Service64.exe",
                         "ACE-ATS64.dll", "ACE-CSI64.dll", @"TenProtect\TPSvc.dll", "TesSafe.sys", "mhyprot.sys", "HYP.exe", "HYPHelper.exe", "HYPWorker.exe",
                         "EAAntiCheat.GameServiceLauncher.exe", "EAAntiCheat.GameServiceLauncher.dll", "vgk.sys", "vgc.exe" })
                Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install(marker.Replace('\\', '_'), ue, marker)));
            foreach (var exe in new[] { "GenshinImpact.exe", "YuanShen.exe", "StarRail.exe", "BH3.exe" })
                Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(Install(exe, exe, $@"{Path.GetFileNameWithoutExtension(exe)}_Data\globalgamemanagers")));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(Install("clean", ue, @"Game\SGuardian\readme.txt", "ZenlessZoneZero.exe.bak", "HYP.exe.txt")));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>Riot's titles as the Riot Client lays them out, with no Vanguard file in any game folder: each is anti-cheat
    /// by its "Riot Games" folder and, moved out of it, by its exe's name, from whichever folder it is added or listed with.</summary>
    [Theory]
    [InlineData(@"VALORANT\live", @"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", @"ShooterGame\Binaries\Win64")]
    [InlineData(@"VALORANT\live", @"ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", "")]
    [InlineData(@"VALORANT\live", "VALORANT.exe", "")]
    [InlineData("League of Legends", @"Game\League of Legends.exe", "Game")]
    [InlineData("League of Legends", "LeagueClient.exe", "")]
    [InlineData(@"LoR\live", @"Game\LoR.exe", "Game")]
    [InlineData(@"2XKO\Live", @"Lion\Binaries\Win64\Lion-Win64-Shipping.exe", @"Lion\Binaries\Win64")]
    public void Riot_games_are_anti_cheat_by_their_folder_and_by_their_exe(string title, string exe, string added)
    {
        var root = Directory.CreateTempSubdirectory("scskiller-riot-test-").FullName;
        try
        {
            foreach (var library in new[] { "Riot Games", "Games" })
            {
                var dir = Path.Combine(root, library, title);
                var path = Path.Combine(dir, exe);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [0]);
                if (exe.StartsWith("ShooterGame")) File.WriteAllBytes(Path.Combine(dir, "VALORANT.exe"), [0]);
                foreach (var store in new[] { Store.Manual, Store.Epic, Store.Other })
                {
                    var g = new Game(store == Store.Manual ? ManualSource.IdOf(path) : $"{store}:{title}", title, store, Path.Combine(dir, added), path);
                    Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(g));
                    Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(g, quick: true));
                }
            }
            var other = Path.Combine(root, "Riot Games", "Other", "Game.exe");   // any exe in a "Riot Games" folder
            var clean = Path.Combine(root, "Games", "Other", "Game.exe");
            foreach (var f in new[] { other, clean }) File.WriteAllBytes(Path.Combine(Directory.CreateDirectory(Path.GetDirectoryName(f)!).FullName, "Game.exe"), [0]);
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(new Game(ManualSource.IdOf(other), "Other", Store.Manual, Path.GetDirectoryName(other)!, other)));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(new Game(ManualSource.IdOf(clean), "Other", Store.Manual, Path.GetDirectoryName(clean)!, clean)));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>The walk above a hand-added game stops at the temp folder: build tools leave Path.GetRandomFileName() folders
    /// there, and one ending in .xem (XIGNCODE's marker) made every hand-added game under it anti-cheat.</summary>
    [Fact]
    public void A_hand_added_game_in_the_temp_folder_doesnt_read_the_temp_folder()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-test-").FullName;
        var leftover = Path.Combine(Path.GetTempPath(), $"scskiller-anticheat-test-{Guid.NewGuid():N}.xem");
        try
        {
            var exe = Path.Combine(root, "Game", "Game.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
            File.WriteAllBytes(exe, [0]);
            var game = new Game(ManualSource.IdOf(exe), "Game", Store.Manual, Path.GetDirectoryName(exe)!, exe);
            Directory.CreateDirectory(leftover);
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game, quick: true));
        }
        finally
        {
            Directory.Delete(leftover, false);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void An_install_folder_that_cannot_be_listed_is_not_known_to_be_clean()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-acl-test-").FullName;
        var locked = Directory.CreateDirectory(Path.Combine(root, "support")).FullName;
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var deny = new System.Security.AccessControl.FileSystemAccessRule(me, System.Security.AccessControl.FileSystemRights.ListDirectory,
            System.Security.AccessControl.AccessControlType.Deny);
        try
        {
            File.WriteAllBytes(Path.Combine(root, "Game.exe"), new byte[100]);
            var game = new Game("test:locked", "Locked", Store.Other, root, Path.Combine(root, "Game.exe"));
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));

            var acl = new DirectoryInfo(locked).GetAccessControl();
            acl.AddAccessRule(deny);
            new DirectoryInfo(locked).SetAccessControl(acl);
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));   // whatever it holds: never installed into
        }
        finally
        {
            var acl = new DirectoryInfo(locked).GetAccessControl();
            acl.RemoveAccessRule(deny);
            new DirectoryInfo(locked).SetAccessControl(acl);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void An_install_root_that_cannot_be_listed_is_not_known_to_be_clean()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-root-test-").FullName;
        var install = Directory.CreateDirectory(Path.Combine(root, "Game")).FullName;
        var exeDir = Directory.CreateDirectory(Path.Combine(root, "Launcher")).FullName;   // the exe outside the install
        File.WriteAllBytes(Path.Combine(exeDir, "Game.exe"), new byte[100]);
        var game = new Game("test:root", "Root", Store.Other, install, Path.Combine(exeDir, "Game.exe"));
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        System.Security.AccessControl.FileSystemAccessRule Deny(System.Security.AccessControl.FileSystemRights r) =>
            new(me, r, System.Security.AccessControl.AccessControlType.Deny);
        // the install can't be listed nor its attributes read, and its parent can't be listed: Directory.Exists says false
        var (installDeny, parentDeny) = (Deny(System.Security.AccessControl.FileSystemRights.ListDirectory | System.Security.AccessControl.FileSystemRights.ReadAttributes),
            Deny(System.Security.AccessControl.FileSystemRights.ListDirectory));
        void Acl(string dir, System.Security.AccessControl.FileSystemAccessRule rule, bool add)
        {
            var acl = new DirectoryInfo(dir).GetAccessControl();
            if (add) acl.AddAccessRule(rule); else acl.RemoveAccessRule(rule);
            new DirectoryInfo(dir).SetAccessControl(acl);
        }
        try
        {
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));
            Acl(install, installDeny, true);
            Acl(root, parentDeny, true);
            Assert.False(Directory.Exists(install));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(game, quick: true));
        }
        finally
        {
            Acl(root, parentDeny, false);
            Acl(install, installDeny, false);
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Junctions_are_names_not_folders_to_walk()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-anticheat-link-test-").FullName;
        var links = new List<string>();
        try
        {
            void Junction(string link, string target)
            {
                links.Add(link);
                using var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
                    { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
                p.StandardOutput.ReadToEnd();
                p.WaitForExit();
                Assert.Equal(0, p.ExitCode);
            }
            var protectedGame = Directory.CreateDirectory(Path.Combine(root, "Protected", "EasyAntiCheat")).Parent!.FullName;
            var install = Directory.CreateDirectory(Path.Combine(root, "Clean", "bin")).Parent!.FullName;
            File.WriteAllBytes(Path.Combine(install, "bin", "Game.exe"), new byte[100]);
            var game = new Game("test:clean", "Clean", Store.Other, install, Path.Combine(install, "bin", "Game.exe"));
            Junction(Path.Combine(install, "shared"), protectedGame);   // into another game's install: not this game's files
            Junction(Path.Combine(install, "bin", "loop"), install);    // a cycle
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(game));

            Junction(Path.Combine(install, "BattlEye"), protectedGame);   // its name still counts
            Assert.Equal(AntiCheat.BattlEye, GameFiles.DetectAntiCheat(game));

            // the exe's own folder is a link: <install>\bin -> elsewhere\GameBin, with Game.exe and support\EasyAntiCheat
            var binTarget = Directory.CreateDirectory(Path.Combine(root, "elsewhere", "GameBin", "support", "EasyAntiCheat")).Parent!.Parent!.FullName;
            File.WriteAllBytes(Path.Combine(binTarget, "Game.exe"), new byte[100]);
            var linked = Directory.CreateDirectory(Path.Combine(root, "Linked")).FullName;
            Junction(Path.Combine(linked, "bin"), binTarget);
            var viaLink = new Game("test:linked", "Linked", Store.Other, linked, Path.Combine(linked, "bin", "Game.exe"));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(viaLink));
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(viaLink, quick: true));
        }
        finally
        {
            foreach (var link in links.Where(Directory.Exists)) Directory.Delete(link);   // the link only, never its target
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void Launcher_ids_never_name_a_folder_outside_the_data_folder()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-ids-test-").FullName;
        try
        {
            var store = new Core.App.AppStore(Path.Combine(root, "data"));
            var games = Path.GetFullPath(Path.Combine(root, "data", "games"));
            var install = Directory.CreateDirectory(Path.Combine(root, "Game")).FullName;
            File.WriteAllBytes(Path.Combine(install, "Game.exe"), new byte[100]);
            const string up = @"..\..\..\..\..\..\..\..\XboxGames\Demo\Content";

            var manifests = Directory.CreateDirectory(Path.Combine(root, "Manifests")).FullName;   // Epic: the manifest's AppName
            File.WriteAllText(Path.Combine(manifests, "a.item"), System.Text.Json.JsonSerializer.Serialize(new { AppName = @"x\" + up, InstallLocation = install }));
            File.WriteAllText(Path.Combine(manifests, "b.item"), System.Text.Json.JsonSerializer.Serialize(new { AppName = @"C:\Windows", InstallLocation = install }));
            Directory.CreateDirectory(Path.Combine(install, "__Installer"));   // EA: the installer's contentID
            File.WriteAllText(Path.Combine(install, "__Installer", "installerdata.xml"),
                $"<DiPManifest><contentIDs><contentID>x/{up.Replace('\\', '/')}</contentID></contentIDs><runtime><launcher><filePath>Game.exe</filePath></launcher></runtime></DiPManifest>");
            var ids = new EpicSource(manifests).Discover().Concat(new EaSource([install], _ => null).Discover()).Select(g => g.Id).ToList();
            Assert.Equal(3, ids.Count);

            // Xbox: Identity.Name when the manifest has no Publisher; the rest are what a GameDir caller may pass
            string[] unsafeIds = [.. ids, $@"xbox:x\{up}", "xbox:..", "xbox:x. ", "..", ".", @"\\server\share", "steam:1/../../x", "epic:Demo/A", "%x", ""];
            string[] plainIds = ["epic:Demo_A", "epic:Demo:A", "epic:a:b", "epic:a%3Ab", "a_b"];
            foreach (var id in unsafeIds.Concat(plainIds))
            {
                var dir = Path.GetFullPath(store.GameDir(id));
                Assert.Equal(games, Path.GetDirectoryName(dir), StringComparer.OrdinalIgnoreCase);
                Assert.Equal(store.GameDir(id), store.GameDir(Core.App.AppStore.GameId(Path.GetFileName(dir))));   // the uninstall hook's way back
                Assert.Equal(unsafeIds.Contains(id), Path.GetFileName(dir).StartsWith('%'));
            }
            Assert.Equal(unsafeIds.Length, unsafeIds.Select(id => store.GameDir(id).ToUpperInvariant()).Distinct().Count());
            // the folders main used, whenever they are plain names; two ids main gave one folder still share it
            Assert.Equal(Path.Combine(games, "steam_1245620"), store.GameDir("steam:1245620"));
            Assert.Equal(Path.Combine(games, "xbox_Pub.Game_8wekyb3d8bbwe"), store.GameDir("xbox:Pub.Game_8wekyb3d8bbwe"));
            Assert.Equal(Path.Combine(games, "ea_Origin.OFR.50.0004321"), store.GameDir("ea:Origin.OFR.50.0004321"));
            Assert.Equal(Path.Combine(games, "epic_Demo_A"), store.GameDir("epic:Demo:A"));
            Assert.Equal(store.GameDir("epic:Demo_A"), store.GameDir("epic:Demo:A"));
            Assert.NotEqual(store.GameDir("epic:Demo/A"), store.GameDir("epic:Demo_A"));
            Assert.Equal(Path.Combine(games, "epic_a_b"), store.GameDir("epic:a:b"));
            Assert.Equal(Path.Combine(games, "epic_a%3Ab"), store.GameDir("epic:a%3Ab"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Ea_finds_jedi_survivor_once_its_download_is_done()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dirs = EaSource.InstallDirs().ToList();
        var games = new EaSource().Discover();
        output.WriteLine($"{dirs.Count} candidate folders, {sw.ElapsedMilliseconds} ms");
        foreach (var g in games) output.WriteLine($"{g.Id,-12} {g.Name,-30} {g.Version,-12} {GameFiles.DetectAntiCheat(g),-8} {g.ExePath}");
        const string jedi = @"D:\EA\Jedi Survivor";
        if (!Directory.Exists(jedi)) return;   // not on this machine
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using var b = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, view);
            using var k = b.OpenSubKey(@"SOFTWARE\Respawn\Jedi Survivor");
            using var u = b.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{B9CBE70C-C93E-467A-B112-D126650B08A5}");
            output.WriteLine($"{view}: Install Dir = {k?.GetValue("Install Dir")}, uninstall InstallLocation = {u?.GetValue("InstallLocation")}");
        }
        var touchedUp = EaSource.ReadRegistry(@"HKEY_LOCAL_MACHINE\SOFTWARE\Respawn\Jedi Survivor\Install Dir") != null;
        if (!File.Exists(Path.Combine(jedi, "__Installer", "installerdata.xml")) || !touchedUp)
        {
            output.WriteLine("Jedi Survivor: not installed yet (download in progress, or the installer's touchup not run)");
            Assert.DoesNotContain(games, g => g.InstallDir.Equals(jedi, StringComparison.OrdinalIgnoreCase));
            return;
        }
        var game = Assert.Single(games, g => g.Id == "ea:198300");
        Assert.EndsWith(@"\SwGame\Binaries\Win64\JediSurvivor.exe", game.ExePath, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void Ubisoft_discovery_skips_gracefully_when_connect_is_not_installed()
    {
        var games = new UbisoftSource().Discover();
        foreach (var g in games) output.WriteLine($"{g.Id} {g.Name} {g.ExePath}");
        // Ubisoft Connect itself isn't installed on this machine (D:\Ubisoft is a bare folder, no registry Installs
        // key), so this only proves Discover() doesn't throw; a machine with Connect installed exercises the rest.
    }

    [Trait("Needs", "Game")]
    [Fact]
    public void BattleNet_finds_overwatch_conservatively_marked_as_anti_cheat()
    {
        var games = new BattleNetSource().Discover();
        foreach (var g in games) output.WriteLine($"{g.Id,-20} {g.Name,-20} {GameFiles.DetectAntiCheat(g)} {g.ExePath}");
        if (games.Count == 0) return;   // Battle.net isn't installed on this machine

        var ow = Assert.Single(games, g => g.Name == "Overwatch");
        Assert.Equal("battlenet:prometheus", ow.Id);
        Assert.True(File.Exists(ow.ExePath), ow.ExePath);
        Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(ow));   // Warden: conservative, not file-based
    }

    [Fact]
    public void HoYoPlay_lists_finished_installs_and_each_games_own_anti_cheat_files_flag_it()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-hoyoplay-test-").FullName;
        try
        {
            void Put(string rel, string text = "") { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllText(Path.Combine(root, rel), text); }
            // the install roots' files as HoYoPlay lays them out (names only)
            foreach (var (dir, exe, ac) in new[]
            {
                ("Genshin Impact game", "GenshinImpact", new[] { "HoYoKProtect.sys", "mhypbase.dll" }),
                ("Star Rail", "StarRail", new[] { "HoYoKProtect.sys", "mhypbase.dll" }),
                ("ZenlessZoneZero Game", "ZenlessZoneZero", new[] { "HoYoKProtect.sys", "mhypbase.dll" }),
                ("Honkai Impact 3rd game", "BH3", new[] { "ACE-BASE.sys", @"AntiCheatExpert\SGuard64.exe" }),
            })
            {
                Put($@"{dir}\{exe}.exe", new string('x', 100));
                Put($@"{dir}\{exe}_Data\globalgamemanagers");
                Put($@"{dir}\UnityPlayer.dll");
                Put($@"{dir}\config.ini", "[General]\r\nchannel=1\r\ncps=hyp_hoyoverse\r\ngame_version=5.1.0\r\nsub_channel=0\r\n");
                foreach (var f in ac) Put($@"{dir}\{f}");
            }
            Put(@"Downloading\StarRail.exe", "x");   // no config.ini yet

            var games = new HoYoPlaySource([("hk4e_global", Path.Combine(root, "Genshin Impact game")), ("hkrpg_global", Path.Combine(root, "Star Rail")),
                ("nap_global", Path.Combine(root, "ZenlessZoneZero Game")), ("bh3_global", Path.Combine(root, "Honkai Impact 3rd game")),
                ("hkrpg_cn", Path.Combine(root, "Downloading")), ("hk4e_cn", Path.Combine(root, "Missing"))]).Discover();

            Assert.Equal(["hoyoplay:hk4e_global Genshin Impact GenshinImpact.exe 5.1.0", "hoyoplay:hkrpg_global Honkai: Star Rail StarRail.exe 5.1.0",
                "hoyoplay:nap_global Zenless Zone Zero ZenlessZoneZero.exe 5.1.0", "hoyoplay:bh3_global Honkai Impact 3rd BH3.exe 5.1.0"],
                games.Select(g => $"{g.Id} {g.Name} {Path.GetFileName(g.ExePath)} {g.Version}"));
            foreach (var g in games)
            {
                Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(g));
                Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(g with { Id = "epic:x" }));   // the same install from another store: its files
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Riot_lists_each_product_settings_install_with_the_game_exe_and_flags_every_one_anti_cheat()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-riot-test-").FullName;
        try
        {
            void Put(string rel, int size = 0) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllBytes(Path.Combine(root, rel), new byte[size]); }
            void Settings(string key) => Put($@"Metadata\{key}\{key}.product_settings.yaml");
            void Yaml(string key, string text) => File.WriteAllText(Path.Combine(root, "Metadata", key, $"{key}.product_settings.yaml"), text);
            string Fwd(string rel) => Path.Combine(root, rel).Replace('\\', '/');   // the client writes forward slashes
            // the yaml as the client writes it, trimmed
            string Of(string install) => "auto_patching_enabled_by_player: false\nlocale_data:\n    available_locales:\n    - en_US\n    default_locale: en_US\n" +
                $"patching_policy: manual\nproduct_install_full_path: \"{Fwd(install)}\"\nproduct_install_root: \"{Fwd("")}\"\n" +
                "settings:\n    create_shortcut: true\n    create_uninstall_key: true\n    locale: en_US\nshortcut_name: Game.lnk\nshould_repair: false\n";
            void Product(string key, string install) { Settings(key); Yaml(key, Of(install)); }

            Put(@"VALORANT\live\VALORANT.exe", 100);   // the launcher stub
            Put(@"VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe", 1000);
            Put(@"VALORANT\live\Engine\Binaries\Win64\CrashReportClient.exe", 2000);
            Product("valorant.live", @"VALORANT\live");
            Yaml("valorant.live", Of(@"VALORANT\live").ReplaceLineEndings("\r\n"));
            foreach (var lol in new[] { "League of Legends", "League of Legends (PBE)" })
            {
                Put($@"{lol}\LeagueClient.exe", 5000);
                Put($@"{lol}\LeagueClientUx.exe", 3000);
                Put($@"{lol}\Game\League of Legends.exe", 1000);
            }
            Product("league_of_legends.live", "League of Legends");
            Product("league_of_legends.pbe", "League of Legends (PBE)");
            Put(@"LoR\live\Game\LoR.exe", 500);
            Put(@"LoR\live\Game\LoR_Data\globalgamemanagers");
            Put(@"LoR\live\Game\UnityCrashHandler64.exe", 900);
            Product("bacon.live", @"LoR\live");
            Put(@"2XKO\Lion\Binaries\Win64\Lion-Win64-Shipping.exe", 1000);   // the project folder's name here is the fixture's
            Put(@"2XKO\2XKO.exe", 100);
            Product("lion.live", "2XKO");
            Put(@"New Game\live\NewGame.exe", 100);
            Product("newgame.live", @"New Game\live");
            Put(@"Installing\LeagueClient.exe", 5000);   // League's client is in, the game not yet
            Product("league_of_legends.beta", "Installing");
            Product("bacon.pbe", "Missing");
            Put(@"Riot Client\RiotClientServices.exe", 100);
            Product("riot_client.live", "Riot Client");
            Settings("Riot Client");   // no patchline
            Settings("valorant.empty");   // no install path
            Settings("valorant.blank");
            Yaml("valorant.blank", $"product_install_full_path:\nproduct_install_root: \"{Fwd(@"VALORANT\live")}\"\n");   // an empty value, not the next line's

            var games = new RiotSource(Path.Combine(root, "Metadata"), entry => entry switch { "Riot Game newgame.live" => "New Game", "Riot Game bacon.live" => " ", _ => null })
                .Discover()
                .OrderBy(g => g.Id, StringComparer.Ordinal).ToList();

            Assert.Equal([
                @"riot:bacon.live Legends of Runeterra LoR\live\Game\LoR.exe",
                @"riot:league_of_legends.live League of Legends League of Legends\Game\League of Legends.exe",
                @"riot:league_of_legends.pbe League of Legends (PBE) League of Legends (PBE)\Game\League of Legends.exe",
                @"riot:lion.live 2XKO 2XKO\Lion\Binaries\Win64\Lion-Win64-Shipping.exe",
                @"riot:newgame.live New Game New Game\live\NewGame.exe",
                @"riot:valorant.live VALORANT VALORANT\live\ShooterGame\Binaries\Win64\VALORANT-Win64-Shipping.exe"],
                games.Select(g => $"{g.Id} {g.Name} {Path.GetRelativePath(root, g.ExePath)}"));
            Assert.All(games, g => Assert.Equal((Store.Other, (string?)null), (g.Store, g.Version)));
            Assert.Equal(Path.Combine(root, "VALORANT", "live"), games[^1].InstallDir);
            foreach (var g in games)
            {
                Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(g));
                Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(g, quick: true));
            }
            // outside a "Riot Games" folder, with no Riot exe name: only its riot: id marks it
            Assert.Equal(AntiCheat.None, GameFiles.DetectAntiCheat(games.Single(g => g.Id == "riot:newgame.live") with { Id = "test:newgame" }));
            Assert.Empty(new RiotSource(Path.Combine(root, "None"), _ => null).Discover());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Nvidia_cache_size_encoding()
    {
        Assert.Equal(new CacheLimit(100L << 30, false), NvidiaBackend.Decode(0x19000, false));   // NVCP "100 GB"
        Assert.Equal(new CacheLimit(null, false), NvidiaBackend.Decode(0xFFFFFFFF, false));
        Assert.Equal(0x19000u, NvidiaBackend.Encode(new CacheLimit(100L << 30, false)));
        Assert.Equal(0xFFFFFFFFu, NvidiaBackend.Encode(new CacheLimit(null, false)));
        Assert.Equal(1u, NvidiaBackend.Encode(new CacheLimit(1, false)));   // rounds up to whole MiB
    }

    [Fact]
    public void Nvidia_auto_shader_compilation_decoding()
    {
        Assert.Equal(AutoShaderCompilation.Off, NvidiaBackend.DecodeAutoShaderCompilation(null));   // not set = the App's default
        Assert.Equal(AutoShaderCompilation.Off, NvidiaBackend.DecodeAutoShaderCompilation(0));
        Assert.Equal(AutoShaderCompilation.Low, NvidiaBackend.DecodeAutoShaderCompilation(1));
        Assert.Equal(AutoShaderCompilation.Medium, NvidiaBackend.DecodeAutoShaderCompilation(2));
        Assert.Equal(AutoShaderCompilation.High, NvidiaBackend.DecodeAutoShaderCompilation(3));
        Assert.Null(NvidiaBackend.DecodeAutoShaderCompilation(4));   // unknown: shown as not readable, never guessed
    }

    [Trait("Needs", "Gpu")]
    [Fact]
    public void Nvidia_auto_shader_compilation_reads_without_changing_anything()
    {
        // Read-only: GetSetting on the base profile + schtasks /Query. Never call SetAutoShaderCompilation from tests
        // (a user decision: it writes a global driver setting and runs NvOSC.exe, which registers a scheduled task).
        if (GpuBackends.Detect() is not NvidiaBackend nv) return;
        var state = nv.GetAutoShaderCompilation();
        output.WriteLine($"Auto Shader Compilation: {state?.ToString() ?? "not readable"}, NvOSC.exe: {NvidiaBackend.NvOscPath() ?? "not found"}");
        Assert.NotNull(state);
        Assert.NotNull(NvidiaBackend.NvOscPath());   // r610+: in the active driver's DriverStore folder
    }

    // Fake NvAPI functions for the argument-layout tests: record what they get, answer what the test says.
    static int fakeRc;
    static uint fakeValue, gotId, gotVersion, gotType, gotValue, gotA, gotB;
    static nint gotSession, gotProfile;
    static bool gotFlags, publicCalled;

    [UnmanagedCallersOnly]
    static unsafe int FakeGetEx(nint session, nint profile, uint id, byte* s, uint* flags)
    {
        (gotSession, gotProfile, gotId, gotVersion, gotFlags) = (session, profile, id, *(uint*)s, flags != null && *flags == 0);
        *(uint*)(s + 8220) = fakeValue;
        return fakeRc;
    }

    [UnmanagedCallersOnly]
    static unsafe int FakeGet(nint session, nint profile, uint id, byte* s)
    {
        publicCalled = true;
        return -160;   // what the public GetSetting answers for the hidden id, set or not
    }

    [UnmanagedCallersOnly]
    static unsafe int FakeSetEx(nint session, nint profile, byte* s, uint a, uint b)
    {
        (gotSession, gotProfile, gotVersion, gotId, gotType, gotValue, gotA, gotB) =
            (session, profile, *(uint*)s, *(uint*)(s + 4100), *(uint*)(s + 4104), *(uint*)(s + 8220), a, b);
        return 0;
    }

    [Fact]
    public unsafe void Nvidia_auto_shader_ex_calls_pass_the_nvcplplugin_argument_layout()
    {
        nint getEx = (nint)(delegate* unmanaged<nint, nint, uint, byte*, uint*, int>)&FakeGetEx;
        nint get = (nint)(delegate* unmanaged<nint, nint, uint, byte*, int>)&FakeGet;
        nint setEx = (nint)(delegate* unmanaged<nint, nint, byte*, uint, uint, int>)&FakeSetEx;

        (fakeRc, fakeValue, publicCalled) = (0, 2, false);
        Assert.Equal(AutoShaderCompilation.Medium, NvidiaBackend.ReadAutoShaderLevel(11, 22, getEx, get));
        Assert.Equal((11, 22, 0x00EAD189u, 0x13020u, true), (gotSession, gotProfile, gotId, gotVersion, gotFlags));   // NVDRS_SETTING_V1, flags in = 0
        Assert.False(publicCalled);
        (fakeRc, fakeValue) = (-160, 0);
        Assert.Equal(AutoShaderCompilation.Off, NvidiaBackend.ReadAutoShaderLevel(11, 22, getEx, get));   // Ex sees hidden ids: not set = Off
        Assert.Null(NvidiaBackend.ReadAutoShaderLevel(11, 22, 0, get));   // older driver, public only: unknown, not Off
        fakeRc = -5;
        Assert.Null(NvidiaBackend.ReadAutoShaderLevel(11, 22, getEx, get));   // both fail: unknown

        Assert.Equal(0, NvidiaBackend.WriteAutoShaderLevel(33, 44, setEx, AutoShaderCompilation.High));
        Assert.Equal((33, 44, 0x13020u, 0x00EAD189u, 0u, 3u, 0u, 0u), (gotSession, gotProfile, gotVersion, gotId, gotType, gotValue, gotA, gotB));
        Assert.Equal(0x13020u, NvidiaBackend.SettingVersion1);
    }

    [Fact]
    public void Nvidia_auto_shader_compilation_refused_while_the_cache_is_disabled()
    {
        var disabled = new CacheLimit(0, false);
        Assert.NotNull(NvidiaBackend.RefuseReason(AutoShaderCompilation.Medium, disabled));
        Assert.Null(NvidiaBackend.RefuseReason(AutoShaderCompilation.Off, disabled));   // turning it off is always allowed
        Assert.Null(NvidiaBackend.RefuseReason(AutoShaderCompilation.High, new CacheLimit(100L << 30, false)));
        Assert.Null(NvidiaBackend.RefuseReason(AutoShaderCompilation.Low, new CacheLimit(null, false)));   // unlimited
        Assert.Null(NvidiaBackend.RefuseReason(AutoShaderCompilation.Low, null));   // unreadable: let the driver decide
    }

    [Fact]
    public void Nvidia_auto_shader_task_counts_only_when_registered_and_enabled()
    {
        const string head = """<?xml version="1.0" encoding="UTF-16"?><Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">""";
        const AutoShaderCompilation Medium = AutoShaderCompilation.Medium, High = AutoShaderCompilation.High;
        const string active = @"C:\Windows\System32\DriverStore\FileRepository\nv_dispi.inf_amd64_new\NvOSC.exe";
        (bool, bool) State(string? xml, string? nvosc = active, AutoShaderCompilation level = Medium) =>
            NvidiaBackend.TaskState(level, xml, nvosc) is var s ? (s.TaskReady, s.TaskEnabled) : default;
        string Task(string settings, string exe = active) =>
            head + $"""<Settings>{settings}</Settings><Actions Context="Author"><Exec><Command>{exe}</Command><Arguments>-runTool</Arguments></Exec></Actions></Task>""";
        Assert.Equal((false, false), State(null));   // schtasks: not registered
        Assert.Equal((true, true), State(Task("<Priority>4</Priority>")));   // Enabled absent = true
        Assert.Equal((true, true), State(Task("<Enabled>true</Enabled>")));
        Assert.Equal((false, false), State(Task("<Enabled>false</Enabled>")));   // the NVIDIA App's "off"
        Assert.Equal((false, true), State(Task("<Enabled>yes</Enabled>")));   // unparsable: re-registered

        string Runs(string exe, int priority = 4) => Task($"<Priority>{priority}</Priority>", exe);
        Assert.Equal((true, true), State(Runs(active.ToUpperInvariant())));
        Assert.Equal((false, true), State(Runs(active.Replace("_new", "_old"))));   // left from an earlier driver
        Assert.Equal((false, true), State(Runs(active), nvosc: null));   // the active driver has no NvOSC.exe
        // NvOSC writes 10/4/2 for Low/Medium/High: High saved but NvOSC failed leaves Medium's task.
        Assert.Equal((false, true), State(Runs(active), level: High));
        Assert.Equal((true, true), State(Runs(active, 2), level: High));
        Assert.Equal((true, true), State(Runs(active, 7), level: High));   // not one NvOSC writes: not judged
    }

    [Fact]
    public void Nvidia_auto_shader_apply_touches_only_what_differs()
    {
        static AutoShaderState S(AutoShaderCompilation level, bool ready, bool enabled) => new(level, ready, enabled);
        const AutoShaderCompilation Off = AutoShaderCompilation.Off, Low = AutoShaderCompilation.Low,
            Medium = AutoShaderCompilation.Medium, High = AutoShaderCompilation.High;
        // Already so (e.g. the NVIDIA App enabled it): no DRS write, no NvOSC run.
        Assert.Equal((false, (string?)null), NvidiaBackend.AutoShaderSteps(S(Medium, true, true), Medium));
        Assert.Equal((false, (string?)null), NvidiaBackend.AutoShaderSteps(S(Off, false, false), Off));
        // Not set (Off after a clean driver install): written, then the task registered.
        Assert.Equal((true, "-register"), NvidiaBackend.AutoShaderSteps(S(Off, false, false), Medium));
        Assert.Equal((true, "-register"), NvidiaBackend.AutoShaderSteps(S(Low, true, true), High));
        // Setting right, task missing, stale or of another level: only NvOSC, the setting is not rewritten.
        Assert.Equal((false, "-register"), NvidiaBackend.AutoShaderSteps(S(Medium, false, false), Medium));
        Assert.Equal((false, "-register"), NvidiaBackend.AutoShaderSteps(S(High, false, true), High));
        Assert.Equal((false, "-deregister"), NvidiaBackend.AutoShaderSteps(S(Off, true, true), Off));
        Assert.Equal((false, "-deregister"), NvidiaBackend.AutoShaderSteps(S(Off, false, true), Off));   // an earlier driver's task still enabled
        Assert.Equal((true, "-deregister"), NvidiaBackend.AutoShaderSteps(S(High, true, true), Off));
        // Unreadable: what was asked.
        Assert.Equal((true, "-register"), NvidiaBackend.AutoShaderSteps(null, Low));
        Assert.Equal((true, "-deregister"), NvidiaBackend.AutoShaderSteps(null, Off));

        Assert.Equal("Already on (Medium). Nothing to change.", NvidiaBackend.AutoShaderOutcome(Medium, changed: false));
        Assert.Equal("Turned on (High).", NvidiaBackend.AutoShaderOutcome(High, changed: true));
        Assert.Equal("Already off. Nothing to change.", NvidiaBackend.AutoShaderOutcome(Off, changed: false));
        Assert.Equal("Turned off.", NvidiaBackend.AutoShaderOutcome(Off, changed: true));
    }

    [Fact]
    public void Nvidia_auto_shader_apply_failures_say_what_happened()
    {
        var admin = NvidiaBackend.AutoShaderDrsFailure("NvAPI_DRS_SaveSettings", -137, "NVAPI_INVALID_USER_PRIVILEGE");
        Assert.IsType<UnauthorizedAccessException>(admin);
        Assert.Equal("switching NVIDIA Auto Shader Compilation needs administrator rights", admin.Message);
        Assert.Equal("this NVIDIA driver doesn't have the Auto Shader Compilation setting",
            NvidiaBackend.AutoShaderDrsFailure("NvAPI_DRS_SetSettingEx", -160, "NVAPI_SETTING_NOT_FOUND").Message);
        Assert.Equal("the NVIDIA driver didn't take the setting, nothing was changed (NvAPI_DRS_SaveSettings answered -1 NVAPI_ERROR)",
            NvidiaBackend.AutoShaderDrsFailure("NvAPI_DRS_SaveSettings", -1, "NVAPI_ERROR").Message);

        Assert.Equal("the setting is saved, but the NVIDIA task couldn't be set up: NvOSC.exe -register failed with Task scheduler operation failure (exit code 5). Apply again to retry.",
            NvidiaBackend.NvOscFailure("-register", 5, settingSaved: true));
        Assert.Equal("the NVIDIA task couldn't be removed: NvOSC.exe -deregister failed with Task scheduler operation failure (exit code 5). Apply again to retry.",
            NvidiaBackend.NvOscFailure("-deregister", 5, settingSaved: false));
        Assert.Contains("an unknown error (exit code 99)", NvidiaBackend.NvOscFailure("-register", 99, false));
        Assert.Contains("an unknown error (exit code -1)", NvidiaBackend.NvOscFailure("-register", -1, false));
        Assert.Equal("the setting is saved, but NvOSC.exe -register is still running after a minute, so the NVIDIA task may not be set up yet. Wait a minute before applying again.",
            NvidiaBackend.NvOscFailure("-register", null, settingSaved: true));
        Assert.Equal("the setting is saved, but the NVIDIA task couldn't be set up: NvOSC.exe -register didn't start (Access is denied.). Apply again to retry.",
            NvidiaBackend.NvOscFailure("-register", null, settingSaved: true, "Access is denied."));
        Assert.Equal("the NVIDIA task couldn't be set up: NvOSC.exe -register failed with Task scheduler operation failure (exit code 5). NvOSC.exe said: Failed to kill and disable task for task \\NVIDIA Shader Compiler: 0x80070005. Apply again to retry."
                     + "\nNvOSC.exe output:\nFailed to kill and disable task for task \\NVIDIA Shader Compiler: 0x80070005.\nExiting with code 5 (Task scheduler operation failure)",
            NvidiaBackend.NvOscFailure("-register", 5, settingSaved: false, output: "Failed to kill and disable task for task \\NVIDIA Shader Compiler: 0x80070005.\r\n\r\n  Exiting with code 5 (Task scheduler operation failure)  \r\n"));
        Assert.Contains("NvOSC.exe said: Task setup failed. Apply", NvidiaBackend.NvOscFailure("-register", 5, false, output: "Task setup failed.\nExiting with code 5 (Task scheduler operation failure)"));
    }

    [Fact]
    public void Epic_keeps_a_base_game_whose_manifest_names_itself_as_the_main_game()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-epic-test-").FullName;
        try
        {
            var install = Directory.CreateDirectory(Path.Combine(root, "Game")).FullName;
            File.WriteAllBytes(Path.Combine(install, "Game.exe"), new byte[100]);
            var manifests = Directory.CreateDirectory(Path.Combine(root, "Manifests")).FullName;
            void Item(string file, object m) => File.WriteAllText(Path.Combine(manifests, file), System.Text.Json.JsonSerializer.Serialize(m));
            Item("a.item", new { AppName = "Game", MainGameAppName = "Game", InstallLocation = install });   // Legendary's export of a base game
            Item("b.item", new { AppName = "Addon", MainGameAppName = "Game", InstallLocation = install });  // an add-on in the game's install
            Item("c.item", new { AppName = "Other", MainGameAppName = "", InstallLocation = install });      // the launcher's own base game
            Assert.Equal(["epic:Game", "epic:Other"], new EpicSource(manifests).Discover().Select(g => g.Id).Order());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Epic_reads_the_new_launchers_install_journal_beside_the_classic_manifests()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-epic-test-").FullName;
        try
        {
            string Install(string name)
            {
                var d = Directory.CreateDirectory(Path.Combine(root, "Epic Games", name)).FullName;
                File.WriteAllBytes(Path.Combine(d, name + ".exe"), new byte[100]);
                return d;
            }
            var (fresh, renamed, pending, both) = (Install("Fresh"), Install("Renamed"), Install("Pending"), Install("Both"));
            var gone = Path.Combine(root, "Epic Games", "Gone");
            var unreal = Directory.CreateDirectory(Path.Combine(root, "Epic Games", "Unreal", "UGame", "Binaries", "Win64")).FullName;
            File.WriteAllBytes(Path.Combine(unreal, "UGameBootstrapper.exe"), new byte[300]);
            File.WriteAllBytes(Path.Combine(unreal, "UGameClient-Win64-Shipping.exe"), new byte[100]);
            var unrealDir = Path.Combine(root, "Epic Games", "Unreal");
            var launcher = Directory.CreateDirectory(Path.Combine(root, "com.epicgames.eglv2")).FullName;
            string J(string id, bool complete, string item, string app, string dir, string exe = "", string version = "", string parent = "") => System.Text.Json.JsonSerializer.Serialize(new
            {
                installationId = id, complete, @namespace = "ns" + app, itemId = item, artifactId = app, dir = dir + @"\", platform = "Windows", launchExecutable = exe, version,
                parentInstallationId = parent, writtenAt = "2026-10-14T12:00:00Z",
            });
            File.WriteAllLines(Path.Combine(launcher, "installations.jsonl"),
            [
                """{"version":1,"writtenAt":"2026-10-14T12:00:00Z","entries":6}""",
                J("A", false, "itemA", "Fresh", fresh),
                J("A", true, "itemA", "Fresh", fresh, "Fresh.exe", "1.2.3-CL-100"),   // the last snapshot of an install is current
                J("B", true, "itemB", "Renamed", renamed),                             // not in the catalog cache: named after its folder
                J("C", true, "itemC", "Pending", pending), J("C", false, "itemC", "Pending", pending),
                J("D", true, "itemD", "Gone", gone),
                J("E", true, "itemE", "Addon", fresh),                                 // an add-on in the game's install
                J("F", true, "itemF", "Both", both, "Both.exe", "2.0"),
                J("G", true, "itemG", "UGame", unrealDir, "UGame/Binaries/Win64/UGameBootstrapper.exe", "++UGame+Release-1.0-CL-5-Windows"),
                J("H", true, "itemH", "0123456789abcdef0123456789abcdef", unrealDir, parent: "G"),   // an add-on the catalog cache doesn't name
                "[]", """{"installationId":""",
            ]);
            Directory.CreateDirectory(Path.Combine(launcher, "cache", "prod"));
            File.WriteAllText(Path.Combine(launcher, "cache", "prod", "catalog-cache.json"), """
                { "items": { "itemA": { "item": { "id": "itemA", "title": "Fresh Game", "mainGameItem": null } },
                             "itemE": { "item": { "id": "itemE", "title": "Fresh Game Soundtrack", "mainGameItem": { "id": "itemA" } } },
                             "itemF": { "item": { "id": "itemF", "title": "Both Game" } } } }
                """);
            var manifests = Directory.CreateDirectory(Path.Combine(root, "Manifests")).FullName;
            File.WriteAllText(Path.Combine(manifests, "f.item"), System.Text.Json.JsonSerializer.Serialize(new { AppName = "Both", DisplayName = "Both (classic)", InstallLocation = both, AppVersionString = "1.0" }));
            File.WriteAllText(Path.Combine(manifests, "c.item"), System.Text.Json.JsonSerializer.Serialize(new { AppName = "Pending", InstallLocation = pending }));   // the new launcher's record of it is unfinished

            var games = new EpicSource(manifests, launcher).Discover().OrderBy(g => g.Id).ToList();
            Assert.Equal(["epic:Both", "epic:Fresh", "epic:Renamed", "epic:UGame"], games.Select(g => g.Id));
            Assert.Equal(("Both Game", "2.0"), (games[0].Name, games[0].Version));   // the new launcher's record wins over a stale classic manifest
            Assert.Equal(("Fresh Game", fresh, Path.Combine(fresh, "Fresh.exe"), "1.2.3-CL-100"), (games[1].Name, games[1].InstallDir, games[1].ExePath, games[1].Version));
            Assert.Equal(("Renamed", (string?)null), (games[2].Name, games[2].Version));
            Assert.Equal(Path.Combine(unreal, "UGameClient-Win64-Shipping.exe"), games[3].ExePath);   // never the bootstrapper the launcher starts
            Assert.Equal(["Both (classic)", "Pending"], new EpicSource(manifests, Path.Combine(root, "missing")).Discover().Select(g => g.Name).Order());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void Purple_lists_a_finished_install_with_its_game_exe_and_nc_anti_cheat()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-purple-test-").FullName;
        try
        {
            void Put(string rel, string text = "") { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!); File.WriteAllText(Path.Combine(root, rel), text); }
            Put(@"Game\VersionInfo_G1_WW_PURPLE.xml", "﻿<?xml version=\"1.0\" encoding=\"UTF-8\"?><VersionInfo><Version>25</Version><Updated>1</Updated></VersionInfo>");
            Put(@"Game\Game1\Binaries\Win64\Game1.exe", new string('x', 100));
            Put(@"Game\Engine\Binaries\Win64\CrashReportClient.exe", new string('x', 1000));
            Put(@"Game\Game1\Plugins\NCGuardSDK\Libraries\Win64\bb64.dll");
            Put(@"Downloading\Game2\Binaries\Win64\Game2.exe", "x");   // no VersionInfo yet
            Put(@"Broken\VersionInfo_G3.xml", "<VersionInfo>");        // being rewritten
            Put(@"Broken\Game3\Binaries\Win64\Game3.exe", "x");

            var games = new PurpleSource([("G1_WW_PURPLE", "Game One", Path.Combine(root, "Game")), ("G2", "Game Two", Path.Combine(root, "Downloading")),
                ("G3", "Game Three", Path.Combine(root, "Broken")), ("G4", "Gone", Path.Combine(root, "Missing"))]).Discover();

            var g = Assert.Single(games);
            Assert.Equal(("purple:G1_WW_PURPLE", "Game One", "25"), (g.Id, g.Name, g.Version));
            Assert.Equal(Path.Combine(root, @"Game\Game1\Binaries\Win64\Game1.exe"), g.ExePath);
            Assert.Equal(AntiCheat.Other, GameFiles.DetectAntiCheat(g));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void FindExe_judges_helper_names_inside_the_install_only_and_skips_Source_2s_console()
    {
        var root = Directory.CreateTempSubdirectory("scskiller-exe-test-").FullName;
        try
        {
            foreach (var parent in new[] { "Setup", "Crash Tests", "DirectX Games", "Redist" })
            {
                var install = Directory.CreateDirectory(Path.Combine(root, parent, "Demo")).FullName;
                File.WriteAllBytes(Path.Combine(install, "Game.exe"), new byte[100]);
                Directory.CreateDirectory(Path.Combine(install, "_CommonRedist"));
                File.WriteAllBytes(Path.Combine(install, "_CommonRedist", "vc_redist.x64.exe"), new byte[500]);
                Assert.Equal(Path.Combine(install, "Game.exe"), GameFiles.FindExe(install));
            }
            var cs2 = Directory.CreateDirectory(Path.Combine(root, "Counter-Strike Global Offensive", "game", "bin", "win64")).FullName;
            File.WriteAllBytes(Path.Combine(cs2, "cs2.exe"), new byte[300]);
            File.WriteAllBytes(Path.Combine(cs2, "vconsole2.exe"), new byte[500]);
            Assert.Equal(Path.Combine(cs2, "cs2.exe"), GameFiles.FindExe(Path.Combine(root, "Counter-Strike Global Offensive")));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>appinfo.vdf v28 with one app whose KeyValues nest far deeper than any real entry: that entry is left out,
    /// discovery isn't taken down by a stack overflow.</summary>
    [Fact]
    public void Steam_app_types_survive_absurdly_nested_key_values()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("scskiller-appinfo-test-").FullName, "appinfo.vdf");
        try
        {
            var kv = new MemoryStream();
            for (var i = 0; i < 200_000; i++) kv.Write([0, 0]);   // type 0 (an object), empty key
            var entry = new byte[60 + kv.Length];
            kv.ToArray().CopyTo(entry, 60);
            using (var w = new BinaryWriter(File.Create(path)))
            {
                w.Write(0x07564428u); w.Write(1u);
                w.Write(7u); w.Write((uint)entry.Length); w.Write(entry);
                w.Write(0u);
            }
            Assert.Empty(SteamSource.AppTypes(path, new HashSet<uint> { 7 })!);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    /// <summary>All or nothing: a read-only cache file is deleted with the others, not left after them.</summary>
    [Fact]
    public void Deleting_cache_files_takes_read_only_ones_too()
    {
        var dir = Directory.CreateTempSubdirectory("scskiller-delete-test-").FullName;
        try
        {
            var files = new[] { "a.parc", "b.parc" }.Select(n => Path.Combine(dir, n)).ToList();
            foreach (var f in files) File.WriteAllBytes(f, new byte[10]);
            File.SetAttributes(files[1], FileAttributes.ReadOnly);
            Assert.Equal(2, AppCacheFiles.DeleteAll(files.Select(f => new FileInfo(f)).ToList()));
            Assert.All(files, f => Assert.False(File.Exists(f)));
        }
        finally { Directory.Delete(dir, true); }
    }
}
