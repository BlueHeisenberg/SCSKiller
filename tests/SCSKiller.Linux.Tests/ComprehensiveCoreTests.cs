using Microsoft.VisualStudio.TestTools.UnitTesting;
using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Games;
using SCSKiller.Linux.Core.Models;
using SCSKiller.Linux.Core.Pipelines;

namespace SCSKiller.Linux.Tests;

[TestClass]
public class ComprehensiveCoreTests
{
    [TestMethod]
    public async Task Test_DriverDetector_DetectsHardware()
    {
        var backend = await DriverDetector.DetectActiveBackendAsync();
        Assert.IsNotNull(backend, "Driver backend must not be null.");

        var info = await backend.GetDriverInfoAsync();
        Assert.IsNotNull(info, "DriverInfo must not be null.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(info.DeviceName), "DeviceName must not be empty.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(info.DriverVersion), "DriverVersion must not be empty.");
        Assert.IsTrue(Directory.Exists(info.ShaderCachePath), $"Shader cache directory should exist at: {info.ShaderCachePath}");
        Assert.IsTrue(info.CachedFilesCount >= 0, "Cached file count must be non-negative.");

        var envs = backend.GetRecommendedEnvironmentVariables();
        Assert.IsTrue(envs.Contains("SHADER_DISK_CACHE") || envs.Contains("MESA"), "Recommended environment variables must be populated.");
    }

