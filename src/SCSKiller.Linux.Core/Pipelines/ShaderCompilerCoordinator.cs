using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Games;
using SCSKiller.Linux.Core.Models;
using System.Diagnostics;
using System.Threading.Channels;

namespace SCSKiller.Linux.Core.Pipelines;

public class ShaderCompilerCoordinator
{
    private readonly GameManager _gameManager;
    private readonly IDriverBackend _driverBackend;

    public ShaderCompilerCoordinator(GameManager gameManager, IDriverBackend driverBackend)
    {
        _gameManager = gameManager;
        _driverBackend = driverBackend;
    }

    public static SCSKiller.Core.IEngineReader CreateAllEngineReaders(string appDataDir) =>
        new SCSKiller.Core.Carved.EngineReaders(
            ("Unreal", new SCSKiller.Core.Unreal.UnrealReader(appDataDir)),
            (SCSKiller.Core.FromSoft.FromSoftReader.Family, new SCSKiller.Core.FromSoft.FromSoftReader(appDataDir)),
            (SCSKiller.Core.Unity.UnityReader.Family, new SCSKiller.Core.Unity.UnityReader()),
            (SCSKiller.Core.ReEngine.ReEngineReader.Family, new SCSKiller.Core.ReEngine.ReEngineReader(appDataDir)),
            (SCSKiller.Core.RedEngine.RedEngineReader.Family, new SCSKiller.Core.RedEngine.RedEngineReader()),
            (SCSKiller.Core.Dagor.DagorReader.Family, new SCSKiller.Core.Dagor.DagorReader()),
            (SCSKiller.Core.Northlight.NorthlightReader.Family, new SCSKiller.Core.Northlight.NorthlightReader()),
            (SCSKiller.Core.Carved.CarvedReader.Family, new SCSKiller.Core.Carved.CarvedReader())
        );

