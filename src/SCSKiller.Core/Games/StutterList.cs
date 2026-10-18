using System.Globalization;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>Games known to suffer shader-compilation stutter: known-stutter.json, embedded in Core (the default) and
/// served at /v1/content/known-stutter.json so it can change without a release.
/// Matched by store id (Game.Id), else by name (letters and digits only, case-insensitive: "STAR WARS Jedi: Survivor™"
/// = "Star Wars Jedi Survivor"). The server's copy replaces the embedded one when it passes <see cref="TryParse"/>
/// (<see cref="ContentFile"/>).</summary>
public sealed class StutterList
{
    public const string ContentPath = "v1/content/known-stutter.json";
    /// <summary>The source of an entry SCSKiller measured itself; any other source is an https URL.</summary>
    public const string OwnMeasurement = "SCSKiller measurement";
    public const int MaxBytes = ContentFile.MaxBytes, MaxGames = 200;
    static readonly string[] Stores = ["steam", "xbox", "epic", "ea", "gog", "ubisoft", "battlenet", "purple", "hoyoplay", "gaijin", "riot"];

    sealed record Entry(string Name, string[] Ids, string Severity, string Reason, string Source, string Date);
    sealed record ListFile(Entry[] Games, string? About = null);

    readonly Dictionary<string, KnownStutter> byId = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, KnownStutter> byName = [];

    public int Count { get; }

    StutterList(ListFile file)
    {
        foreach (var e in file.Games)
        {
            var k = new KnownStutter(e.Severity == "severe" ? StutterSeverity.Severe : StutterSeverity.Moderate, e.Reason, e.Source, e.Date);
            foreach (var id in e.Ids) byId[id] = k;
            byName[Key(e.Name)] = k;
        }
        Count = file.Games.Length;
    }

    /// <summary>The list if the document is valid JSON of the right shape (no unknown fields) within the caps, every
    /// severity "severe" or "moderate", every id a known store's, every source an https URL or <see cref="OwnMeasurement"/>;
    /// else null.</summary>
    public static StutterList? TryParse(string json) =>
        ContentFile.Read<ListFile>(json) is { Games.Length: >= 1 and <= MaxGames } f && (f.About == null || Text(f.About, 1000)) && f.Games.All(Valid) ? new(f) : null;

    static bool Valid(Entry? e) => e is not null &&
        Text(e.Name, 100) && Key(e.Name).Length > 0 && Text(e.Reason, 300)
        && e.Ids is { Length: <= 8 } && e.Ids.All(IsId)
        && e.Severity is "severe" or "moderate"
        && (e.Source == OwnMeasurement || e.Source is { Length: <= 500 } && IsHttps(e.Source))
        && DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    static bool Text(string? s, int max) => ContentFile.Text(s, max);

    internal static bool IsId(string? id) =>
        id is { Length: <= 120 } && id.IndexOf(':') is var c and > 0 && Stores.Contains(id[..c]) && id.Length > c + 1
        && id[(c + 1)..].All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_' or '-')
        && (id[..c] != "steam" || id[(c + 1)..].All(char.IsAsciiDigit));

    internal static bool IsHttps(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort && u.UserInfo.Length == 0;

    public static StutterList Embedded { get => field ??= TryParse(ContentFile.Embedded("SCSKiller.Core.Games.known-stutter.json"))!; }

    /// <summary>The list in use: the embedded one unless replaced.</summary>
    public static StutterList Current { get => field ??= Embedded; set; }

    public static StutterList? Cached(string file) => ContentFile.Cached(file, TryParse);

    public static Task<StutterList?> FetchAsync(string cacheFile, RouteFailover? routes = null, TimeSpan? budget = null) =>
        ContentFile.FetchAsync(ContentPath, cacheFile, TryParse, routes, budget);

    public KnownStutter? Find(Game game) => byId.GetValueOrDefault(game.Id) ?? byName.GetValueOrDefault(Key(game.Name));

    /// <summary>The Library's "Recommended" section: every listed game SCSKiller can compile (Ready, Stale, NeedsRecording,
    /// Warmed), the ones still to do first, then severe first, then by name. Warmed ones stay, last.
    /// Anti-cheat-blocked games are Unsupported, and one waiting for an offline session (a ban risk) isn't recommended either.</summary>
    public static List<GameState> Recommended(IEnumerable<GameState> games) => games
        .Where(s => s.KnownStutter != null && s.Status is GameStatus.Ready or GameStatus.Stale or GameStatus.NeedsRecording or GameStatus.Warmed && !App.ScsKiller.NeedsOfflineSession(s))
        .OrderBy(s => s.Status == GameStatus.Warmed).ThenByDescending(s => s.KnownStutter!.Severity)
        .ThenBy(s => s.Game.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    static string Key(string name) => string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
