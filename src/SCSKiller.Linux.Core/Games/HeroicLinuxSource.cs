using SCSKiller.Linux.Core.Models;
using System.Text.Json;

namespace SCSKiller.Linux.Core.Games;

public class HeroicLinuxSource : IGameSource
{
    public GameSource SourceType => GameSource.Heroic;

    public async Task<IReadOnlyList<GameInfo>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<GameInfo>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string heroicConfig = Path.Combine(home, ".config", "heroic");

            if (!Directory.Exists(heroicConfig)) return results;

            // 1. Epic Games from legendary installed.json
            string legendaryInstalled = Path.Combine(heroicConfig, "legendaryConfig", "legendary", "installed.json");
            if (File.Exists(legendaryInstalled))
            {
                try
                {
                    var json = File.ReadAllText(legendaryInstalled);
                    using var doc = JsonDocument.Parse(json);
                    foreach (var prop in doc.RootElement.EnumerateObject())
                    {
                        var entry = prop.Value;
                        string? title = entry.TryGetProperty("title", out var t) ? t.GetString() : prop.Name;
                        string? installPath = entry.TryGetProperty("install_path", out var p) ? p.GetString() : null;
                        string? exeName = entry.TryGetProperty("executable", out var e) ? e.GetString() : null;

                        if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath)) continue;

                        string exePath = string.IsNullOrWhiteSpace(exeName)
                            ? (GameEngineDetector.FindExecutable(installPath) ?? string.Empty)
                            : Path.Combine(installPath, exeName);

                        if (!File.Exists(exePath))
                        {
                            exePath = GameEngineDetector.FindExecutable(installPath) ?? string.Empty;
                        }

                        // Try finding prefix in GamesConfig
                        string? prefix = null;
                        string gameConfig = Path.Combine(heroicConfig, "GamesConfig", $"{prop.Name}.json");
                        if (File.Exists(gameConfig))
                        {
                            try
                            {
                                using var cfgDoc = JsonDocument.Parse(File.ReadAllText(gameConfig));
                                if (cfgDoc.RootElement.TryGetProperty("winePrefix", out var wp))
                                {
                                    prefix = wp.GetString();
                                }
                            }
                            catch { }
                        }

                        var (engine, estimated) = GameEngineDetector.Detect(installPath);

                        results.Add(new GameInfo(
                            Id: prop.Name,
                            Name: title ?? prop.Name,
                            Source: GameSource.Heroic,
                            InstallPath: installPath,
                            ExecutablePath: exePath,
                            PrefixPath: prefix,
                            BannerPath: null,
                            Engine: engine,
                            State: CacheState.NeedsCompilation,
                            TotalShadersEstimated: estimated,
                            CompiledShadersCount: 0,
                            LastCompiledAt: null
                        ));
                    }
                }
                catch
                {
                    // Ignore JSON parsing errors
                }
            }

            return results;
        }, cancellationToken);
    }
}
