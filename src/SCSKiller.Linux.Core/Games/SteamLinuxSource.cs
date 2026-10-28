using SCSKiller.Linux.Core.Models;
using System.Text.RegularExpressions;

namespace SCSKiller.Linux.Core.Games;

public class SteamLinuxSource : IGameSource
{
    public GameSource SourceType => GameSource.Steam;

    public async Task<IReadOnlyList<GameInfo>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<GameInfo>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var steamBases = new[]
            {
                Path.Combine(home, ".local", "share", "Steam"),
                Path.Combine(home, ".steam", "steam"),
                Path.Combine(home, ".steam", "root")
            };

            string? steamRoot = steamBases.FirstOrDefault(Directory.Exists);
            if (steamRoot == null) return results;

            var libraryFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            libraryFolders.Add(steamRoot);

            string vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(vdfPath))
            {
                var content = File.ReadAllText(vdfPath);
                var matches = Regex.Matches(content, @"""path""\s+""([^""]+)""");
                foreach (Match m in matches)
                {
                    if (m.Groups.Count > 1 && Directory.Exists(m.Groups[1].Value))
                    {
                        libraryFolders.Add(m.Groups[1].Value);
                    }
                }
            }

            foreach (var library in libraryFolders)
            {
                string steamapps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamapps)) continue;

                var manifestFiles = Directory.GetFiles(steamapps, "appmanifest_*.acf");
                foreach (var manifest in manifestFiles)
                {
                    try
                    {
                        var text = File.ReadAllText(manifest);
                        string? appId = Regex.Match(text, @"""appid""\s+""([^""]+)""").Groups[1].Value;
                        string? name = Regex.Match(text, @"""name""\s+""([^""]+)""").Groups[1].Value;
                        string? installDir = Regex.Match(text, @"""installdir""\s+""([^""]+)""").Groups[1].Value;

                        if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(installDir)) continue;

                        // Skip Steam runtimes and common redistributables
                        if (installDir.StartsWith("SteamLinuxRuntime", StringComparison.OrdinalIgnoreCase) ||
                            installDir.StartsWith("Proton", StringComparison.OrdinalIgnoreCase) ||
                            installDir.Equals("Steamworks Shared", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string gamePath = Path.Combine(steamapps, "common", installDir);
                        if (!Directory.Exists(gamePath)) continue;

                        string prefixPath = Path.Combine(steamapps, "compatdata", appId, "pfx");
                        if (!Directory.Exists(prefixPath))
                        {
                            prefixPath = string.Empty;
                        }

                        string? exe = GameEngineDetector.FindExecutable(gamePath);
                        if (string.IsNullOrEmpty(exe)) continue;

                        var (engine, estimatedShaders) = GameEngineDetector.Detect(gamePath);

                        // Locate banner artwork
                        string banner = Path.Combine(steamRoot, "appcache", "librarycache", $"{appId}_library_600x900.jpg");
                        if (!File.Exists(banner))
                        {
                            banner = Path.Combine(steamRoot, "appcache", "librarycache", $"{appId}_header.jpg");
                            if (!File.Exists(banner)) banner = string.Empty;
                        }

                        results.Add(new GameInfo(
                            Id: appId,
                            Name: string.IsNullOrWhiteSpace(name) ? installDir : name,
                            Source: GameSource.Steam,
                            InstallPath: gamePath,
                            ExecutablePath: exe,
                            PrefixPath: string.IsNullOrEmpty(prefixPath) ? null : prefixPath,
                            BannerPath: string.IsNullOrEmpty(banner) ? null : banner,
                            Engine: engine,
                            State: CacheState.NeedsCompilation,
                            TotalShadersEstimated: estimatedShaders,
                            CompiledShadersCount: 0,
                            LastCompiledAt: null
                        ));
                    }
                    catch
                    {
                        // Ignore individual manifest parsing errors
                    }
                }
            }

            return results;
        }, cancellationToken);
    }
}
