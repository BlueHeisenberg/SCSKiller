using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Games;
using SCSKiller.Linux.Core.Models;
using SCSKiller.Linux.Core.Pipelines;

namespace SCSKiller.Linux.Cli;

class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        PrintHeader();

        var driverBackend = await DriverDetector.DetectActiveBackendAsync();
        var gameManager = new GameManager();

        string command = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

        switch (command)
        {
            case "status":
            case "driver":
                return await ShowDriverStatusAsync(driverBackend);

            case "list":
            case "scan":
                return await ListGamesAsync(gameManager, driverBackend);

            case "compile":
                if (args.Length < 2)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Usage: ./run-cli.sh compile <game-id-or-name>");
                    Console.ResetColor();
                    return 1;
                }
                return await CompileGameAsync(gameManager, driverBackend, args[1]);

            case "compile-all":
                return await CompileAllGamesAsync(gameManager, driverBackend);

            case "clear":
            case "clear-game":
                if (args.Length < 2)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine("Usage: ./run-cli.sh clear <game-id-or-name>");
                    Console.WriteLine("Example: ./run-cli.sh clear cyberpunk");
                    Console.ResetColor();
                    return 1;
                }
                return await ClearGameCacheAsync(gameManager, driverBackend, args[1]);

            case "clear-all":
                return await ClearAllCachesAsync(driverBackend);

            case "clear-cache":
                if (args.Length > 1)
                {
                    if (args[1].Equals("--all", StringComparison.OrdinalIgnoreCase) || args[1].Equals("-a", StringComparison.OrdinalIgnoreCase) || args[1].Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        return await ClearAllCachesAsync(driverBackend);
                    }
                    return await ClearGameCacheAsync(gameManager, driverBackend, args[1]);
                }
                return await ClearCacheInteractiveAsync(gameManager, driverBackend);

            case "help":
            default:
                PrintUsage();
                return 0;
        }
    }

    static void PrintHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(@"
╔═══════════════════════════════════════════════════════════════╗
║      Shaderless — Shader Compilation Stutter Killer           ║
║        Kills shader stutter before you play (AMD/NVIDIA)      ║
╚═══════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
    }

    static void PrintUsage()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  ./run-cli.sh status              Show GPU driver and shader cache stats");
        Console.WriteLine("  ./run-cli.sh list                List detected games across Steam, Heroic & Lutris");
        Console.WriteLine("  ./run-cli.sh compile <id|name>   Pre-compile shaders for a specific game");
        Console.WriteLine("  ./run-cli.sh compile-all         Pre-compile shaders for all uncompiled games");
        Console.WriteLine("  ./run-cli.sh clear <id|name>     Purge shader cache for a specific game (e.g. cyberpunk)");
        Console.WriteLine("  ./run-cli.sh clear-all           Purge ALL GPU driver and game shader caches");
        Console.WriteLine("  ./run-cli.sh clear-cache         Interactive cache clearing menu");
        Console.WriteLine();
    }

    static async Task<int> ShowDriverStatusAsync(IDriverBackend backend)
    {
        Console.WriteLine("Detecting GPU and driver caching configuration...\n");
        var info = await backend.GetDriverInfoAsync();

        Console.WriteLine($"GPU Device:           {info.DeviceName}");
        Console.WriteLine($"Driver Stack:         {info.DriverName} (v{info.DriverVersion})");
        Console.WriteLine($"Vendor:               {info.Vendor}");
        Console.WriteLine($"Global Cache Path:    {info.ShaderCachePath}");
        Console.WriteLine($"Global Cached Files:  {info.CachedFilesCount:N0} entries ({FormatBytes(info.CacheSizeBytes)})");
        if (info.SteamShaderCacheSizeBytes > 0)
        {
            Console.WriteLine($"Steam Shader Caches:  {info.SteamShaderCacheFilesCount:N0} entries ({FormatBytes(info.SteamShaderCacheSizeBytes)})");
        }
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Total Cache on Disk:  {FormatBytes(info.TotalCacheSizeBytes)}");
        Console.ResetColor();

        Console.WriteLine($"Status:               {info.Status} {(info.StatusMessage != null ? $"({info.StatusMessage})" : "")}");

        string? replayer = FossilizeLocator.FindFossilizeReplay();
        Console.WriteLine($"Pipeline Replayer:    {(replayer != null ? $"Found ({replayer})" : "Not found in system")}");

        Console.WriteLine("\nRecommended Environment Variables:");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(backend.GetRecommendedEnvironmentVariables());
        Console.ResetColor();

        return 0;
    }

    static async Task<int> ListGamesAsync(GameManager manager, IDriverBackend backend)
    {
        Console.WriteLine("Scanning game libraries (Steam, Heroic, Lutris)...");
        var games = await manager.ScanAllGamesAsync(backend);

        if (games.Count == 0)
        {
            Console.WriteLine("No games discovered in default Steam, Heroic, or Lutris libraries.");
            return 0;
        }

        Console.WriteLine($"\nFound {games.Count} installed games:\n");
        Console.WriteLine(string.Format("{0,-10} {1,-32} {2,-14} {3,-15} {4,-10} {5,-12}", "Source", "Name", "Engine", "Status", "Shaders", "Disk Cache"));
        Console.WriteLine(new string('-', 102));

        foreach (var g in games)
        {
            var color = g.State == CacheState.Cached ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.ForegroundColor = color;
            string cacheStr = g.CacheSizeBytes > 0 ? FormatBytes(g.CacheSizeBytes) : "0 B";
            Console.WriteLine(string.Format("{0,-10} {1,-32} {2,-14} {3,-15} {4,-10} {5,-12}",
                g.Source,
                Truncate(g.Name, 30),
                g.Engine,
                g.State,
                $"~{g.TotalShadersEstimated:N0}",
                cacheStr));
            Console.ResetColor();
        }

        Console.WriteLine("\nRun './run-cli.sh compile <name>' to pre-compile shaders ahead of time.\n");
        return 0;
    }

    static async Task<int> CompileGameAsync(GameManager manager, IDriverBackend backend, string targetQuery)
    {
        var games = await manager.ScanAllGamesAsync(backend);
        var game = games.FirstOrDefault(g =>
            g.Id.Equals(targetQuery, StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains(targetQuery, StringComparison.OrdinalIgnoreCase));

        if (game == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Game matching '{targetQuery}' was not found in installed libraries.");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"Preparing pre-compilation for: {game.Name} ({game.Source})");
        Console.WriteLine($"Engine: {game.Engine} | Estimated Shaders: {game.TotalShadersEstimated:N0}");
        Console.WriteLine($"Game Directory: {game.InstallPath}\n");

        var coordinator = new ShaderCompilerCoordinator(manager, backend);
        CompilationProgress? lastProgress = null;

        await foreach (var progress in coordinator.CompileGameShadersAsync(game))
        {
            lastProgress = progress;
            RenderProgressBar(progress.Percentage, progress.ProcessedShaders, progress.TotalShaders, progress.CurrentAction);
        }

        if (lastProgress?.Failed == true)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n\nERROR: {lastProgress.ErrorMessage ?? lastProgress.CurrentAction}\n");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n\nSUCCESS: Shaders for '{game.Name}' compiled into GPU driver cache!");
        Console.WriteLine("Game will now launch with zero shader-compilation stutters.\n");
        Console.ResetColor();

        return 0;
    }

    static async Task<int> CompileAllGamesAsync(GameManager manager, IDriverBackend backend, CancellationToken cancellationToken = default)
    {
        var games = await manager.ScanAllGamesAsync(backend, cancellationToken);
        var uncompiled = games.Where(g => g.State != CacheState.Cached).ToList();

        if (uncompiled.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("All detected games are already cached and up to date!");
            Console.ResetColor();
            return 0;
        }

        Console.WriteLine($"Starting batch compilation for {uncompiled.Count} games...\n");
        foreach (var game in uncompiled)
        {
            await CompileGameAsync(manager, backend, game.Id);
        }

        return 0;
    }

    static async Task<int> ClearGameCacheAsync(GameManager manager, IDriverBackend backend, string targetQuery)
    {
        var games = await manager.ScanAllGamesAsync(backend);
        var game = games.FirstOrDefault(g =>
            g.Id.Equals(targetQuery, StringComparison.OrdinalIgnoreCase) ||
            g.Name.Contains(targetQuery, StringComparison.OrdinalIgnoreCase));

        if (game == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Game matching '{targetQuery}' was not found in installed libraries.");
            Console.ResetColor();
            return 1;
        }

        var (bytes, files, cachePath) = await backend.GetGameCacheStatsAsync(game.Id, game.Source, game.PrefixPath);
        Console.WriteLine($"Target game:         {game.Name} ({game.Source}, AppID: {game.Id})");
        Console.WriteLine($"Cache location:      {(string.IsNullOrEmpty(cachePath) ? "N/A" : cachePath)}");
        Console.WriteLine($"Current cache size:  {FormatBytes(bytes)} ({files:N0} files)");

        if (bytes == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("Shader cache is already 0 B (empty). Nothing to clean.");
            Console.ResetColor();
            return 0;
        }

        Console.Write($"\nAre you sure you want to delete shader cache for '{game.Name}'? (y/N): ");
        string? confirm = Console.ReadLine();
        if (confirm?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true)
        {
            Console.WriteLine("Operation cancelled.");
            return 0;
        }

        Console.WriteLine("Deleting shader cache...");
        await backend.ClearGameCacheAsync(game.Id, game.Source, game.PrefixPath);

        // Verify on disk
        var (afterBytes, _, _) = await backend.GetGameCacheStatsAsync(game.Id, game.Source, game.PrefixPath);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nSUCCESS: Shader cache for '{game.Name}' has been purged from disk!");
        Console.WriteLine($"Space freed: {FormatBytes(bytes)}");
        Console.WriteLine($"New state:   NeedsCompilation ({FormatBytes(afterBytes)})");
        Console.ResetColor();
        return 0;
    }

    static async Task<int> ClearAllCachesAsync(IDriverBackend backend)
    {
        var info = await backend.GetDriverInfoAsync();
        Console.WriteLine($"Total cache detected on disk: {FormatBytes(info.TotalCacheSizeBytes)}");
        Console.WriteLine($"  - Global Driver Cache:      {FormatBytes(info.CacheSizeBytes)} ({info.ShaderCachePath})");
        if (info.SteamShaderCacheSizeBytes > 0)
        {
            Console.WriteLine($"  - Steam Game Shader Caches: {FormatBytes(info.SteamShaderCacheSizeBytes)}");
        }

        Console.Write("\nAre you sure you want to purge ALL GPU driver and Steam game shader caches? (y/N): ");
        string? confirm = Console.ReadLine();
        if (confirm?.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) != true)
        {
            Console.WriteLine("Operation cancelled.");
            return 0;
        }

        Console.WriteLine("Purging all driver and game shader caches from disk...");
        await backend.ClearAllCachesAsync();

        var afterInfo = await backend.GetDriverInfoAsync();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nSUCCESS: All shader caches successfully purged from disk!");
        Console.WriteLine($"Remaining cache size: {FormatBytes(afterInfo.TotalCacheSizeBytes)}");
        Console.ResetColor();
        return 0;
    }

    static async Task<int> ClearCacheInteractiveAsync(GameManager manager, IDriverBackend backend)
    {
        Console.WriteLine("Select cache purge option:");
        Console.WriteLine("  1) Clear ALL GPU driver and Steam game shader caches");
        Console.WriteLine("  2) Clear shader cache for a specific game");
        Console.WriteLine("  3) Clear only global driver cache");
        Console.WriteLine("  q) Cancel");
        Console.Write("\nEnter choice (1-3, q): ");
        string? choice = Console.ReadLine()?.Trim();

        switch (choice)
        {
            case "1":
                return await ClearAllCachesAsync(backend);
            case "2":
                Console.Write("Enter game name or App ID: ");
                string? query = Console.ReadLine()?.Trim();
                if (string.IsNullOrEmpty(query))
                {
                    Console.WriteLine("No game specified. Cancelled.");
                    return 0;
                }
                return await ClearGameCacheAsync(manager, backend, query);
            case "3":
                Console.WriteLine("Purging global driver cache...");
                await backend.ClearCacheAsync();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("Global driver cache cleared.");
                Console.ResetColor();
                return 0;
            default:
                Console.WriteLine("Cancelled.");
                return 0;
        }
    }

    static void RenderProgressBar(double percentage, int current, int total, string action)
    {
        int barWidth = 30;
        int filled = (int)Math.Round((percentage / 100.0) * barWidth);
        string bar = new string('█', Math.Min(barWidth, filled)) + new string('░', Math.Max(0, barWidth - filled));

        Console.Write($"\r[{bar}] {percentage,5:F1}% ({current}/{total}) — {Truncate(action, 45)}");
    }

    static string FormatBytes(long bytes)
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

    static string Truncate(string val, int maxLen)
    {
        if (string.IsNullOrEmpty(val)) return string.Empty;
        return val.Length <= maxLen ? val : val.Substring(0, maxLen - 3) + "...";
    }
}
