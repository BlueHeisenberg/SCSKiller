namespace SCSKiller.Linux.Core.Models;

public enum GameSource
{
    Steam,
    Heroic,
    Lutris,
    Bottles,
    Manual
}

public enum GameEngine
{
    Unknown,
    UnrealEngine4,
    UnrealEngine5,
    Unity,
    RedEngine,
    FromSoftware,
    ReEngine,
    Northlight,
    DirectX12,
    DirectX11
}

public enum CacheState
{
    NeedsCompilation,
    Cached,
    Outdated,
    Compiling
}

public record GameInfo(
    string Id,
    string Name,
    GameSource Source,
    string InstallPath,
    string ExecutablePath,
    string? PrefixPath,
    string? BannerPath,
    GameEngine Engine,
    CacheState State,
    int TotalShadersEstimated,
    int CompiledShadersCount,
    DateTime? LastCompiledAt,
    long CacheSizeBytes = 0,
    int CachedFilesCount = 0,
    bool RecorderInstalled = false,
    int RecordedPsos = 0
);
