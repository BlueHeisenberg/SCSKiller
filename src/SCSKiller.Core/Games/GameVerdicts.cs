using System.Globalization;
using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>Verdicts on single games from game-verdicts.json. The embedded copy is the plain document; the server's travel
/// signed (<see cref="ContentTrust"/>, name "game-verdicts"): the public copy at /v1/content/game-verdicts.json and alpha's
/// and beta's at /v1/updates/&lt;channel&gt;/game-verdicts.json behind the access token. A channel's copy that verifies wins,
/// even over a newer public one, so an entry only in the public copy reaches only stable.
/// "no-stutter": the game prepares its shaders itself, so SCSKiller never suggests compiling it (no bulk add, no
/// driver-update rebuild, no recorder by default) but still allows it. "unsupported-yet" and "unsupported": the game is
/// Unsupported ("Not supported yet", "Not supported"). "no-anti-cheat": a WeGame game confirmed to carry no anti-cheat,
/// the only kind whose clean folder lets a recorder in (<see cref="GameFiles.WeGameId"/>). "anti-cheat": the game counts as
/// one with anti-cheat, by store id and by the whole, case-insensitive names of its optional "exes" (<see cref="GameFiles.DetectAntiCheat"/>).
/// A served list only adds to the markers, the built-in lists and the embedded list's entries, and engines or builds don't limit such an entry. Each entry names a reason code the app has its own line for
/// (<see cref="Reasons"/>); an unknown code shows the entry's note, else a line for its verdict. Matched by store id, and
/// only within the entry's engines and builds when it lists them. Read leniently, so later versions can add to the format:
/// unknown top-level members are ignored, and an entry with an unknown member or verdict, or one this version finds
/// invalid, is skipped.</summary>
public sealed class GameVerdicts
{
    public const string Name = "game-verdicts", FileName = Name + ".json";
    public const string NoStutterVerdict = "no-stutter", UnsupportedYetVerdict = "unsupported-yet", UnsupportedVerdict = "unsupported";
    public const string NoAntiCheatVerdict = "no-anti-cheat", AntiCheatVerdict = "anti-cheat";
    public const string OtherReason = "other";
    public const int MaxGames = 500;
    static readonly string[] Verdicts = [NoStutterVerdict, UnsupportedYetVerdict, UnsupportedVerdict, NoAntiCheatVerdict, AntiCheatVerdict];
    static readonly string[] Members = ["name", "ids", "verdict", "reason", "note", "source", "date", "engines", "builds", "exes"];

    /// <summary>The reason codes this version shows in its own words. Codes are [a-z0-9-]; "other" takes the entry's note.</summary>
    public static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>
    {
        ["engine-precompiles"] = "Its engine prepares shaders at startup",
        ["driver-cache-ignored"] = "The driver doesn't use a compile's cache for this game",
        ["anti-cheat"] = "The game contains anti-cheat",
        ["crashes"] = "Compiling crashes this game or the driver",
        ["engine-unreadable"] = "SCSKiller can't read this game's shaders yet",
    };

    /// <summary>A verdict for one game: what it is and the line the app shows.</summary>
    public sealed record Verdict(string Kind, string Text);

    sealed record Entry(string Name, string[] Ids, string Verdict, string Reason, string Source, string Date,
        string? Note = null, string[]? Engines = null, string[]? Builds = null, string[]? Exes = null);

    readonly Dictionary<string, List<Entry>> byId = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> antiCheat = [];   // ids and exe names: only an id has a ':'

    /// <summary>The entries this version applies.</summary>
    public int Count { get; }

    GameVerdicts(List<Entry> entries)
    {
        foreach (var e in entries)
            foreach (var id in e.Ids)
                (byId.TryGetValue(id, out var l) ? l : byId[id] = []).Add(e);
        antiCheat.AddRange(entries.Where(e => e.Verdict == AntiCheatVerdict).SelectMany(e => e.Ids.Concat(e.Exes ?? [])));
        Count = entries.Count;
    }

