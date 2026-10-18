using System.Globalization;
using System.Text.Json;

namespace SCSKiller.Core.App;

/// <summary>An item of news.json. Targeting, all optional and checked on the PC: <paramref name="Channels"/> the running
/// build's channel, <paramref name="Ent"/> any of the account's entitlement flags ("none": an account with none, or signed
/// out), <paramref name="MinVersion"/>..<paramref name="MaxVersion"/> the app version (inclusive), <paramref name="Gpu"/>
/// the GPU vendor ("nvidia", "amd", "intel"), <paramref name="Game"/> a game id the Library has (also its "Go to game"
/// button). Dates are yyyy-MM-dd; an item is hidden from its <paramref name="Expires"/> day on.</summary>
public sealed record NewsItem(string Id, string Date, string Title, string Body, string? Link = null, string? Game = null, string? Expires = null,
    string[]? Channels = null, string[]? Ent = null, string? MinVersion = null, string? MaxVersion = null, string[]? Gpu = null);

/// <summary>What an item is shown against.</summary>
public sealed record NewsTarget(AppVersion Version, IReadOnlyCollection<string> Ent, GpuVendor Gpu, Func<string, bool> HasGame);

/// <summary>Notifications from SCSKiller: news.json, signed like plan-rules.json (<see cref="ContentTrust"/>, the rules keys,
/// name "news"). Stable's is /v1/content/news.json; alpha and beta have their own whole file behind the access token, read
/// by that update channel (<see cref="FetchAsync"/>). An item that fails validation, or has a field this version doesn't
/// know (a newer targeting rule), is dropped alone.</summary>
public sealed class News
{
    public const string Name = "news";
    public const string ContentPath = "v1/content/news.json";
    const int MaxItems = 50;

    sealed record Doc(JsonElement[] Items, string? About = null);

    public IReadOnlyList<NewsItem> Items { get; }

    News(IReadOnlyList<NewsItem> items) => Items = items;

    public static readonly News Empty = new([]);

    public static News? TryParse(string json)
    {
        if (ContentFile.Read<Doc>(json) is not { Items.Length: <= MaxItems } doc || doc.About != null && !ContentFile.Text(doc.About, 1000)) return null;
        var items = new List<NewsItem>();
        foreach (var e in doc.Items)
            if (ContentFile.Read<NewsItem>(e) is { } i && Valid(i) && items.All(x => x.Id != i.Id)) items.Add(i);
        return new(items);
    }

