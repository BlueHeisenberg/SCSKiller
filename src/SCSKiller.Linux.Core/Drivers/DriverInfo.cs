namespace SCSKiller.Linux.Core.Drivers;

public enum GpuVendor
{
    Unknown,
    Nvidia,
    Amd,
    Intel
}

public enum DriverCacheStatus
{
    Healthy,
    NeedsEnlargement,
    InvalidatedByUpdate,
    DirectoryMissing
}

public record DriverInfo(
    GpuVendor Vendor,
    string DeviceName,
    string DriverName,
    string DriverVersion,
    string ShaderCachePath,
    long CacheSizeBytes,
    int CachedFilesCount,
    long SteamShaderCacheSizeBytes,
    int SteamShaderCacheFilesCount,
    long TotalCacheSizeBytes,
    DriverCacheStatus Status,
    string? StatusMessage
);
