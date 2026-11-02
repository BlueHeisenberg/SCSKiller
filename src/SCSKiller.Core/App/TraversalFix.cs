using SCSKiller.Core.Vulkan;

namespace SCSKiller.Core.App;

/// <summary>The traversal stutter fix (proxy/traversal.h): the D3D12 recorder places a game's small committed resources in
/// heaps it made ahead. Experimental, off until chosen (<see cref="Settings.Traversal"/>, a game's override).</summary>
public static class TraversalFix
{
    /// <summary>The recorder's traversal_ keys when it's on (traversal.h's defaults for the rest).</summary>
    internal static readonly Dictionary<string, string> Keys = new() { ["suballocate"] = "1", ["uav_buffers"] = "0", ["max_pool_mb"] = "4096" };

    // Windows only: under Proton vkd3d-proton makes the resources, with no WDDM residency wait to remove
    public static bool Available => OperatingSystem.IsWindows();

    public static bool Asks(Settings s) => Available && s.WelcomeSeen && !s.TraversalAsked;
    public static Settings Answered(Settings s, TraversalMode mode) => s with { Traversal = mode, TraversalAsked = true };

    /// <summary>The game's recorder can take it: the D3D12 proxy (not the d3d11 one or the Vulkan layer), in a game it can
    /// record, an anti-cheat game only through its sessions and where they place d3d12.dll.</summary>
    public static bool Applies(GameState s, VendorCaps caps) => Available && !s.Records11 && !(s.Engine is { } e && VulkanPlan.IsVulkan(e))
        && (s.AnyAntiCheat ? (s.RiskEligible || s.OfflineEligible) && ScsKiller.RiskDll(s.Engine, caps) == ScsKiller.Dll12 : s.RecorderSkip == null);

    /// <summary>The setting's answer for the game; never for an anti-cheat game, as Record in all games.</summary>
    public static bool ByDefault(Settings st, GameState s) => !s.AnyAntiCheat && st.Traversal == TraversalMode.All;

    public static bool On(Settings st, GameState s, VendorCaps caps) => Applies(s, caps) && (s.TraversalOverride ?? ByDefault(st, s));

    /// <summary>8 GB of VRAM or less; null when the adapter doesn't say (0).</summary>
    public static bool? LowVram(GpuInfo gpu) => gpu.DedicatedVideoMemory == 0 ? null : gpu.DedicatedVideoMemory < 9UL << 30;   // an 8 GB card reports a little under 8 GiB

    public static string Title => Loc.T("Traversal_Title");
    public static string AskIntro => Loc.T("Traversal_AskIntro");
    public static string AskBody => Loc.T("Traversal_AskBody");
    public static string VramLine => Loc.T("Traversal_VramLine");
    public static string LowVramWarning => Loc.T("Traversal_LowVramWarning");
    public static string AskFooter => Loc.T("Traversal_AskFooter");
    public static string AskAll => Loc.T("Traversal_AskAll");
    public static string AskNotNow => Loc.T("Common_KeepItOff");
    public static string[] SettingsChoices => [Loc.T("Common_Off"), Loc.T("Common_On")];
    public static int SettingsIndex(Settings s) => s.Traversal == TraversalMode.All ? 1 : 0;
    public static TraversalMode SettingsAt(int index) => index == 1 ? TraversalMode.All : TraversalMode.Off;
    public static string SettingsLine => Loc.T("Traversal_SettingsLine");
    public static string PerGameLine => Loc.T("Traversal_PerGameLine");
    public static string GameLine => Loc.T("Traversal_GameLine");
    public static string RecorderOffLine => Loc.T("Traversal_RecorderOffLine");

    /// <summary>The game page's choices: 0 = the setting, 1 = on, 2 = off.</summary>
    public static List<string> GameChoices(Settings st, GameState s) => [ByDefault(st, s) ? Loc.T("Traversal_DefaultOn") : Loc.T("Traversal_DefaultOff"), Loc.T("Common_On"), Loc.T("Common_Off")];
    public static int GameIndex(GameState s) => s.TraversalOverride switch { null => 0, true => 1, false => 2 };
    public static bool? OverrideAt(int index) => index switch { 1 => true, 2 => false, _ => null };

    public static string AntiCheatLine(GameState s) => Loc.T("Traversal_AntiCheat", ScsKiller.AntiCheatName(s));

    /// <summary>The game page's description: the line, the anti-cheat line, and the VRAM line unless <see cref="LowVramWarning"/> shows instead.</summary>
    public static string GameNote(GameState s, GpuInfo gpu) =>
        GameLine + (s.AnyAntiCheat ? " " + AntiCheatLine(s) : s.RecorderEffective ? "" : " " + RecorderOffLine) + (LowVram(gpu) == true ? "" : " " + VramLine);
}
