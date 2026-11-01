using SCSKiller.Core.Localization;

namespace SCSKiller.Ui.Models;

/// <summary>Differences between Windows, Linux and macOS the UI code handles: which stores are offered, how games are added
/// by hand, where the launch options line is needed.</summary>
public sealed class AppPlatform
{
    public required string Name { get; init; }
    /// <summary>The stores the scan reads, in the first scan's order.</summary>
    public required IReadOnlyList<string> Stores { get; init; }
    /// <summary>The stores whose game updates have the game read again.</summary>
    public required string UpdatingStores { get; init; }
    /// <summary>The theme choice that follows the system's.</summary>
    public string SystemTheme => OperatingSystem.IsWindows() ? Loc.T("Settings_Theme_Windows") : Loc.T("Settings_Theme_System");
    /// <summary>What builds a game's pipelines besides the driver.</summary>
    public required string Runtime { get; init; }
    /// <summary>Games added by hand and game folders (ManualSource).</summary>
    public bool AddGames { get; init; }
    /// <summary>The Library's line on how to add a game no store lists, where <see cref="AddGames"/> is off; null: none.</summary>
    public string? AddGamesNote { get; init; }
    public bool StartAtSignIn { get; init; }
    /// <summary>The notification-area icon: closing the window can leave SCSKiller running. On Linux only while a
    /// StatusNotifierItem host shows it, otherwise closing quits.</summary>
    public bool Tray { get; init; }
    /// <summary>The driver-update setting's sign-in and idle check, which notifies or compiles (ScheduledTask).</summary>
    public bool DriverUpdateModes { get; init; }
    /// <summary>NVIDIA App's Automatic Shader Compilation, a setting of the Windows driver.</summary>
    public bool AutoShaderCompilation { get; init; }
    /// <summary>The OS's own shader cache of each game (Windows' D3DSCache), which clearing a game's caches deletes too.</summary>
    public bool SystemShaderCache { get; init; }
    /// <summary>Sessions that record games with anti-cheat (Settings.RecordAntiCheatGames, IScsKiller.StartRiskSession).</summary>
    public bool RecordAntiCheatGames { get; init; }
    /// <summary>The empty library's line, read when the library is empty; null: no empty state.</summary>
    public Func<string>? NoGamesNote { get; init; }
    /// <summary>The game page's line for Steam's launch options (IScsKiller.LaunchLineOf), which SCSKiller doesn't write.</summary>
    public bool SteamLaunchLine { get; init; }

    public static readonly AppPlatform Windows = new()
    {
        Name = "Windows", Stores = ["Steam", "Epic", "Xbox", "EA", "GOG", "Ubisoft Connect", "Battle.net", "PURPLE", "HoYoPlay", "Gaijin", "Riot Client"],
        UpdatingStores = "Steam or the Xbox app", Runtime = "Windows",
        AddGames = true, StartAtSignIn = true, Tray = true, DriverUpdateModes = true, AutoShaderCompilation = true, SystemShaderCache = true,
        RecordAntiCheatGames = true,
    };

    /// <summary><paramref name="steam"/>: a Steam is installed, the native one or the Flatpak or Snap one.</summary>
    public static AppPlatform Linux(Func<bool> steam) => new()
    {
        // the flags left off are features Linux doesn't have yet, each turned on once it does
        Name = "Linux", Stores = ["Steam", "Heroic", "Lutris"], UpdatingStores = "Steam", Runtime = "Proton", Tray = true, StartAtSignIn = true, DriverUpdateModes = true,
        NoGamesNote = () => steam() ? Loc.T("Library_NoGames_Linux_Steam") : Loc.T("Library_NoGames_Linux_NoSteam"),
        SteamLaunchLine = true, RecordAntiCheatGames = true,
        // Steam's shortcut gives the game a Proton prefix, which a game added by hand wouldn't have
        AddGamesNote = Loc.T("Library_AddGames_Note_Linux"),
    };

    /// <summary>The UI on the Linux build's core, with no game scan, compile, recorder or Vulkan layer yet.</summary>
    public static readonly AppPlatform MacOS = new()
    {
        Name = "macOS", Stores = ["Steam"], UpdatingStores = "Steam", Runtime = "the game's runtime",
        NoGamesNote = () => Loc.T("Library_NoGames_MacOS"),
    };
}
