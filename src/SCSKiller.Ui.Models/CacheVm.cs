using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Games;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Ui.Models;

/// <summary>A cache folder on the Cache page.</summary>
public sealed record CacheFolderRow(string Label, string Path, string Size, string? Note)
{
    public bool HasNote => Note != null;
}

/// <summary>A cache item; Folders: where its files are (<see cref="CacheInventory.FoldersOf"/>).</summary>
public sealed record CacheItemRow(string Name, string Size, IReadOnlyList<string> Folders, string? Note, CacheItem Item, CacheGroupKind Kind, bool Enabled, object? Icon = null)
{
    public bool NoIcon => Icon == null;
    public bool HasFolder => Folders.Count > 0;
    public bool HasNote => Note != null;
}

public sealed record CacheGroupRow(string Title, string Size, string Caption, string? Note, IReadOnlyList<CacheItemRow> Items, bool Expanded,
    CacheGroup Group, bool Enabled)
{
    public bool HasNote => Note != null;
}

/// <summary>The Cache page: a read of <see cref="IScsKiller.CacheInventory"/> off the UI thread, and its deletes.</summary>
public sealed class CacheVm : Bindable
{
    CacheInventory? read;
    HashSet<string> games = [];   // the library the last read attributed by
    string split = "";
    readonly BackgroundRead<(CacheInventory Inventory, CacheUsage Usage, CacheLimit? Limit, long? Capped, long? Fullest)> cache;

    public CacheVm()
    {
        cache = new("Cache page", () => (Host.Core.CacheInventory(), Host.Core.Vendor.GetCacheUsage(), Host.Core.Vendor.GetCacheLimit(), (Host.Core.Vendor as AmdBackend)?.AppCache.DxcBytes(), LibraryVm.Fullest()),
            r =>
            {
                read = r.Inventory;
                split = Format.CacheSplit(r.Usage, r.Limit, r.Capped, Host.Core.WindowsCacheBytes);
                UpperNote = Format.CacheUpperNote(r.Usage);
                var fullest = r.Limit?.Bytes != null ? r.Fullest : null;
                Warning = Format.CacheWarning(Host.Core.Vendor.Vendor, r.Limit, fullest ?? r.Capped ?? r.Usage.BytesOnDisk, fullest != null);
                Changed();
            });
        Refresh();
    }

    public void Refresh()
    {
        games = Host.Core.Games.Select(g => g.Game.Id).ToHashSet();
        cache.Request();
    }

    /// <summary>A scan that lists other games (the first one after start among them) moves caches between groups.</summary>
    public void GamesChanged()
    {
        if (!games.SetEquals(Host.Core.Games.Select(g => g.Game.Id))) Refresh();
    }

    public CacheInventory? Inventory => read;
    public string Total => read == null ? Loc.T("Cache_Reading") : split;
    public string? UpperNote { get; private set; }
    public string? Warning { get; private set; }
    public bool HasWarning => Warning != null;
    public bool NoFolders => read is { Folders.Count: 0, Groups.Count: 0 };

    public bool Deleting { get; private set; }
    public bool CanDelete => read != null && !Deleting;
    public bool CanClearEverything => CanDelete && read!.Everything(withOthers: true).Count > 0;
    public string? Result { get; private set; }
    public bool HasResult => Result != null;
    public InfoBarSeverity ResultSeverity { get; private set; }

    /// <summary>Deletes off the UI thread, then reads the caches again: the page shows what is left, not a subtraction.</summary>
    public async Task DeleteAsync(IReadOnlyList<CacheItem> items)
    {
        (Deleting, Result) = (true, null);
        Changed();
        try
        {
            var r = await Task.Run(() => Host.Core.DeleteCaches(items));
            Result = Loc.T(r.Files == 1 ? "Cache_DeletedOne" : "Cache_DeletedMany", Format.Bytes(r.Bytes), r.Files)
                + (r.Skipped.Count > 0 ? Loc.T("Cache_SkippedFiles", r.Skipped.Count) + ":\n" + string.Join("\n", r.Skipped) : "");
            ResultSeverity = r.Skipped.Count > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        }
        catch (Exception e)
        {
            (Result, ResultSeverity) = (Loc.T("Cache_NothingDeleted", e.Message), InfoBarSeverity.Error);
        }
        Deleting = false;
        Refresh();
        Changed();
    }

