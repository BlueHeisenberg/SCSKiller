using System.Globalization;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>Games that build all their shaders at the main menu or a loading screen: precompiles-at-boot.json, embedded
/// in Core and served at /v1/content/precompiles-at-boot.json. A listed game
/// is Unsupported with <see cref="Reason"/>. Matched by store id only. The server's copy replaces the embedded one when it
/// passes <see cref="TryParse"/> (<see cref="ContentFile"/>).</summary>
public sealed class BootPrecompiles
{
    public const string ContentPath = "v1/content/precompiles-at-boot.json";
    public const string Reason = "builds its shaders at the main menu. In-game stutter isn't from shader compilation";
    public const int MaxGames = 200;

    sealed record Entry(string Name, string[] Ids, string Source, string Date);
    sealed record ListFile(Entry[] Games, string? About = null);

    readonly HashSet<string> ids = new(StringComparer.OrdinalIgnoreCase);

    BootPrecompiles(ListFile file)
    {
        foreach (var e in file.Games) ids.UnionWith(e.Ids);
    }

    /// <summary>The list if the document is valid JSON of the right shape (no unknown fields) within the caps, every
    /// entry with 1..8 known stores' ids and an https source; else null.</summary>
    public static BootPrecompiles? TryParse(string json) =>
        ContentFile.Read<ListFile>(json) is { Games.Length: >= 1 and <= MaxGames } f && (f.About == null || ContentFile.Text(f.About, 1000)) && f.Games.All(Valid) ? new(f) : null;

    static bool Valid(Entry? e) => e is not null &&
        ContentFile.Text(e.Name, 100) && e.Ids is { Length: >= 1 and <= 8 } && e.Ids.All(StutterList.IsId)
        && e.Source is { Length: <= 500 } && StutterList.IsHttps(e.Source)
        && DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static BootPrecompiles Embedded { get => field ??= TryParse(ContentFile.Embedded("SCSKiller.Core.Games.precompiles-at-boot.json"))!; }

    /// <summary>The list in use: the embedded one unless replaced.</summary>
    public static BootPrecompiles Current { get => field ??= Embedded; set; }

    public static BootPrecompiles? Cached(string file) => ContentFile.Cached(file, TryParse);

    public static Task<BootPrecompiles?> FetchAsync(string cacheFile, RouteFailover? routes = null, TimeSpan? budget = null) =>
        ContentFile.FetchAsync(ContentPath, cacheFile, TryParse, routes, budget);

    public bool Has(Game game) => ids.Contains(game.Id);
}
