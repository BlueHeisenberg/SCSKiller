using Microsoft.Win32;

namespace SCSKiller.Core.Games;

public sealed partial class SteamSource
{
    private static partial string? LibraryPath(string root, string path) => path;

    public static partial string Root() => Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string ?? @"C:\Program Files (x86)\Steam";
}
