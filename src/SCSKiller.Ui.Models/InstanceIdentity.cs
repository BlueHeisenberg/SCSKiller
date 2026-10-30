using System.Security.Cryptography;
using System.Text;

namespace SCSKiller.Ui.Models;

/// <summary>The single-instance key and the toast identity (AUMID) of this process. Only a Release build on the default
/// data folder is the installed app: it keeps the WinUI app's key, so a launch during an upgrade from it still finds the
/// running one, and the AUMID Velopack gives an installed app (null: none set here). Anything else (a dev build, a run on
/// SCSKILLER_DATA_DIR) gets an identity of its own per data folder, so it never redirects into the installed app, is never
/// redirected into it, and never registers over its toasts. Windows scopes a key to the exe's path: the installed app keeps
/// its path across an upgrade.</summary>
/// <param name="GameFolders">May manage recorders in game folders while it holds the key: not a dev build on the default
/// data folder, which the installed app (another exe, so another key scope) manages.</param>
/// <param name="Folder">The data folder's own name, the same for every exe and identity on it.</param>
public sealed record InstanceIdentity(string Key, string? Aumid, bool GameFolders, string Folder)
{
    public const string ProductionKey = "SCSKiller";
    /// <summary>The argument Windows starts the exe with when a toast is clicked while no instance is registered.</summary>
    public const string ToastRelaunchArg = "----AppNotificationActivated:";

    public bool Production => Aumid == null;

    /// <summary><paramref name="moved"/>: SCSKILLER_DATA_DIR's folder (<c>AppStore.Moved</c>), <paramref name="dataDir"/> the
    /// data folder in use.</summary>
    public static InstanceIdentity For(bool release, string? moved, string dataDir)
    {
        var folder = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDir)).ToUpperInvariant())))[..16];
        if (release && moved == null) return new(ProductionKey, null, true, folder);
        var id = "SCSKiller.Test." + folder;
        return new(id, id, moved != null, folder);
    }

    /// <summary>A toast clicked after a test run quit: Windows starts the exe without the SCSKILLER_DATA_DIR it ran with, so
    /// it would open the default data folder, and a Release build would take the installed app's key and redirect the click
    /// into it. Only the installed app (<paramref name="installed"/>, read only for such a start) starts from a toast there.</summary>
    public static bool RefusesStart(bool release, string? moved, Func<bool> installed, IEnumerable<string> args) =>
        moved == null && args.Any(a => a.StartsWith(ToastRelaunchArg, StringComparison.Ordinal)) && !(release && installed());
}
