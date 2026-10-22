using SCSKiller.Linux.Core.Drivers;
using SCSKiller.Linux.Core.Models;
using System.Text.Json;

namespace SCSKiller.Linux.Core.Games;

public class GameManager
{
    private readonly List<IGameSource> _sources;
    private readonly string _stateFilePath;

    public GameManager(IEnumerable<IGameSource>? sources = null)
    {
        _sources = sources?.ToList() ?? new List<IGameSource>
        {
            new SteamLinuxSource(),
            new HeroicLinuxSource(),
            new LutrisLinuxSource()
        };

        string appData = SCSKiller.Linux.Core.AppPaths.DataDir;
        Directory.CreateDirectory(appData);
        _stateFilePath = Path.Combine(appData, "games-cache-state.json");
    }

    public async Task<IReadOnlyList<GameInfo>> ScanAllGamesAsync(IDriverBackend? driverBackend = null, CancellationToken cancellationToken = default)
    {
        var tasks = _sources.Select(s => s.DiscoverGamesAsync(cancellationToken));
        var results = await Task.WhenAll(tasks);

        var allGames = results.SelectMany(r => r).ToList();

        if (driverBackend == null)
        {
            driverBackend = await DriverDetector.DetectActiveBackendAsync(cancellationToken);
        }

        var savedStates = LoadStates();
        var updated = new List<GameInfo>();
        foreach (var g in allGames)
        {
            var (cacheBytes, cacheFiles, _) = await driverBackend.GetGameCacheStatsAsync(g.Id, g.Source, g.PrefixPath);

            // PHYSICAL ON-DISK BYTES ARE THE ONLY SOURCE OF TRUTH.
            // If bytes are 0, it is NEVER cached, regardless of any state file.
            CacheState state;
            int compiledCount;
            DateTime? compiledAt;

            if (cacheBytes > 0)
            {
                state = CacheState.Cached;
                if (savedStates.TryGetValue(g.Id, out var rec))
                {
                    compiledCount = rec.CompiledCount > 0 ? rec.CompiledCount : g.TotalShadersEstimated;
                    compiledAt = rec.CompiledAt;
                }
                else
                {
                    compiledCount = g.TotalShadersEstimated;
                    compiledAt = DateTime.UtcNow;
                }
            }
            else
            {
                state = CacheState.NeedsCompilation;
                compiledCount = 0;
                compiledAt = null;
            }

            bool recorder = false;
            int recorded = 0;
            try
            {
                recorder = RecorderManager.IsInstalled(g);
                RecorderManager.Import(g);   // a finished session's inbox (skipped while the game holds it)
                recorded = RecorderManager.RecordedPsos(g);
            }
            catch { }

            updated.Add(g with
            {
                State = state,
                CompiledShadersCount = compiledCount,
                LastCompiledAt = compiledAt,
                CacheSizeBytes = cacheBytes,
                CachedFilesCount = cacheFiles,
                RecorderInstalled = recorder,
                RecordedPsos = recorded
            });
        }

        return updated;
    }

    public void SaveGameCompiled(string gameId, int compiledCount)
    {
        // Saved for audit history
        var states = LoadStates();
        states[gameId] = new GameStateRecord(CacheState.Cached, compiledCount, DateTime.UtcNow);
        SaveStates(states);
    }

    private Dictionary<string, GameStateRecord> LoadStates()
    {
        if (!File.Exists(_stateFilePath)) return new Dictionary<string, GameStateRecord>();
        try
        {
            var json = File.ReadAllText(_stateFilePath);
            return JsonSerializer.Deserialize<Dictionary<string, GameStateRecord>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    private void SaveStates(Dictionary<string, GameStateRecord> states)
    {
        try
        {
            var json = JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_stateFilePath, json);
        }
        catch { }
    }

    public record GameStateRecord(CacheState State, int CompiledCount, DateTime CompiledAt);
}
