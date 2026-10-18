using Microsoft.Win32;

namespace SCSKiller.Core.Vendors;

/// <summary>AMD (Adrenalin). Caps measured on driver 32.0.31041.1004 (Adrenalin 26.8.1), see ARCHITECTURE.md:
/// the D3D12 cache is per exe file name (case-sensitive, except where a driver app profile fixes the key: see AmdAppCache), but blend/raster/RT state, root signature and above all the input
/// layout are part of the compiled result, so synthesized templates don't warm it. Each stage is compiled and cached on its
/// own (PerStageCache).
///
/// Driver version = "&lt;Adrenalin release&gt; (&lt;driver store version&gt;)", e.g. "26.8.1 (32.0.31041.1004)": a hotfix
/// driver can keep the Adrenalin number while its compiler (and so the cache) changes; the store/UMD version changes with
/// every driver. Read from the registry only: the display adapter's class key (RadeonSoftwareVersion + DriverVersion), else
/// HKLM\SOFTWARE\AMD\CN, else the release in ReleaseVersion ("26.10.41.01-260811a-..."); the DXGI user-mode driver version
/// when the class key has no DriverVersion. Shown only, never parsed: staleness follows DXGI's (ScsKiller.DriverId).
///
/// Cache size: the driver caps DxcCache at a fixed <see cref="AmdAppCache.DxcCacheCap"/>; Adrenalin, the registry and
/// ADLX offer no size setting (only the shader cache mode and a reset).</summary>
public sealed class AmdBackend : IGpuVendorBackend, IRefreshableGpu
{
    const string DisplayClass = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    readonly Func<string, (string? Adrenalin, string? DriverStore)> _registry;
    string? _umd, _read;   // DXGI's version and the version read at the last complete read
    bool _hadAdrenalin, _hadStore;   // the registry values that read found
    string? _adrenalin;              // the last release the registry gave

    /// <summary>The form without the registry's store version: the release, if known, and DXGI's version.</summary>
    public string FallbackVersion(string umd) => FormatVersion(_adrenalin, umd);

    public AmdBackend(GpuInfo dxgi) : this(dxgi, DriverVersions) { }

    internal AmdBackend(GpuInfo dxgi, Func<string, (string? Adrenalin, string? DriverStore)> registry)
    {
        (_registry, Gpu) = (registry, dxgi);
        Refresh(dxgi);
    }

    /// <summary>The version is read as at start (the registry's; DXGI's only without a store version there), never compared
    /// with DXGI's, whose format can differ. False (read again at the next check) for a read that can't be trusted yet:
    /// <list type="bullet">
    /// <item>a registry value the last complete read found is missing (a read during an install): that read's version stays;</item>
    /// <item>no registry value at all: DXGI's version, as at start, until the registry has one;</item>
    /// <item>DXGI's version changed but the registry still gives the same version: it is behind an install.</item>
    /// </list></summary>
    public bool Refresh(GpuInfo adapter)
    {
        var (adrenalin, store) = _registry(adapter.Name);
        _adrenalin = adrenalin ?? _adrenalin;
        if (_read != null && ((_hadAdrenalin && adrenalin == null) || (_hadStore && store == null)))
        {
            Gpu = adapter with { DriverVersion = _read };
            return false;
        }
        var version = FormatVersion(adrenalin, store ?? adapter.DriverVersion);
        Gpu = adapter with { DriverVersion = version };
        if ((adrenalin == null && store == null) || (_umd != null && adapter.DriverVersion != _umd && version == _read)) return false;
        (_umd, _read, _hadAdrenalin, _hadStore) = (adapter.DriverVersion, version, adrenalin != null, store != null);
        return true;
    }

    /// <summary>"26.8.1 (32.0.31041.1004)"; either part alone when the other is unknown.</summary>
    public static string FormatVersion(string? adrenalin, string? driverStore) =>
        (adrenalin is { Length: > 0 }, driverStore is { Length: > 0 }) switch
        {
            (true, true) => $"{adrenalin} ({driverStore})",
            (true, false) => adrenalin!,
            (false, true) => driverStore!,
            _ => "",
        };

