using System.Globalization;
using System.Text.Json;
using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>Games whose Maximum mode the app suggests, per GPU vendor, from maximum-mode.json. The embedded copy is the
/// bundled body; downloaded copies travel signed (<see cref="ContentTrust"/>, name "maximum-mode") like
/// game-verdicts.json: the public copy at /v1/content/ and alpha's and beta's at /v1/updates/&lt;channel&gt;/, a channel's
/// copy winning. Matched by store id and vendor ("amd", "nvidia": lowercase, as written). Read leniently like the game
/// verdicts: unknown top-level members are ignored, and an entry with an unknown member, or one this version finds
/// invalid, is skipped.</summary>
public sealed class MaximumMode
{
    public const string Name = "maximum-mode", FileName = Name + ".json";
    static readonly string[] Members = ["name", "ids", "vendors", "source", "date", "note"];
    static readonly string[] VendorNames = ["amd", "nvidia"];

    sealed record Entry(string Name, string[] Ids, string[] Vendors, string Source, string Date, string? Note = null);

    /// <summary>A suggestion for one game on this vendor; Note: the entry's line, null when it has none.</summary>
    public sealed record Suggestion(string? Note);

    readonly Dictionary<string, List<Entry>> byId = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The entries this version applies.</summary>
    public int Count { get; }

    MaximumMode(List<Entry> entries)
    {
        foreach (var e in entries)
            foreach (var id in e.Ids)
                (byId.TryGetValue(id, out var l) ? l : byId[id] = []).Add(e);
        Count = entries.Count;
    }

    /// <summary>The list, or null when the document isn't an object within <see cref="ContentFile.MaxBytes"/> whose "games"
    /// is an array of at most <see cref="GameVerdicts.MaxGames"/>. The plain document: a signed one is checked by
    /// <see cref="ContentTrust"/> first.</summary>
    public static MaximumMode? TryParse(string json)
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
        && e.Vendors is { Length: >= 1 and <= 2 } && e.Vendors.All(v => VendorNames.Contains(v, StringComparer.Ordinal)) && e.Vendors.Distinct().Count() == e.Vendors.Length
        && (e.Note == null || ContentFile.Text(e.Note, 120)) && (e.Source == "" || ContentFile.Text(e.Source, 500))
        && DateOnly.TryParseExact(e.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);

    public static MaximumMode Embedded { get => field ??= TryParse(ContentFile.Embedded("SCSKiller.Core.Games." + FileName))!; }

    /// <summary>The list in use: the embedded one unless replaced.</summary>
    public static MaximumMode Current { get => field ??= Embedded; set; }

    /// <summary>The suggestion for the game on <paramref name="vendor"/> ("amd", "nvidia"; <see cref="ScsKiller.PackGpu"/>);
    /// null when no entry lists both.</summary>
    public Suggestion? For(Game game, string? vendor) =>
        vendor != null && byId.GetValueOrDefault(game.Id)?.FirstOrDefault(e => e.Vendors.Contains(vendor, StringComparer.Ordinal)) is { } hit ? new(hit.Note) : null;
}
