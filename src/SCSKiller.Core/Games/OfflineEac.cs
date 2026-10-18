using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>A game of offline-eac.json: started as <paramref name="Exe"/> with steam_appid.txt holding <paramref name="AppId"/>
/// beside it, it runs offline without EasyAntiCheat. <paramref name="EacProduct"/>: "productid/sandboxid" of its
/// EasyAntiCheat\Settings.json, which identifies a copy added by hand; null: only the store's copy qualifies.</summary>
public sealed record OfflineEacGame(string Id, string Name, string Exe, string AppId, string? EacProduct = null);

/// <summary>The games SCSKiller offers an offline recording session for (offline-eac.json, embedded only: the list changes
/// with a release, never from the server).</summary>
public static class OfflineEac
{
    sealed record Entry(string Id, string Name, string Exe, string[] Sources, string? EacProductId = null, string? EacSandboxId = null);
    sealed record ListFile(Entry[] Games, string? About = null);

    /// <summary>Replaceable for tests.</summary>
    public static IReadOnlyList<OfflineEacGame> Current { get; internal set; } = Parse(ContentFile.Embedded("SCSKiller.Core.Games.offline-eac.json"));

    static List<OfflineEacGame> Parse(string json) =>
        [.. (ContentFile.Read<ListFile>(json) ?? throw new InvalidDataException("offline-eac.json")).Games
            .Select(e => new OfflineEacGame(e.Id, e.Name, e.Exe, e.Id.StartsWith("steam:", StringComparison.Ordinal) && e.Id[6..].All(char.IsAsciiDigit) && e.Id.Length > 6
                ? e.Id[6..] : throw new InvalidDataException($"offline-eac.json: {e.Id} isn't a Steam app"),
                (e.EacProductId, e.EacSandboxId) switch
                {
                    (null, null) => null,
                    ({ } p, { } s) when IsEacId(p) && IsEacId(s) => $"{p}/{s}",
                    _ => throw new InvalidDataException($"offline-eac.json: {e.Id} needs both EasyAntiCheat ids, 32 lowercase hex digits each"),
                }))];

    static bool IsEacId(string id) => id.Length == 32 && id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f');

    /// <summary>The entry the game is, when it uses EasyAntiCheat (and not VAC) and runs on D3D12, its exe the listed one, and
    /// it is the listed id, or a copy added by hand whose EasyAntiCheat settings name the entry's product.
    /// <paramref name="unsupported"/>: an engine SCSKiller doesn't support qualifies too (Settings.RecordAntiCheatGames on).</summary>
    public static OfflineEacGame? Of(Game g, AntiCheat antiCheat, EngineInfo? engine, bool unsupported = false) =>
        antiCheat == AntiCheat.EasyAntiCheat && !Vac.Is(g) && engine != null && (unsupported || engine.Unsupported == null) && engine.GraphicsApi.Contains("D3D12")
        && Current.FirstOrDefault(e => Path.GetFileName(g.ExePath).Equals(e.Exe, StringComparison.OrdinalIgnoreCase)
            && (e.Id == g.Id || g.Store == Store.Manual && e.EacProduct != null && GameFiles.EacProduct(g.ExePath) == e.EacProduct)) is { } entry ? entry : null;
}