    public async IAsyncEnumerable<CompilationProgress> CompileGameShadersAsync(
        GameInfo game,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        int estimatedTotal = game.TotalShadersEstimated > 0 ? game.TotalShadersEstimated : 5000;

        yield return new CompilationProgress(
            GameId: game.Id,
            GameName: game.Name,
            ProcessedShaders: 0,
            TotalShaders: estimatedTotal,
            Percentage: 0,
            CurrentAction: "Preparing shader extraction and D3D12/D3D11 pipeline environment...",
            IsComplete: false,
            Failed: false,
            ErrorMessage: null
        );

        await Task.Delay(50, cancellationToken);

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string appDataDir = SCSKiller.Linux.Core.AppPaths.DataDir;
        string workingDir = Path.Combine(appDataDir, "work", game.Id);
        Directory.CreateDirectory(workingDir);

        // Resolve the real game / Shipping executable using SCSKiller.Core's GameFiles resolver
        string rawExe = !string.IsNullOrEmpty(game.ExecutablePath)
            ? game.ExecutablePath
            : (GameEngineDetector.FindExecutable(game.InstallPath) ?? "");
        if (!string.IsNullOrEmpty(rawExe) && Directory.Exists(game.InstallPath))
        {
            try
            {
                string resolvedExe = SCSKiller.Core.Games.GameFiles.GameExe(game.InstallPath, rawExe);
                if (!string.IsNullOrEmpty(resolvedExe) && File.Exists(resolvedExe))
                {
                    game = game with { ExecutablePath = resolvedExe };
                }
            }
            catch { }
        }

        var driverInfo = await _driverBackend.GetDriverInfoAsync(cancellationToken);
        var caps = driverInfo.Vendor == GpuVendor.Amd
            ? new SCSKiller.Core.VendorCaps(
                "amd-1",
                CacheKeyedByExeName: true,
                StateIndependentCache: false,
                CacheSizeConfigurable: false,
                PerStageCache: true,
                RtCacheGranularity: SCSKiller.Core.RtCacheGranularity.WholeObject)
            : new SCSKiller.Core.VendorCaps(
                "nvidia-1",
                CacheKeyedByExeName: true,
                StateIndependentCache: true,
                CacheSizeConfigurable: true,
                PerStageCache: true,
                RtCacheGranularity: SCSKiller.Core.RtCacheGranularity.Collection,
                PackageKeyed: true);

        string genDbPath = Path.Combine(workingDir, "scskiller_gen.db");
        string recDbPath = Path.Combine(workingDir, "scskiller.db");
        int threads = Math.Max(2, Environment.ProcessorCount);

        yield return new CompilationProgress(
            GameId: game.Id,
            GameName: game.Name,
            ProcessedShaders: 0,
            TotalShaders: estimatedTotal,
            Percentage: 2.0,
            CurrentAction: "Scanning game archives across all supported engines and building compilation plan...",
            IsComplete: false,
            Failed: false,
            ErrorMessage: null
        );

        string? extractError = null;
        int plannedItems = 0;

        if (Directory.Exists(game.InstallPath) &&
            !string.IsNullOrEmpty(game.ExecutablePath) &&
            File.Exists(game.ExecutablePath) &&
            game.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var scsStore = game.Source switch
                {
                    GameSource.Steam => SCSKiller.Core.Store.Steam,
                    GameSource.Heroic => SCSKiller.Core.Store.Epic,
                    _ => SCSKiller.Core.Store.Manual
                };
                var scsGame = new SCSKiller.Core.Game(game.Id, game.Name, scsStore, game.InstallPath, game.ExecutablePath);
                var reader = CreateAllEngineReaders(appDataDir);
                var engineInfo = reader.Detect(scsGame);

                if (engineInfo == null)
                {
                    extractError = "Could not detect a supported shader archive in this game directory.";
                }
                else if (engineInfo.Unsupported != null)
                {
                    extractError = $"Unsupported game archive: {engineInfo.Unsupported}";
                }
                else
                {
                    string packDir = Path.Combine(appDataDir, "packs");
                    string sharedPackDir = Path.Combine(appDataDir, "community", "packs", driverInfo.Vendor == GpuVendor.Amd ? "amd" : "nvidia");
                    var planner = new SCSKiller.Core.Planning.Planner(packDir, sharedPackDir);

                    SCSKiller.Core.Recording? recording = null;
                    try { RecorderManager.Import(game); } catch { }   // the last session's inbox, if the game isn't holding it
                    var index = await Task.Run(() => reader.Index(scsGame, engineInfo, new Progress<string>(), cancellationToken), cancellationToken);

                    // As Windows PrepareRecording: SCSKiller's copy, with every shader it names by hash read back from the install
                    string store = RecorderManager.RecordingPath(game.Id);
                    string rehydrated = Path.Combine(workingDir, "recording.db");
                    try { File.Delete(recDbPath); } catch { }   // Materialize writes it again from the recording
                    if (File.Exists(store) && new FileInfo(store).Length > 0)
                    {
                        await Task.Run(() =>
                        {
                            using (SCSKiller.Core.App.Recordings.Lock(store))
                                SCSKiller.Core.Planning.Rehydrate.Run(store, rehydrated, scsGame, engineInfo, reader, index, null, cancellationToken);
                        }, cancellationToken);
                        recording = new SCSKiller.Core.Recording(rehydrated);
                    }

                    // vkd3d-proton keys its Vulkan pipelines on the whole PSO state (input layout, RT/DS formats, blend, raster):
                    // with the game's draws recorded, plan their exact layouts (ExactLayouts) as on a state-dependent cache.
                    bool hasDraws = recording != null && RecorderManager.HasDraws(recording.DbPath);
                    var planCaps = hasDraws ? caps with { StateIndependentCache = false } : caps;

                    var plan = await Task.Run(() => planner.Build(scsGame, engineInfo, index, recording, planCaps, workingDir, new Progress<string>(), cancellationToken, maximum: true), cancellationToken);
                    await Task.Run(() => planner.Materialize(plan, scsGame, engineInfo, reader, recording, workingDir, cancellationToken), cancellationToken);
                    plannedItems = (int)Math.Min(int.MaxValue, plan.Stats.Recorded + plan.Stats.Generated + plan.Stats.D3D11Shaders + plan.Stats.MiddlewareItems + plan.Stats.Variants);
                    if (plannedItems > 0)
                    {
                        estimatedTotal = plannedItems;
                    }
                }
            }
            catch (Exception ex)
            {
                extractError = ex.Message;
            }
        }

        bool hasDb = (File.Exists(genDbPath) && new FileInfo(genDbPath).Length > 0) ||
                     (File.Exists(recDbPath) && new FileInfo(recDbPath).Length > 0);