    public GpuVendor Vendor => GpuVendor.Amd;
    public GpuInfo Gpu { get; private set; }
    public VendorCaps Caps { get; } = new("amd-1", CacheKeyedByExeName: true, StateIndependentCache: false, CacheSizeConfigurable: false,
        PerStageCache: true, RtCacheGranularity: RtCacheGranularity.WholeObject);   // selftest dxr 3: only an exact repeat of the whole object hits

    static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    /// <summary>D3D12 shader/pipeline cache.</summary>
    public static string CacheDir => Path.Combine(LocalAppData, "AMD", "DxcCache");
    /// <summary>D3D11 shader cache.</summary>
    public static string D3D11CacheDir => Path.Combine(LocalAppData, "AMD", "DxCache");

    /// <summary>DxcCache + DxCache per-application files.</summary>
    public AmdAppCache AppCache { get; } = new(CacheDir, D3D11CacheDir);
    IAppCache? IGpuVendorBackend.AppCache => AppCache;

    /// <summary>Both caches: DxcCache (D3D12) + DxCache (D3D11) bytes; <see cref="CacheUsage.Path"/> is the DxcCache folder
    /// (CacheUsage holds one path, DxCache is its sibling). Files are sized in powers of two (4 KiB .. 512 MiB seen) and
    /// grow by doubling, so the bytes on disk are an upper bound of the cached data.</summary>
    public CacheUsage GetCacheUsage() => new(CacheDir, Bytes(CacheDir) + Bytes(D3D11CacheDir), UpperBound: true);

    /// <summary>Bytes on disk of one cache folder (for a D3D12/D3D11 breakdown).</summary>
    public static long Bytes(string dir) => AppCacheFiles.Readable(dir)
        ? new DirectoryInfo(dir).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length)
        : 0;

    public CacheLimit? GetCacheLimit() => new(AmdAppCache.DxcCacheCap, IsDriverDefault: false);

    public void SetCacheLimit(CacheLimit limit) =>
        throw new NotSupportedException($"the AMD shader cache size is not configurable ({Gpu.Name})");

    /// <summary>The Adrenalin release ("26.8.1") and driver store version ("32.0.31041.1004") of the AMD display adapter
    /// named <paramref name="adapterName"/> (DXGI description = the class key's DriverDesc; the first AMD adapter if none
    /// matches); null for what isn't found.</summary>
    public static (string? Adrenalin, string? DriverStore) DriverVersions(string adapterName)
    {
        string? release = null, store = null;
        try
        {
            using var cls = Registry.LocalMachine.OpenSubKey(DisplayClass);
            RegistryKey? best = null;
            foreach (var name in cls?.GetSubKeyNames() ?? [])
            {
                if (name.Length != 4 || !name.All(char.IsAsciiDigit)) continue;   // 0000, 0001, ... ("Properties" is locked)
                RegistryKey? k;
                try { k = cls!.OpenSubKey(name); } catch (System.Security.SecurityException) { continue; }
                if (k == null) continue;
                bool amd = (k.GetValue("MatchingDeviceId") as string)?.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) == true;
                bool named = string.Equals((k.GetValue("DriverDesc") as string)?.Trim(), adapterName.Trim(), StringComparison.OrdinalIgnoreCase);
                if (amd && (named || best == null)) { best?.Dispose(); best = k; if (named) break; }
                else k.Dispose();
            }
            using (best)
            {
                store = (best?.GetValue("DriverVersion") as string)?.Trim() is { Length: > 0 } d ? d : null;
                if (best?.GetValue("RadeonSoftwareVersion") is string { Length: > 0 } v) return (v.Trim(), store);
                release = best?.GetValue("ReleaseVersion") as string;
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        try
        {
            using var cn = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\AMD\CN");
            if (cn?.GetValue("RadeonSoftwareVersion") is string { Length: > 0 } v) return (v.Trim(), store);
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        return (release?.Split('-')[0] is { Length: > 0 } r ? r.Trim() : null, store);
    }
}
