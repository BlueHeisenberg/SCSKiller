using System.Globalization;
using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>The exe of single games whose own pick is wrong, from game-exes.json. The embedded copy is the bundled body;
/// downloaded copies travel signed (<see cref="ContentTrust"/>, name "game-exes") like game-verdicts.json: the public copy
/// at /v1/content/ and alpha's and beta's at /v1/updates/&lt;channel&gt;/, a channel's copy winning. Matched by store id, and
/// only within the entry's engines and builds when it lists them (<see cref="GameVerdicts.InScope"/>). exe is relative to
/// the install root, either separator, never rooted, no part ending in a dot; the app uses it only when that file is in
/// the install, and a user's own pick wins over it. Read leniently like the game verdicts: unknown top-level members are
/// ignored, and an entry with an unknown member, or one this version finds invalid, is skipped.</summary>
public sealed class GameExes
{
    public const string Name = "game-exes", FileName = Name + ".json";
    static readonly string[] Members = ["name", "ids", "exe", "source", "date", "engines", "builds"];

    sealed record Entry(string Name, string[] Ids, string Exe, string Source, string Date, string[]? Engines = null, string[]? Builds = null);

    readonly Dictionary<string, List<Entry>> byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The entries this version applies.</summary>
    public int Count { get; }

    GameExes(List<Entry> entries)
    {
        foreach (var e in entries)
            foreach (var id in e.Ids)
                (byId.TryGetValue(id, out var l) ? l : byId[id] = []).Add(e);
        Count = entries.Count;
    }

    /// <summary>The list, or null when the document isn't an object within <see cref="ContentFile.MaxBytes"/> whose "games"
    /// is an array of at most <see cref="GameVerdicts.MaxGames"/>. The plain document: a signed one is checked by
    /// <see cref="ContentTrust"/> first.</summary>
    public static GameExes? TryParse(string json)
    {
        if (System.Text.Encoding.UTF8.GetByteCount(json) > ContentFile.MaxBytes) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("games", out var games)
                || games.ValueKind != JsonValueKind.Array || games.GetArrayLength() > GameVerdicts.MaxGames) return null;
            return new(games.EnumerateArray().Select(Read).OfType<Entry>().ToList());
        }
        catch (JsonException) { return null; }
    }

    static Entry? Read(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.EnumerateObject().All(p => Members.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
        && ContentFile.Read<Entry>(e) is { } entry && Valid(entry) ? entry : null;

    static bool Valid(Entry e) =>
        ContentFile.Text(e.Name, 100) && e.Ids is { Length: >= 1 and <= 8 } && e.Ids.All(GameVerdicts.IsId)
        && RelativeExe(e.Exe) && (e.Source == "" || ContentFile.Text(e.Source, 500))
        && GameVerdicts.Scope(e.Engines) && GameVerdicts.Scope(e.Builds)
        && DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    // Windows drops a part's trailing dots and spaces ("..." is ".."); ':' also refuses a drive and an NTFS stream
    static bool RelativeExe(string? exe) =>
        ContentFile.Text(exe, 260) && exe!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
        && exe.Split('/', '\\').All(p => p.Length > 0 && p.Trim() == p && !p.EndsWith('.') && !p.Contains(':'));

    public static GameExes Embedded { get => field ??= TryParse(ContentFile.Embedded("SCSKiller.Core.Games." + FileName))!; }

    /// <summary>The list in use: the embedded one unless replaced.</summary>
    public static GameExes Current { get => field ??= Embedded; set; }

    /// <summary>The game's exe relative to its install root, with this OS's separator; null when no entry in scope lists it.
    /// <paramref name="engine"/>: read only for an entry that lists engines.</summary>
    public string? Exe(Game game, Func<EngineInfo?> engine) =>
        byId.GetValueOrDefault(game.Id)?.FirstOrDefault(e => GameVerdicts.InScope(e.Engines, e.Builds, game, engine)) is { } hit
            ? hit.Exe.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar) : null;
}
