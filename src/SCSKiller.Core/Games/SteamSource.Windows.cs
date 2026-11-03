using Microsoft.Win32;

namespace SCSKiller.Core.Games;

public sealed partial class SteamSource
{
    private static partial string? LibraryPath(string root, string path) => path;

    public static partial string Root() => Root(out _);

    public static partial string Root(out string? from) => Root(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string, out from);

    const string DefaultRoot = @"C:\Program Files (x86)\Steam";

    // SteamPath can name a game's bundled Steam folder (platform\steam), which has no steam.exe. None holding one: SteamPath as
    // it is, as the proxy's steam_pick, since dropping it drops the libraries it lists
    internal static string Root(string? steamPath, string? installPath, out string from)
    {
        foreach (var (p, name) in new[] { (steamPath, "SteamPath"), (installPath, "InstallPath"), (DefaultRoot, "default") })
            if (!string.IsNullOrEmpty(p) && File.Exists(Path.Combine(p, "steam.exe")))
            {
                from = name;
                return p;
            }
        from = string.IsNullOrEmpty(steamPath) ? "default, no folder holds steam.exe" : "SteamPath, no folder holds steam.exe";
        return string.IsNullOrEmpty(steamPath) ? DefaultRoot : steamPath;
    }
}