    [TestMethod]
    public async Task Test_GameDiscovery_SteamSource()
    {
        var source = new SteamLinuxSource();
        var games = await source.DiscoverGamesAsync();

        Assert.IsNotNull(games);
        Assert.IsTrue(games.Count > 0, "Steam should discover installed games on this machine.");

        foreach (var game in games)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(game.Id), "Game Id must not be empty.");
            Assert.IsFalse(string.IsNullOrWhiteSpace(game.Name), "Game Name must not be empty.");
            Assert.IsTrue(Directory.Exists(game.InstallPath), $"InstallPath must exist: {game.InstallPath}");
            Assert.AreEqual(GameSource.Steam, game.Source);
        }
    }

    [TestMethod]
    public async Task Test_GameDiscovery_HeroicSource()
    {
        var source = new HeroicLinuxSource();
        var games = await source.DiscoverGamesAsync();

        Assert.IsNotNull(games);
        Assert.IsTrue(games.Count > 0, "Heroic should discover installed games on this machine.");
        var hike = games.FirstOrDefault(g => g.Name.Contains("Short Hike", StringComparison.OrdinalIgnoreCase));
        Assert.IsNotNull(hike, "A Short Hike should be discovered from Heroic.");
        Assert.IsTrue(Directory.Exists(hike.InstallPath), "Install path for A Short Hike must exist.");
    }

    [TestMethod]
    public async Task Test_GameDiscovery_LutrisSource_DoesNotCrash()
    {
        var source = new LutrisLinuxSource();
        var games = await source.DiscoverGamesAsync();
        Assert.IsNotNull(games);
    }

    [TestMethod]
    public async Task Test_GameManager_StrictPhysicalDiskTruth()
    {
        var backend = await DriverDetector.DetectActiveBackendAsync();
        var manager = new GameManager();
        var games = await manager.ScanAllGamesAsync(backend);

        Assert.IsNotNull(games);
        Assert.IsTrue(games.Count >= 10, $"Expected at least 10 games, found {games.Count}");

        foreach (var g in games)
        {
            if (g.CacheSizeBytes > 0)
            {
                Assert.AreEqual(CacheState.Cached, g.State, $"Game {g.Name} with {g.CacheSizeBytes} bytes must be Cached.");
            }
            else
            {
                Assert.AreEqual(CacheState.NeedsCompilation, g.State, $"Game {g.Name} with 0 bytes must NOT be Cached.");
            }
        }
    }

    [TestMethod]
    public void Test_GameEngineDetector_EdgeCases()
    {
        // Nonexistent path
        var (eng1, count1) = GameEngineDetector.Detect("/nonexistent/path/123");
        Assert.AreEqual(GameEngine.Unknown, eng1);
        Assert.AreEqual(0, count1);

        // Null/empty path for executable
        var exe = GameEngineDetector.FindExecutable("/nonexistent/path/123");
        Assert.IsNull(exe);

        // Real Cyberpunk directory test
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string cpPath = Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Cyberpunk 2077");
        if (Directory.Exists(cpPath))
        {
            var (cpEng, cpCount) = GameEngineDetector.Detect(cpPath);
            Assert.AreEqual(GameEngine.RedEngine, cpEng);
            Assert.IsTrue(cpCount > 5000);
        }
    }

    [TestMethod]
    public async Task Test_ShaderCompilerCoordinator_RunsEndToEnd()
    {
        var manager = new GameManager();
        var backend = await DriverDetector.DetectActiveBackendAsync();
        var coordinator = new ShaderCompilerCoordinator(manager, backend);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string workDir = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "work", "test_verification_game");
        Directory.CreateDirectory(workDir);
        string recDb = Path.Combine(workDir, "scskiller.db");
        // Write valid 8-byte SCSK v2 header ("SCSK" + uint32(2)) so scskiller_warm.exe initializes D3D12 device and completes cleanly
        await File.WriteAllBytesAsync(recDb, new byte[] { (byte)'S', (byte)'C', (byte)'S', (byte)'K', 2, 0, 0, 0 });

        var testGame = new GameInfo(
            Id: "test_verification_game",
            Name: "Test Verification Game",
            Source: GameSource.Manual,
            InstallPath: "/tmp",
            ExecutablePath: "/bin/sh",
            PrefixPath: null,
            BannerPath: null,
            Engine: GameEngine.DirectX12,
            State: CacheState.NeedsCompilation,
            TotalShadersEstimated: 100,
            CompiledShadersCount: 0,
            LastCompiledAt: null
        );

        try
        {
            var progressSteps = new List<CompilationProgress>();
            await foreach (var step in coordinator.CompileGameShadersAsync(testGame))
            {
                progressSteps.Add(step);
                Assert.IsFalse(step.Failed, $"Step failed with: {step.ErrorMessage}");
            }

            Assert.IsTrue(progressSteps.Count > 0, "Coordinator must emit progress steps.");
            var last = progressSteps.Last();
            Assert.IsTrue(last.IsComplete, "Final progress step must be marked complete.");
            Assert.AreEqual(100.0, last.Percentage, "Final percentage must be 100%.");
        }
        finally
        {
            try { if (Directory.Exists(workDir)) Directory.Delete(workDir, true); } catch { }
        }
    }

    [TestMethod]
    public void Test_FossilizeLocator_FindsReplayer()
    {
        string? path = FossilizeLocator.FindFossilizeReplay();
        Assert.IsNotNull(path, "fossilize_replay must be found on this system.");
        Assert.IsTrue(File.Exists(path), $"Path reported by locator must exist: {path}");
    }

    [TestMethod]
    public async Task Test_DriverBackend_ClearGameCache_RemovesTargetCache()
    {
        var backend = await DriverDetector.DetectActiveBackendAsync();
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string fakeAppId = "9999999";
        string fakeCacheDir = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache", fakeAppId, "nvidiav1");
        Directory.CreateDirectory(fakeCacheDir);
        string fakeFile = Path.Combine(fakeCacheDir, "test_shader.bin");
        await File.WriteAllBytesAsync(fakeFile, new byte[1024 * 64]);

        try
        {
            // Verify stats before
            var (bytesBefore, filesBefore, _) = await backend.GetGameCacheStatsAsync(fakeAppId, GameSource.Steam);
            Assert.AreEqual(64 * 1024, bytesBefore);
            Assert.AreEqual(1, filesBefore);

            // Clear cache
            await backend.ClearGameCacheAsync(fakeAppId, GameSource.Steam);

            // Verify stats after
            var (bytesAfter, filesAfter, _) = await backend.GetGameCacheStatsAsync(fakeAppId, GameSource.Steam);
            Assert.AreEqual(0L, bytesAfter);
            Assert.AreEqual(0, filesAfter);
        }
        finally
        {
            string rootAppDir = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache", fakeAppId);
            if (Directory.Exists(rootAppDir))
            {
                try { Directory.Delete(rootAppDir, true); } catch { }
            }
        }
    }

    [TestMethod]
    public void Test_UnrealReader_DetectDawnwalker()
    {
        string dawnwalkerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "Steam", "steamapps", "common", "The Blood of Dawnwalker");
        if (!Directory.Exists(dawnwalkerDir)) return;

        string exePath = Path.Combine(dawnwalkerDir, "Dawnwalker", "Binaries", "Win64", "Dawnwalker.exe");
        var game = new SCSKiller.Core.Game("3751260", "The Blood of Dawnwalker", SCSKiller.Core.Store.Steam, dawnwalkerDir, exePath);

        var reader = new SCSKiller.Core.Unreal.UnrealReader();
        var engine = reader.Detect(game, out string notes);

        Assert.IsNotNull(engine, $"UnrealReader should detect Dawnwalker! Notes: {notes}");
        Console.WriteLine($"Dawnwalker Detected: Family={engine.Family}, Version={engine.Version}, Fork={engine.Fork}, Api={engine.GraphicsApi}, Notes={notes}");
    }

    [TestMethod]
    public void Test_Dawnwalker_FullPipeline_PlanAndMaterialize()
    {
        string dawnwalkerDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "share", "Steam", "steamapps", "common", "The Blood of Dawnwalker");
        if (!Directory.Exists(dawnwalkerDir)) return;

        string exePath = Path.Combine(dawnwalkerDir, "Dawnwalker", "Binaries", "Win64", "Dawnwalker.exe");
        var game = new SCSKiller.Core.Game("3751260", "The Blood of Dawnwalker", SCSKiller.Core.Store.Steam, dawnwalkerDir, exePath);

        var reader = new SCSKiller.Core.Unreal.UnrealReader();
        var engine = reader.Detect(game, out string notes);
        Assert.IsNotNull(engine);

        var progress = new Progress<string>(s => Console.WriteLine($"[Index] {s}"));
        var index = reader.Index(game, engine, progress, CancellationToken.None);
        Console.WriteLine($"[Index] Done. Unique shaders: {index.Shaders.Count}, Shader maps: {index.Maps.Count}");

        var caps = new SCSKiller.Core.VendorCaps(
            "nvidia-1",
            CacheKeyedByExeName: true,
            StateIndependentCache: true,
            CacheSizeConfigurable: true,
            PerStageCache: true,
            RtCacheGranularity: SCSKiller.Core.RtCacheGranularity.Collection,
            PackageKeyed: true);
        string workDir = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "work", "3751260");
        Directory.CreateDirectory(workDir);
        string genDb = Path.Combine(workDir, "scskiller_gen.db");

        try
        {
            var planner = new SCSKiller.Core.Planning.Planner();
            var plan = planner.Build(game, engine, index, null, caps, workDir, progress, CancellationToken.None);
            Console.WriteLine($"[Plan] Done. Plan file: {plan.FilePath}, Generated PSOs (PerStageCache=true): {plan.Stats.Generated}, RootSigs: {plan.Stats.RootSignatures}");

            planner.Materialize(plan, game, engine, reader, null, workDir, CancellationToken.None);
            Assert.IsTrue(File.Exists(genDb), "scskiller_gen.db must be generated by Materialize!");
            var fi = new FileInfo(genDb);
            Console.WriteLine($"[Materialize] Done. scskiller_gen.db size: {fi.Length:N0} bytes");
            Assert.IsTrue(fi.Length > 0, "scskiller_gen.db must not be empty!");
        }
        finally
        {
            try { if (File.Exists(genDb)) File.Delete(genDb); } catch { }
        }
    }

    [TestMethod]
    public void Test_ProtonRunner_FinalizeDriverCaches_PromotesReadOnlyBuckets()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "scskiller_test_cache_" + Guid.NewGuid().ToString("N"));
        string vkd3dDir = Path.Combine(tempRoot, "VKD3D_shader_cache");
        string glCacheDir = Path.Combine(tempRoot, "nvidiav1", "GLCache");
        string leafDir = Path.Combine(glCacheDir, "ee579c6f9b8441af518e5e98be76e16b", "ca212eac8b1e670f");
        Directory.CreateDirectory(vkd3dDir);
        Directory.CreateDirectory(leafDir);

        try
        {
            // Simulate explorer.exe 32-byte stub in bucket 0, warmer 1MB cache in bucket 1, and game 256KB cache in bucket 2
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache0.bin"), new byte[32]);
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache0.toc"), new byte[32]);
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache1.bin"), new byte[1024 * 1024]);
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache1.toc"), new byte[4096]);
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache2.bin"), new byte[256 * 1024]);
            File.WriteAllBytes(Path.Combine(leafDir, "steamapp_shader_cache2.toc"), new byte[1024]);

            File.WriteAllBytes(Path.Combine(vkd3dDir, "vkd3d-proton.game.exe.cache.write"), new byte[8192]);

            ProtonRunner.FinalizeDriverCaches(tempRoot, vkd3dDir, glCacheDir);

            Assert.IsFalse(File.Exists(Path.Combine(leafDir, "steamapp_shader_cache0.bin")), "32-byte stub bucket 0 should be deleted");
            Assert.IsTrue(File.Exists(Path.Combine(leafDir, "steamapp_merged_shader_cache.bin")), "Largest bucket should be promoted to steamapp_merged_shader_cache.bin");
            Assert.AreEqual(1024 * 1024, new FileInfo(Path.Combine(leafDir, "steamapp_merged_shader_cache.bin")).Length);
            Assert.IsTrue(File.Exists(Path.Combine(leafDir, "steam_shader_cache.bin")), "Second bucket should be promoted to steam_shader_cache.bin");
            Assert.AreEqual(256 * 1024, new FileInfo(Path.Combine(leafDir, "steam_shader_cache.bin")).Length);
            Assert.IsTrue(File.Exists(Path.Combine(vkd3dDir, "vkd3d-proton.game.exe.cache")), "VKD3D .cache.write should be promoted to .cache");
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}

