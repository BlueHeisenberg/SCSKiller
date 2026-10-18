using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Core.Vendors;

namespace SCSKiller.Tests.Platform;

// The Cache page's inventory, on fake cache folders under the test's root: each file in exactly one item, nothing written.
public partial class AppTests
{
    static Dictionary<(CacheGroupKind, string), long> Items(CacheInventory inv) =>
        inv.Groups.SelectMany(g => g.Items.Select(i => (g.Kind, i))).ToDictionary(x => (x.Kind, x.i.Name), x => x.i.Bytes);

    static (string Name, DateTime Written, long Length)[] Stamps(params string[] dirs) =>
        dirs.SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories)).OrderBy(f => f.FullName)
            .Select(f => (f.FullName, f.LastWriteTimeUtc, f.Length)).ToArray();

    [Fact]
    public async Task Cache_inventory_groups_nvidia_files_by_game_driver_build_and_vendor_without_writing()
    {
        var installedAt = DateTime.UtcNow.AddDays(-1);
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        k.AppCache = new NvidiaAppCache(Path.Combine(_root, "DXCache"));
        k.DriverInstalledAt = () => installedAt;

        CacheFile("0002a91d11111111.nvph", 4096);
        CacheFile("32e6a91d11111111.nvph", 8192);
        File.SetLastWriteTimeUtc(CacheFile("fc52a91d11111111.nvph", 1024), installedAt.AddDays(-30));   // an older driver's, under the game's key
        File.SetLastWriteTimeUtc(CacheFile("fc52a91d55555555.nvph", 512), installedAt.AddDays(-30));
        CacheFile("0002a91d22222222.nvph", 16384);
        CacheFile("0002a91d44444444.nvph", 2048);
        CacheFile("0002a91d99999999.nvph", 32768);
        CacheFile("desktop.ini", 100);

        var rec = k.Store.LoadGame(_game.Id);
        (rec.CacheKeys, rec.GameKeys, rec.WarmedKeys) = (["11111111", "44444444"], ["11111111"], ["11111111"]);   // 44444444: an earlier compile's only
        k.Store.SaveGame(_game.Id, rec);
        k.Store.SaveGame("steam:42", new GameRecord { CacheKeys = ["22222222", "11111111"], WarmedExeName = "Gone-Win64-Shipping.exe" });

        var d3ds = Path.Combine(_root, "D3DSCache");
        FakeD3DSCache.Folder(d3ds, "1111", Path.Combine(k.Store.DataDir, "games", "steam_42", "work", "stage-10-1", "Gone-Win64-Shipping.exe"));
        FakeD3DSCache.Folder(d3ds, "2222", _game.ExePath);   // the game's own: not a compile's
        Directory.CreateDirectory(Path.Combine(_root, "AMD", "DxcCache"));
        File.WriteAllBytes(Path.Combine(_root, "AMD", "DxcCache", "207c35a9.dfac411e.71efbc0e.2b1a674a.0.parc"), new byte[700]);
        Directory.CreateDirectory(Path.Combine(_root, "NVIDIA", "GLCache", "1a2b"));
        File.WriteAllBytes(Path.Combine(_root, "NVIDIA", "GLCache", "1a2b", "c.bin"), new byte[300]);   // the active vendor's, no key

        string[] caches = [Path.Combine(_root, "DXCache"), Path.Combine(_root, "AMD"), d3ds];
        var before = Stamps(caches);
        var inv = k.CacheInventory();
        Assert.Equal(before, Stamps(caches));

        var gone = "Gone-Win64-Shipping.exe (steam_42)";
        var items = Items(inv);
        Assert.Equal(12288, items[(CacheGroupKind.Installed, $"Fake Game, {gone}")]);   // installed wins a shared key, both named
        Assert.Equal(16384, items[(CacheGroupKind.Uninstalled, gone)]);
        Assert.Equal(1024, items[(CacheGroupKind.OldDriver, $"Fake Game, {gone}")]);
        Assert.Equal(512, items[(CacheGroupKind.OldDriver, ScsKiller.OtherApps)]);
        Assert.Equal(2048, items[(CacheGroupKind.OldBuild, "Fake Game")]);
        Assert.True(items[(CacheGroupKind.OldBuild, ScsKiller.CompileLeftovers)] > 0);
        Assert.Equal(700, items[(CacheGroupKind.OtherVendor, "AMD DirectX 12")]);
        Assert.Equal(32768, items[(CacheGroupKind.Unattributed, "99999999")]);
        Assert.Equal(100, items[(CacheGroupKind.Unattributed, ScsKiller.NoAppKey)]);
        Assert.Equal(300, items[(CacheGroupKind.Unattributed, "NVIDIA OpenGL and Vulkan")]);

        var files = inv.Groups.SelectMany(g => g.Items).SelectMany(i => i.Files).Select(f => f.Path).ToList();
        Assert.Equal(files.Count, files.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(files, f => f.StartsWith(Path.Combine(d3ds, "2222"), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Enum.GetValues<CacheGroupKind>(), inv.Groups.Select(g => g.Kind));
        Assert.Null(inv.Groups.Single(g => g.Kind == CacheGroupKind.OtherVendor).Note);   // no AMD adapter listed

        Assert.Equal(installedAt, inv.DriverInstalled);

        // compiled under this driver before its DriverStore folder's time (a Windows upgrade remade it): that time is wrong
        rec = k.Store.LoadGame(_game.Id);
        (rec.WarmedAt, rec.WarmedDriverId) = (installedAt.AddDays(-40), k.DriverId);
        k.Store.SaveGame(_game.Id, rec);
        var clamped = k.CacheInventory();
        Assert.Equal(installedAt.AddDays(-40), clamped.DriverInstalled);
        Assert.DoesNotContain(clamped.Groups, g => g.Kind == CacheGroupKind.OldDriver);
        Assert.Equal(12288 + 1024, Items(clamped)[(CacheGroupKind.Installed, $"Fake Game, {gone}")]);

        var folder = Assert.Single(inv.Folders);
        Assert.Equal((Path.Combine(_root, "DXCache"), true, null, false, 4096L + 8192 + 1024 + 512 + 16384 + 2048 + 32768 + 100),
            (folder.Path, folder.Exists, folder.Final, folder.TargetMissing, folder.Bytes));
    }

    [Fact]
    public async Task Cache_inventory_on_amd_attributes_by_the_exe_name_hash_and_has_no_old_driver_group()
    {
        var k = Killer(new FakeReader(Unreal));
        await k.ScanAsync(default);
        k.AppCache = AmdCache();
        k.DriverInstalledAt = () => throw new InvalidOperationException("not read on AMD");
        k.Adapters = () => [Listed(Gpu), Listed(Gpu with { Vendor = GpuVendor.Nvidia })];

        var dxc = Path.Combine(_root, "AMD", "DxcCache");
        Directory.CreateDirectory(dxc);
        var ours = Path.Combine(dxc, $"{AmdAppCache.DxcAppHash(Path.GetFileName(_game.ExePath)):x}.dfac411e.71efbc0e.2b1a674a.0.parc");
        File.WriteAllBytes(ours, new byte[4096]);
        File.SetLastWriteTimeUtc(ours, DateTime.UtcNow.AddYears(-1));   // AMD's write times say nothing
        File.WriteAllBytes(Path.Combine(dxc, "2fd0f7f9fcb062f7.dfb482b94e92c8ee.7deb3888.9fbd7402.0.parc"), new byte[300]);   // a 64-bit app: no D3D12 key
        Directory.CreateDirectory(Path.Combine(_root, "NVIDIA", "DXCache"));
        File.WriteAllBytes(Path.Combine(_root, "NVIDIA", "DXCache", "0002a91d11111111.nvph"), new byte[900]);

        var inv = k.CacheInventory();
        var items = Items(inv);
        Assert.Equal(4096, items[(CacheGroupKind.Installed, "Fake Game")]);
        Assert.Equal(300, items[(CacheGroupKind.Unattributed, ScsKiller.NoAppKey)]);
        Assert.Equal(900, items[(CacheGroupKind.OtherVendor, "NVIDIA DirectX")]);
        Assert.Contains("NVIDIA GPU", inv.Groups.Single(g => g.Kind == CacheGroupKind.OtherVendor).Note);
        Assert.DoesNotContain(inv.Groups, g => g.Kind == CacheGroupKind.OldDriver);
        Assert.Equal(new[] { "DirectX 12", "DirectX 11" }, inv.Folders.Select(f => f.Label));
        Assert.False(inv.Folders[1].Exists);
        Assert.Equal((AmdAppCache.DxcCacheCap, (long?)null), (inv.Folders[0].Cap, inv.Folders[1].Cap));   // the fixed limit counts DirectX 12 only

        // the exe name's key learned, then left by a compile under another key the game holds: an earlier compile's
        var rec = k.Store.LoadGame(_game.Id);
        var hash = AmdAppCache.DxcKey(Path.GetFileName(_game.ExePath));
        (rec.CacheKeys, rec.GameKeys, rec.WarmedKeys) = ([hash, "dxc:0000abcd"], ["dxc:0000abcd"], ["dxc:0000abcd"]);
        k.Store.SaveGame(_game.Id, rec);
        Assert.Equal(4096, Items(k.CacheInventory())[(CacheGroupKind.OldBuild, "Fake Game")]);
    }

    /// <summary>The Unreal reader plans from the user's own pipeline caches in the game's Saved folders, never the install's.</summary>
    [Fact]
    public void The_unreal_reader_gets_the_users_own_pipeline_caches()
    {
        var reader = ScsKiller.DefaultReaders(Path.Combine(_root, "readers"));
        Killer(reader);
        var saved = Path.Combine(_root, "Fake", "Saved", "Fake_PCD3D_SM6.upipelinecache");
        foreach (var f in new[] { saved, Path.Combine(_game.InstallDir, "Saved", "Fake_PCD3D_SM6.upipelinecache") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllBytes(f, new byte[10]);
        }
        Assert.Equal([saved], ((Core.Carved.EngineReaders)reader).Get<Core.Unreal.UnrealReader>()!.UserCaches!(_game));
    }
}
