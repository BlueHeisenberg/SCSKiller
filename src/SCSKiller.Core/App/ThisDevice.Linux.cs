namespace SCSKiller.Core.App;

public static partial class ThisDevice
{
    public static partial string Kind => KindOf("/sys/class/dmi/id/product_name");

    internal static string KindOf(string productName) => IsDeck(productName) ? "Steam Deck" : "Linux PC";

    /// <summary>SteamOS: its Game Mode has no unlocked keyring, so tokens stay in their 0600 file there.</summary>
    public static bool SteamOs => IsSteamOs("/etc/os-release") ?? IsSteamOs("/usr/lib/os-release") ?? false;

    /// <summary>A Steam Deck's board name: Jupiter (LCD) or Galileo (OLED).</summary>
    internal static bool IsDeck(string productName)
    {
        try { return File.ReadAllText(productName).Trim() is "Jupiter" or "Galileo"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>ID=steamos in an os-release file; null when it can't be read.</summary>
    internal static bool? IsSteamOs(string osRelease)
    {
        try { return File.ReadLines(osRelease).Any(l => l.Split('=', 2) is ["ID", var id] && id.Trim().Trim('"', '\'') == "steamos"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
