namespace SCSKiller.Linux.Core.Pipelines;

public static class FossilizeLocator
{
    public static string? FindFossilizeReplay()
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var candidatePaths = new[]
        {
            "/usr/bin/fossilize-replay",
            "/usr/local/bin/fossilize-replay",
            "/usr/bin/fossilize_replay",
            Path.Combine(home, ".local", "share", "Steam", "ubuntu12_64", "fossilize_replay"),
            Path.Combine(home, ".local", "share", "Steam", "steamrt64", "fossilize_replay"),
            Path.Combine(home, ".steam", "steam", "ubuntu12_64", "fossilize_replay"),
            Path.Combine(home, ".steam", "steam", "steamrt64", "fossilize_replay"),
            Path.Combine(home, ".var", "app", "com.valvesoftware.Steam", "data", "Steam", "ubuntu12_64", "fossilize_replay")
        };

        foreach (var path in candidatePaths)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv != null)
        {
            foreach (var dir in pathEnv.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                string target = Path.Combine(dir, "fossilize-replay");
                if (File.Exists(target)) return target;
                target = Path.Combine(dir, "fossilize_replay");
                if (File.Exists(target)) return target;
            }
        }

        return null;
    }
}
