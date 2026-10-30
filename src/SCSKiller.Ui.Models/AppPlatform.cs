namespace SCSKiller.Ui.Models;

/// <summary>What the app offers where the OSes differ, as the view models show it: the UI sets <see cref="Host.Platform"/>
/// from its Platform/Features.*.cs, a test sets its own. The stores and GPUs are ScsKiller.CreateDefault's and
/// GpuBackends.For's on that OS.</summary>
public sealed record AppPlatform
{
    /// <summary>The OS, as the texts name it.</summary>
    public required string Name { get; init; }
    /// <summary>The stores the scan reads, in the first scan's order.</summary>
    public required IReadOnlyList<string> Stores { get; init; }
    /// <summary>The stores whose game updates have the game read again.</summary>
    public required string UpdatingStores { get; init; }
    /// <summary>The theme choice that follows the system's.</summary>
    public required string SystemTheme { get; init; }
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
    /// <summary>The Library's line for the "When idle" rebuild after a driver update.</summary>
    public string IdleRebuildNote { get; init; } = "Rebuilds on its own while you're away from the PC";
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
        UpdatingStores = "Steam or the Xbox app", SystemTheme = "Use Windows setting", Runtime = "Windows",
        AddGames = true, StartAtSignIn = true, Tray = true, DriverUpdateModes = true, AutoShaderCompilation = true, SystemShaderCache = true,
        RecordAntiCheatGames = true,
    };

    /// <summary><paramref name="steam"/>: a Steam is installed, the native one or the Flatpak or Snap one.</summary>
    public static AppPlatform Linux(Func<bool> steam) => new()
    {
        // the flags left off are features Linux doesn't have yet, each turned on once it does
        Name = "Linux", Stores = ["Steam", "Heroic", "Lutris", "Faugus"], UpdatingStores = "Steam", SystemTheme = "Use system setting", Runtime = "Proton", Tray = true, StartAtSignIn = true, DriverUpdateModes = true,
        // no input idle time: the background compile starts at once and pauses only for a game
        IdleRebuildNote = "Rebuilds in the background and pauses while a game runs",
        NoGamesNote = () => steam() ? "SCSKiller lists the Windows games you installed with Steam, Heroic, Lutris or Faugus, and the ones you added to Steam with Proton."
            : "SCSKiller lists the Windows games of Steam, Heroic, Lutris and Faugus.",
        SteamLaunchLine = true, RecordAntiCheatGames = true,
        // Steam's shortcut gives the game a Proton prefix, which a game added by hand wouldn't have
        AddGamesNote = "To add another Windows game, use Add a Non-Steam Game in Steam. SCSKiller lists it at the next scan.",
    };

    /// <summary>The UI on the Linux build's core, with no game scan, compile, recorder or Vulkan layer yet.</summary>
    public static readonly AppPlatform MacOS = new()
    {
        Name = "macOS", Stores = ["Steam"], UpdatingStores = "Steam", SystemTheme = "Use system setting", Runtime = "the game's runtime",
        NoGamesNote = () => "Finding and compiling games isn't available on macOS yet.",
    };
}