    long OldDriver => read?.Bytes(CacheGroupKind.OldDriver) ?? 0;
    public bool HasOldDriver => OldDriver > 0;
    public string OldDriverText => Loc.T("Cache_OldDriverText", Format.Bytes(OldDriver), read?.DriverInstalled?.ToLocalTime().ToString("d") ?? "");
    public string FreeText => Loc.T("Cache_FreeText", Format.Bytes(OldDriver));
    public IReadOnlyList<CacheItem> OldDriverItems => read?.Groups.Where(g => g.Kind == CacheGroupKind.OldDriver).SelectMany(g => g.Items).ToList() ?? [];

    public IReadOnlyList<CacheFolderRow> Folders => read?.Folders.Select(f => new CacheFolderRow(f.Label, f.Path,
        f.Cap is { } cap ? $"{Format.Bytes(f.Bytes)} of {Format.Bytes(cap)}" : Format.Bytes(f.Bytes),
        f.TargetMissing ? Loc.T("Cache_FolderTargetMissing")
        : f.Cap is { } full && f.Bytes >= full * 9 / 10 ? Loc.T("Cache_FolderNearlyFull")
        : f.Final != null ? Loc.T("Cache_FolderStoredVia", f.Final)
        : !f.Exists ? Loc.T("Cache_FolderNotCreated") : null)).ToList() ?? [];

    public IReadOnlyList<CacheGroupRow> Groups => read?.Groups.Select(g =>
    {
        var (title, caption) = Texts(g.Kind, read.DriverInstalled);
        var items = g.Items.Select(i => new CacheItemRow(ItemName(g.Kind, i), Format.OnDisk(i.Bytes, i.InUse), read.FoldersOf(i), i.Note, i, g.Kind, CanDelete && !i.Kept, g.Kind == CacheGroupKind.Installed ? GameIcon(i) : null)).ToList();
        return new CacheGroupRow(title, Format.OnDisk(g.Bytes, g.InUse), caption, g.Note, items, g.Kind != CacheGroupKind.Unattributed, g, CanDelete && g.Items.Any(i => !i.Kept));
    }).ToList() ?? [];

    // The Library's icon source and cache, so no new extraction path.
    static object? GameIcon(CacheItem i) =>
        i.GameIds.Select(id => Host.Core.Games.FirstOrDefault(g => g.Game.Id == id)).FirstOrDefault(g => g != null) is { } game ? Host.Icons.Get(game.Game.ExePath) : null;

    /// <summary>An icon finished loading: the rows read it again.</summary>
    public void IconsChanged() => Changed();

    static string ItemName(CacheGroupKind kind, CacheItem i) => kind == CacheGroupKind.Unattributed && i.Name.Length == 8 && i.Name.All(char.IsAsciiHexDigit) ? Loc.T("Cache_UnknownApp", i.Name) : i.Name;

    public static (string Title, string Caption) Texts(CacheGroupKind kind, DateTime? installed) => kind switch
    {
        CacheGroupKind.Installed => (Loc.T("Cache_GInstalledTitle"), Loc.T("Cache_GInstalledCaption")),
        CacheGroupKind.Uninstalled => (Loc.T("Cache_GUninstalledTitle"), Loc.T("Cache_GUninstalledCaption")),
        CacheGroupKind.OldDriver => (Loc.T("Cache_GOldDriverTitle"), Loc.T("Cache_GOldDriverCaption", installed?.ToLocalTime().ToString("d") ?? "")),
        CacheGroupKind.OldBuild => (Loc.T("Cache_GOldBuildTitle"), Loc.T("Cache_GOldBuildCaption")),
        CacheGroupKind.OtherVendor => (Loc.T("Cache_GOtherVendorTitle"), Loc.T("Cache_GOtherVendorCaption")),
        _ => (Loc.T("Cache_GOtherTitle"), Loc.T("Cache_GOtherCaption")),
    };

    /// <summary>The confirm's text for deleting <paramref name="items"/> of one group: what happens after.</summary>
    public static string Consequence(CacheGroupKind kind, IReadOnlyList<CacheItem> items) => kind switch
    {
        CacheGroupKind.Installed or CacheGroupKind.Uninstalled =>
            Loc.T("Cache_DeleteGames", string.Join(", ", items.Select(i => i.Name))),
        CacheGroupKind.OldDriver => Loc.T("Cache_DeleteOldDriver"),
        CacheGroupKind.OldBuild => Loc.T("Cache_DeleteOldBuild"),
        CacheGroupKind.OtherVendor => Loc.T("Cache_DeleteOtherVendor"),
        _ => Loc.T("Cache_DeleteOther"),
    } + string.Concat(items.Select(i => i.Note).OfType<string>().Select(n => " " + n)) + Loc.T("Cache_FilesInUseSkipped");
}
