using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Drivers;

public interface IDriverBackend
{
    GpuVendor Vendor { get; }
    Task<DriverInfo> GetDriverInfoAsync(CancellationToken cancellationToken = default);
    Task ClearCacheAsync(CancellationToken cancellationToken = default);
    Task ClearAllCachesAsync(CancellationToken cancellationToken = default);
    Task ClearGameCacheAsync(string gameId, GameSource source, string? prefixPath = null, CancellationToken cancellationToken = default);
    string GetRecommendedEnvironmentVariables();
    Task<(long Bytes, int Files, string Path)> GetGameCacheStatsAsync(string gameId, GameSource source, string? prefixPath = null);
}
