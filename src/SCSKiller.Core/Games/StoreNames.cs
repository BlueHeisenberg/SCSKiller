namespace SCSKiller.Core.Games;

/// <summary>A game's store as the Library and the CLI name it. Steam's non-Steam games, GOG, Ubisoft, Battle.net, PURPLE,
/// HoYoPlay, GRYPHLINK, WeGame, Gaijin, Riot Client, Faugus and Lutris games are Store.Other: told apart by their id prefix.</summary>
public static class StoreNames
{
    public const string AddedByYou = "Added by you", SteamShortcut = "Steam (non-Steam game)";

    /// <summary>The Library's sections, in list order.</summary>
    public static readonly string[] Sections = ["Steam", SteamShortcut, "Epic Games", "Xbox / Game Pass", "EA app", "GOG", "Ubisoft Connect", "Battle.net", "PURPLE", "HoYoPlay", "GRYPHLINK", "WeGame", "Gaijin", "Riot Client", "Faugus", "Lutris", "Other", AddedByYou];

    public static string Of(Game g) => g.Store switch
    {
        Store.Steam => "Steam", Store.Epic => "Epic Games", Store.Xbox => "Xbox / Game Pass", Store.EA => "EA app", Store.Manual => AddedByYou,
        _ => g.Id[..Math.Max(0, g.Id.IndexOf(':'))] switch { "steam-shortcut" => SteamShortcut, "gog" => "GOG", "ubisoft" => "Ubisoft Connect", "battlenet" => "Battle.net", "purple" => "PURPLE", "hoyoplay" => "HoYoPlay", "gryphline" => "GRYPHLINK", "wegame" => "WeGame", "gaijin" => "Gaijin", "riot" => "Riot Client", "faugus" => "Faugus", "lutris" => "Lutris", _ => "Other" },
    };
}
