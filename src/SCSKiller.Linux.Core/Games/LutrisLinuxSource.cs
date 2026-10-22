using Microsoft.Data.Sqlite;
using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Games;

public class LutrisLinuxSource : IGameSource
{
    public GameSource SourceType => GameSource.Lutris;

    public async Task<IReadOnlyList<GameInfo>> DiscoverGamesAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            var results = new List<GameInfo>();
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string dbPath = Path.Combine(home, ".local", "share", "lutris", "pga.db");

            if (!File.Exists(dbPath)) return results;

            try
            {
                using var connection = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
                connection.Open();

                using var cmd = connection.CreateCommand();
                cmd.CommandText = "SELECT id, slug, name, runner, directory, installed FROM games WHERE installed = 1";

                using var reader = cmd.ExecuteReader();
                while (reader.Read())
                {
                    string id = reader.GetInt64(0).ToString();
                    string slug = reader.IsDBNull(1) ? id : reader.GetString(1);
                    string name = reader.IsDBNull(2) ? slug : reader.GetString(2);
                    string? directory = reader.IsDBNull(4) ? null : reader.GetString(4);

                    if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) continue;

                    string? exe = GameEngineDetector.FindExecutable(directory);
                    if (string.IsNullOrEmpty(exe)) continue;

                    var (engine, estimated) = GameEngineDetector.Detect(directory);

                    string banner = Path.Combine(home, ".local", "share", "lutris", "coverart", $"{slug}.jpg");
                    if (!File.Exists(banner))
                    {
                        banner = Path.Combine(home, ".local", "share", "lutris", "banners", $"{slug}.jpg");
                        if (!File.Exists(banner)) banner = string.Empty;
                    }

                    results.Add(new GameInfo(
                        Id: id,
                        Name: name,
                        Source: GameSource.Lutris,
                        InstallPath: directory,
                        ExecutablePath: exe,
                        PrefixPath: directory,
                        BannerPath: string.IsNullOrEmpty(banner) ? null : banner,
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
                // Ignore DB read errors
            }

            return results;
        }, cancellationToken);
    }
}
