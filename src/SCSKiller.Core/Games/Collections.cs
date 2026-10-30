using SCSKiller.Core.App;

namespace SCSKiller.Core.Games;

/// <summary>A game of collections.json. <paramref name="Exes"/>: file names in the install folder, or in a collection whose
/// games have folders of their own, paths inside the game's <paramref name="Data"/>; the first the one it's listed with.
/// <paramref name="Data"/>: the folder its engine reader reads, relative to the install folder.</summary>
public sealed record CollectionGame(string Id, string Name, string[] Exes, string Data);

/// <summary>Store apps that install several games in one folder (collections.json, embedded only), listed as one game
/// each. The first keeps the app's id and owns the folder's recorder, armed for whichever of the collection's exes ran
/// last (ScsKiller.ArmTarget); the others never install, arm or import it, and get their records from its imports.
/// A collection whose games have folders of their own lists each with that folder as its install, as a game of its own.</summary>
public static class Collections
{
    sealed record Entry(string Id, string Name, string[] Exes, string Data);
    sealed record Collection(Entry[] Games);
    sealed record ListFile(Collection[] Collections, string? About = null);

    static readonly List<IReadOnlyList<CollectionGame>> All = Parse(ContentFile.Embedded("SCSKiller.Core.Games.collections.json"));

    /// <summary>The collections whose games share the install folder and its recorder.</summary>
    public static IReadOnlyList<IReadOnlyList<CollectionGame>> Current { get; } = [.. All.Where(c => !InOwnFolders(c))];

    // Parse requires every entry to name an exe
    static bool InOwnFolders(IReadOnlyList<CollectionGame> c) => !IsName(c[0].Exes[0]);

    // the file's paths are Windows-form
    static bool IsName(string exe) => Path.GetFileName(GameFiles.Under("", exe)) == exe;

    internal static List<IReadOnlyList<CollectionGame>> Parse(string json)
    {
        var file = ContentFile.Read<ListFile>(json) ?? throw new InvalidDataException("collections.json");
        var all = new List<IReadOnlyList<CollectionGame>>();
        var ids = new HashSet<string>();
        foreach (var c in file.Collections)
        {
            if (c.Games.Length < 2) throw new InvalidDataException("collections.json: a collection of one game");
            var own = c.Games[0].Exes is [var first, ..] && !IsName(first);
            foreach (var e in c.Games)
                if (!ContentFile.Text(e.Id, 128) || !ContentFile.Text(e.Name, 128) || !ids.Add(e.Id)
                    || e != c.Games[0] && !e.Id.StartsWith(c.Games[0].Id + ":", StringComparison.Ordinal)
                    || e.Exes.Length == 0 || e.Exes.Any(x => !x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        || (own ? !x.StartsWith(e.Data + "\\", StringComparison.OrdinalIgnoreCase) || x.Split('\\', '/').Contains("..") : !IsName(x)))
                    || !ContentFile.Text(e.Data, 260) || Path.IsPathRooted(e.Data) || e.Data.Split('\\', '/').Contains(".."))
                    throw new InvalidDataException($"collections.json: {e.Id}");
            all.Add([.. c.Games.Select(e => new CollectionGame(e.Id, e.Name, e.Exes, e.Data))]);
        }
        return all;
    }

    /// <summary>The collection the game is in and its entry there; null when it's in none.</summary>
    public static (IReadOnlyList<CollectionGame> All, CollectionGame Game)? Member(string gameId)
    {
        foreach (var c in Current)
            if (c.FirstOrDefault(e => e.Id == gameId) is { } e) return (c, e);
        return null;
    }

    /// <summary>The id of the game whose recorder records <paramref name="gameId"/>: its collection's first; null when that's
    /// itself or it's in none.</summary>
    public static string? RecordedBy(string gameId) => Member(gameId) is { } m && m.All[0].Id != gameId ? m.All[0].Id : null;

    /// <summary>The exe names the game runs as; null when it's in no collection, or listed with an exe its entry doesn't name
    /// (the install no longer has one: listed as any game, any exe of its folder).</summary>
    public static string[]? Exes(Game g) =>
        Member(g.Id)?.Game.Exes is { } exes && exes.Contains(Path.GetFileName(g.ExePath), StringComparer.OrdinalIgnoreCase) ? exes : null;

    /// <summary>Every exe of each collection that names <paramref name="exe"/>'s file name, in its folder: what a shared
    /// recorder there may be armed for.</summary>
    public static IEnumerable<string> ExesBeside(string exe) =>
        Current.Where(c => c.Any(e => e.Exes.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase)))
            .SelectMany(c => c.SelectMany(e => e.Exes)).Select(x => GameFiles.Find(Path.GetDirectoryName(exe)!, x));

    /// <summary>The games a store lists for its app <paramref name="app"/>: one per game of the collection with the app's id
    /// whose exe the install has; none when it's no collection's, or the install lacks the first game's exes.</summary>
    public static List<Game> Expand(Game app)
    {
        if (All.FirstOrDefault(c => c[0].Id == app.Id) is not { } c) return [];
        var own = InOwnFolders(c);
        var games = new List<Game>();
        foreach (var e in c)
            if (e.Exes.Select(x => GameFiles.Find(app.InstallDir, x)).FirstOrDefault(File.Exists) is { } exe)
                games.Add(app with { Id = e.Id, Name = e.Name, ExePath = exe, InstallDir = own ? GameFiles.Find(app.InstallDir, e.Data) : app.InstallDir });
        return games.Count > 0 && games[0].Id == c[0].Id ? games : [];
    }

    /// <summary>The store app's install folder: for a game listed in a folder of its own, the one its entry's data is in;
    /// else, or when <paramref name="g"/>'s install folder isn't that data folder (a record from before), its install folder.</summary>
    public static string AppDir(Game g) =>
        All.Where(InOwnFolders).SelectMany(c => c).FirstOrDefault(e => e.Id == g.Id) is { } e
        && Path.TrimEndingDirectorySeparator(g.InstallDir) is var dir && dir.EndsWith(Path.DirectorySeparatorChar + GameFiles.Under("", e.Data), StringComparison.OrdinalIgnoreCase)
            ? dir[..^(e.Data.Length + 1)]
            : g.InstallDir;
}