    static bool Valid(NewsItem i) =>
        i.Id is { Length: > 0 and <= 64 } && i.Id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c is '-' or '.')
        && Day(i.Date) != null && (i.Expires == null || Day(i.Expires) != null)
        && ContentFile.Text(i.Title, 100) && ContentFile.Text(i.Body, 500)
        && (i.Link == null || IsAllowedLink(i.Link)) && (i.Game == null || ContentFile.Text(i.Game, 200))
        && (i.MinVersion == null || AppVersion.Parse(i.MinVersion) != null) && (i.MaxVersion == null || AppVersion.Parse(i.MaxVersion) != null)
        && List(i.Channels) && List(i.Ent) && List(i.Gpu);

    static bool List(string[]? values) => values == null || values.Length > 0 && values.All(v => ContentFile.Text(v, 32));

    public static DateOnly? Day(string? s) => DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    /// <summary>The welcome's domains, Discord, and the project's GitHub.</summary>
    public static bool IsAllowedLink(string url)
    {
        if (WelcomeContent.IsAllowedLink(url)) return true;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || !u.IsDefaultPort || u.UserInfo.Length != 0) return false;
        bool Host(string h) => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase);
        return Host("discord.gg") || Host("discord.com") || u.Host.EndsWith(".discord.com", StringComparison.OrdinalIgnoreCase)
            || Host("github.com") && (u.AbsolutePath.Equals("/BlueHeisenberg", StringComparison.OrdinalIgnoreCase)
                || u.AbsolutePath.StartsWith("/BlueHeisenberg/", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The items for this PC on <paramref name="today"/>, newest first.</summary>
    public IReadOnlyList<NewsItem> For(NewsTarget t, DateOnly today) =>
        Items.Where(i => Shown(i, t, today)).OrderByDescending(i => Day(i.Date)).ThenBy(i => i.Id, StringComparer.Ordinal).ToList();

    static bool Shown(NewsItem i, NewsTarget t, DateOnly today) =>
        (i.Expires == null || today < Day(i.Expires))
        && (i.Channels == null || i.Channels.Contains(t.Version.Channel))
        && (i.Ent == null || i.Ent.Any(t.Ent.Contains) || t.Ent.Count == 0 && i.Ent.Contains("none"))
        && (i.MinVersion == null || t.Version.CompareTo(AppVersion.Parse(i.MinVersion)) >= 0)
        && (i.MaxVersion == null || t.Version.CompareTo(AppVersion.Parse(i.MaxVersion)) <= 0)
        && (i.Gpu == null || i.Gpu.Contains(t.Gpu.ToString().ToLowerInvariant()))
        && (i.Game == null || t.HasGame(i.Game));

    /// <summary>The newest kept news among stable's and those of <paramref name="channels"/> (<see cref="Channels"/>; null: not
    /// known yet, every copy kept), <see cref="ContentTrust.Cached"/>.</summary>
    public static News? Cached(string file, ContentTrust trust, IReadOnlyCollection<string>? channels) => trust.Cached(Name, TryParse, file, channels);

    /// <summary>The channel file besides stable's that an update channel reads: internal reads alpha's.</summary>
    public static string[] Channels(string updateChannel) => updateChannel switch
    {
        UpdateChannels.Alpha or UpdateChannels.Internal => [UpdateChannels.Alpha],
        UpdateChannels.Beta => [UpdateChannels.Beta],
        _ => [],
    };

    /// <summary>The newest signed, valid news among stable's and, with an access <paramref name="token"/>, that of the update
    /// channel (<paramref name="channels"/>: <see cref="Channels"/> of <see cref="UpdateChannels.Effective"/>; null: not known
    /// yet), each channel's kept beside <paramref name="cacheFile"/> (<see cref="ContentTrust.FetchAsync"/>). Null when no
    /// channel read has any.</summary>
    public static Task<News?> FetchAsync(string cacheFile, ContentTrust trust, RouteFailover? routes = null, string? token = null,
        IReadOnlyCollection<string>? channels = null, TimeSpan? budget = null) =>
        trust.FetchAsync(Name, TryParse, cacheFile, channels, token, routes, budget);
}

/// <summary>news-seen.json, local only: the X.Y.Z whose What's new was shown, the news items read and dismissed.</summary>
public sealed record NewsSeen(string? WhatsNew = null, string[]? Read = null, string[]? Dismissed = null);

public static class NewsState
{
    const int Keep = 200;

    /// <summary>The items not dismissed, and the ids of those unread.</summary>
    public static (IReadOnlyList<NewsItem> Shown, IReadOnlySet<string> Unread) Of(AppStore store, IEnumerable<NewsItem> items)
    {
        var seen = store.LoadNewsSeen();
        var shown = items.Where(i => seen.Dismissed?.Contains(i.Id) != true).ToList();
        return (shown, shown.Select(i => i.Id).Except(seen.Read ?? []).ToHashSet());
    }

    /// <summary><paramref name="feed"/>: the ids of every item in news.json now, whose state is never pruned.</summary>
    public static void MarkRead(AppStore store, IEnumerable<string> ids, IReadOnlyCollection<string> feed) =>
        Update(store, s => s with { Read = Add(s.Read, ids, feed) });

    public static void Dismiss(AppStore store, string id, IReadOnlyCollection<string> feed) =>
        Update(store, s => s with { Dismissed = Add(s.Dismissed, [id], feed), Read = Add(s.Read, [id], feed) });

    static readonly object Lock = new();

    static void Update(AppStore store, Func<NewsSeen, NewsSeen> change)
    {
        lock (Lock) store.SaveNewsSeen(change(store.LoadNewsSeen()));
    }

    /// <summary>The feed's ids always, then the newest of the others up to <see cref="Keep"/>.</summary>
    static string[] Add(string[]? ids, IEnumerable<string> more, IReadOnlyCollection<string> feed)
    {
        var all = (ids ?? []).Concat(more).Distinct().ToList();
        var gone = all.Where(i => !feed.Contains(i)).TakeLast(Keep).ToHashSet();
        return all.Where(i => feed.Contains(i) || gone.Contains(i)).ToArray();
    }
}
