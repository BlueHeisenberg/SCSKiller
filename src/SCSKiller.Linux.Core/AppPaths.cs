namespace SCSKiller.Linux.Core;

/// <summary>Shaderless's data folder (~/.local/share/shaderless). The first use moves the folder of builds from before the
/// rename (~/.local/share/scskiller-linux) there, recordings, plans and prefixes included.</summary>
public static class AppPaths
{
    public const string AppName = "Shaderless";
    static readonly Lazy<string> _dataDir = new(Resolve);
    public static string DataDir => _dataDir.Value;

    static string Resolve()
    {
        var share = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var dir = Path.Combine(share, "shaderless");
        var old = Path.Combine(share, "scskiller-linux");
        try
        {
            if (!Directory.Exists(dir) && Directory.Exists(old)) Directory.Move(old, dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return old; }   // left where it is: still used
        Directory.CreateDirectory(dir);
        return dir;
    }
}
