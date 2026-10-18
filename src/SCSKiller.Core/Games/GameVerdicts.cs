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
/// Unsupported ("Not supported yet", "Not supported"). Each entry names a reason code the app has its own line for
/// (<see cref="Reasons"/>); an unknown code shows the entry's note, else a line for its verdict. Matched by store id, and
/// only within the entry's engines and builds when it lists them. Read leniently, so later versions can add to the format:
/// unknown top-level members are ignored, and an entry with an unknown member or verdict, or one this version finds
/// invalid, is skipped.</summary>
public sealed class GameVerdicts
{
    public const string Name = "game-verdicts", FileName = Name + ".json";
    public const string NoStutterVerdict = "no-stutter", UnsupportedYetVerdict = "unsupported-yet", UnsupportedVerdict = "unsupported";
    public const string OtherReason = "other";
    public const int MaxGames = 500;
    static readonly string[] Verdicts = [NoStutterVerdict, UnsupportedYetVerdict, UnsupportedVerdict];
    static readonly string[] Members = ["name", "ids", "verdict", "reason", "note", "source", "date", "engines", "builds"];

    /// <summary>The reason codes this version shows in its own words. Codes are [a-z0-9-]; "other" takes the entry's note.</summary>
    public static readonly IReadOnlyDictionary<string, string> Reasons = new Dictionary<string, string>
    {
        ["engine-precompiles"] = "Its engine prepares shaders at startup",
        ["driver-cache-ignored"] = "The driver doesn't use a compile's cache for this game",
        ["anti-cheat"] = "Its anti-cheat doesn't allow it",
        ["crashes"] = "Compiling crashes this game or the driver",
        ["engine-unreadable"] = "SCSKiller can't read this game's shaders yet",
    };

    /// <summary>A verdict for one game: what it is and the line the app shows.</summary>
    public sealed record Verdict(string Kind, string Text);

    sealed record Entry(string Name, string[] Ids, string Verdict, string Reason, string Source, string Date,
        string? Note = null, string[]? Engines = null, string[]? Builds = null);

    readonly Dictionary<string, List<Entry>> byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The entries this version applies.</summary>
    public int Count { get; }

    GameVerdicts(List<Entry> entries)
    {
        foreach (var e in entries)
            foreach (var id in e.Ids)
                (byId.TryGetValue(id, out var l) ? l : byId[id] = []).Add(e);
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
        && Scope(e.Engines) && Scope(e.Builds)
        && DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // any store: an id of a store this version doesn't know never matches
    internal static bool IsId(string? id) => id is { Length: <= 120 } && id.IndexOf(':') is > 0 and var c && c < id.Length - 1 && !id.Any(char.IsWhiteSpace);

    static bool Scope(string[]? s) => s == null || s is { Length: >= 1 and <= 16 } && s.All(v => ContentFile.Text(v, 60));

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

    // "Unreal 5.7" takes 5.7 and 5.7.4, not 5.70; builds are Game.Version values
    Verdict? Find(Game game, EngineInfo? engine, string verdict) =>
        byId.GetValueOrDefault(game.Id)?.FirstOrDefault(e => e.Verdict == verdict
            && (e.Engines == null || engine != null && $"{engine.Family} {engine.Version}" is var have
                && e.Engines.Any(v => have.Equals(v, StringComparison.OrdinalIgnoreCase) || have.StartsWith(v + ".", StringComparison.OrdinalIgnoreCase)))
            && (e.Builds == null || game.Version != null && e.Builds.Contains(game.Version))) is { } hit
            ? new(hit.Verdict, Text(hit.Verdict, hit.Reason, hit.Note)) : null;
}