        if (!hasDb && extractError != null)
        {
            yield return new CompilationProgress(
                GameId: game.Id,
                GameName: game.Name,
                ProcessedShaders: 0,
                TotalShaders: estimatedTotal,
                Percentage: 0,
                CurrentAction: $"Shader extraction failed: {extractError}",
                IsComplete: true,
                Failed: true,
                ErrorMessage: $"Failed to extract game shaders: {extractError}"
            );
            yield break;
        }

        // Run Proton-powered D3D12/DXR/D3D11 warmer
        yield return new CompilationProgress(
            GameId: game.Id,
            GameName: game.Name,
            ProcessedShaders: 0,
            TotalShaders: estimatedTotal,
            Percentage: 5.0,
            CurrentAction: $"Warming GPU driver pipeline cache via Proton across {threads} CPU threads...",
            IsComplete: false,
            Failed: false,
            ErrorMessage: null
        );

        var channel = Channel.CreateUnbounded<WarmerEvent>();
        var warmerTask = Task.Run(async () =>
        {
            try
            {
                bool ok = await ProtonRunner.RunWarmerAsync(
                    game,
                    workingDir,
                    ev => channel.Writer.TryWrite(ev),
                    cancellationToken);
                channel.Writer.Complete();
                return ok;
            }
            catch (Exception ex)
            {
                channel.Writer.Complete(ex);
                return false;
            }
        }, cancellationToken);

        int done = 0;
        int currentTotal = estimatedTotal;
        int failed = 0;
        string? lastError = null;

        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken))
            {
                while (channel.Reader.TryRead(out var ev))
                {
                    if (ev.Total.HasValue && ev.Total.Value > 0)
                        currentTotal = ev.Total.Value;
                    if (ev.Done.HasValue)
                        done = ev.Done.Value;
                    if (ev.Failed.HasValue)
                        failed = ev.Failed.Value;
                    if (ev.Message != null)
                        lastError = ev.Message;

                    double pct = currentTotal > 0 ? Math.Min(99.0, (double)done / currentTotal * 100.0) : 0;

                    string actionMsg = ev.Event switch
                    {
                        "init" => $"Initializing D3D12/D3D11 device across {ev.Threads ?? threads} CPU threads...",
                        "start" => $"Starting compilation of {currentTotal} game pipeline states...",
                        "progress" => $"Compiling pipelines into driver cache ({done}/{currentTotal})...",
                        "done" => $"Completed compilation: {done} succeeded, {failed} failed.",
                        "error" => $"Warmer error: {ev.Message}",
                        _ => $"Compiling pipelines ({done}/{currentTotal})..."
                    };

                    yield return new CompilationProgress(
                        GameId: game.Id,
                        GameName: game.Name,
                        ProcessedShaders: done,
                        TotalShaders: currentTotal,
                        Percentage: Math.Round(pct, 1),
                        CurrentAction: actionMsg,
                        IsComplete: false,
                        Failed: ev.Event == "error",
                        ErrorMessage: ev.Message
                    );
                }
            }
        }
        finally
        {
            // Clean up large materialized scskiller_gen.db after warming finishes, matching Windows ScsKiller.cs
            try
            {
                if (File.Exists(genDbPath))
                    File.Delete(genDbPath);
            }
            catch { }
        }

        bool success = await warmerTask;

        // Verify newly compiled cache on disk
        var (cacheBytes, cacheFiles, cachePath) = await _driverBackend.GetGameCacheStatsAsync(game.Id, game.Source, game.PrefixPath);

        _gameManager.SaveGameCompiled(game.Id, currentTotal > 0 ? currentTotal : done);

        string finishMessage = cacheBytes > 0
            ? $"Pre-compilation finished! Cached {FormatBytes(cacheBytes)} in GPU driver shader cache ({done} pipelines compiled with 0 stutter)."
            : $"Pre-compilation finished! {done} pipelines compiled into GPU driver cache.";

        yield return new CompilationProgress(
            GameId: game.Id,
            GameName: game.Name,
            ProcessedShaders: currentTotal,
            TotalShaders: currentTotal,
            Percentage: 100.0,
            CurrentAction: finishMessage,
            IsComplete: true,
            Failed: !success && done == 0,
            ErrorMessage: !success && done == 0 ? (lastError ?? "Warmer process exited unexpectedly") : null
        );
    }

    private static string FormatBytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < suffixes.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return $"{d:0.##} {suffixes[i]}";
    }
}