    /// <summary>The list, or null when the document isn't an object within <see cref="ContentFile.MaxBytes"/> whose
    /// "games" is an array of at most <see cref="MaxGames"/>. The plain document: a signed one is checked by
    /// <see cref="ContentTrust"/> first.</summary>
    public static GameVerdicts? TryParse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > ContentFile.MaxBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("games", out var games)
                || games.ValueKind != JsonValueKind.Array || games.GetArrayLength() > MaxGames) return null;
            return new(games.EnumerateArray().Select(Read).OfType<Entry>().ToList());
        }
        catch (JsonException) { return null; }
    }

    static Entry? Read(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.EnumerateObject().All(p => Members.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
        && ContentFile.Read<Entry>(e) is { } entry && Valid(entry) ? entry : null;

    // an unknown reason code is fine: it shows the note
    static bool Valid(Entry e) =>
        Verdicts.Contains(e.Verdict)
        && ContentFile.Text(e.Name, 100) && e.Ids is { Length: >= 1 and <= 8 } && e.Ids.All(IsId)
        && e.Reason is { Length: >= 1 and <= 40 } && e.Reason.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
        && (e.Note == null || ContentFile.Text(e.Note, 120)) && (e.Source == "" || ContentFile.Text(e.Source, 500))
        && Scope(e.Engines) && Scope(e.Builds) && (e.Exes == null || e.Verdict == AntiCheatVerdict && Scope(e.Exes) && e.Exes.All(IsExe))
        && DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // any store: an id of a store this version doesn't know never matches
    internal static bool IsId(string? id) => id is { Length: <= 120 } && id.IndexOf(':') is > 0 and var c && c < id.Length - 1 && !id.Any(char.IsWhiteSpace);

    static bool IsExe(string x) => x.Length > 4 && x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && x.IndexOfAny(['/', '\\', ':', '*', '?']) < 0;

    internal static bool Scope(string[]? s) => s == null || s is { Length: >= 1 and <= 16 } && s.All(v => ContentFile.Text(v, 60));

    /// <summary>The line for a reason code: the app's own, else the note, else one for the verdict.</summary>
    public static string Text(string verdict, string reason, string? note) =>
        reason != OtherReason && Reasons.TryGetValue(reason, out var own) ? own
        : note ?? (verdict == NoStutterVerdict ? "This game prepares its shaders itself" : "SCSKiller can't compile this game");

    public static GameVerdicts Embedded { get => field ??= TryParse(ContentFile.Embedded("SCSKiller.Core.Games." + FileName))!; }

    /// <summary>The list in use: the embedded one unless replaced.</summary>
    public static GameVerdicts Current { get => field ??= Embedded; set; }

    /// <summary>Why the game has no shader stutter; null when no entry says so.</summary>
    public string? NoStutter(Game game, EngineInfo? engine) => Find(game, engine, NoStutterVerdict)?.Text;

    /// <summary>The unsupported or unsupported-yet verdict on the game; null when there is none.</summary>
    public Verdict? Unsupported(Game game, EngineInfo? engine) => Find(game, engine, UnsupportedVerdict) ?? Find(game, engine, UnsupportedYetVerdict);

    /// <summary>A "no-anti-cheat" entry names the id; one limited to engines or builds never counts: the anti-cheat check knows neither.</summary>
    public bool NoAntiCheat(string id) => byId.GetValueOrDefault(id)?.Any(e => e.Verdict == NoAntiCheatVerdict && e.Engines == null && e.Builds == null) == true;

    // this list's and the embedded one's: a served list can't take back what the app shipped with
    HashSet<string> AntiCheatNames { get => field ??= new(antiCheat.Concat(Embedded.antiCheat), StringComparer.OrdinalIgnoreCase); }

    /// <summary>An "anti-cheat" entry names this store id, or this exe by its file name.</summary>
    public bool HasAntiCheat(string? idOrExe) => idOrExe != null && AntiCheatNames.Contains(idOrExe);

    public bool HasAntiCheat(Game game) => HasAntiCheat(game.Id) || HasAntiCheat(Path.GetFileName(game.ExePath));

    /// <summary>The store ids of the "anti-cheat" entries.</summary>
    public IEnumerable<string> AntiCheatIds => AntiCheatNames.Where(n => n.Contains(':'));

    /// <summary>The "anti-cheat" entries as a stamp: a clean verdict holds only for the list it was checked against.</summary>
    // a newline: no name or id can hold one
    public string AntiCheatStamp { get => field ??= string.Join('\n', AntiCheatNames.Select(n => n.ToLowerInvariant()).Order(StringComparer.Ordinal)); }

    Verdict? Find(Game game, EngineInfo? engine, string verdict) =>
        byId.GetValueOrDefault(game.Id)?.FirstOrDefault(e => e.Verdict == verdict && InScope(e.Engines, e.Builds, game, () => engine)) is { } hit
            ? new(hit.Verdict, Text(hit.Verdict, hit.Reason, hit.Note)) : null;

    // "Unreal 5.7" takes 5.7 and 5.7.4, not 5.70; builds are Game.Version values
    internal static bool InScope(string[]? engines, string[]? builds, Game game, Func<EngineInfo?> engine) =>
        (engines == null || engine() is { } en && $"{en.Family} {en.Version}" is var have
            && engines.Any(v => have.Equals(v, StringComparison.OrdinalIgnoreCase) || have.StartsWith(v + ".", StringComparison.OrdinalIgnoreCase)))
        && (builds == null || game.Version != null && builds.Contains(game.Version));
}
