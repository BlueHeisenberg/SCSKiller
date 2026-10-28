using System.Diagnostics;
using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Drivers;

public class NvidiaBackend : IDriverBackend
{
    public GpuVendor Vendor => GpuVendor.Nvidia;

    public async Task<DriverInfo> GetDriverInfoAsync(CancellationToken cancellationToken = default)
    {
        string deviceName = "NVIDIA GeForce GPU";
        string driverVersion = "Unknown";

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "nvidia-smi",
                    Arguments = "--query-gpu=gpu_name,driver_version --format=csv,noheader",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            var parts = output.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length >= 2)
            {
                deviceName = parts[0];
                driverVersion = parts[1];
            }
        }
        catch { }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string globalCachePath = Path.Combine(home, ".cache", "nvidia", "GLCache");
        if (!Directory.Exists(globalCachePath))
        {
            string legacyPath = Path.Combine(home, ".nv", "GLCache");
            if (Directory.Exists(legacyPath))
            {
                globalCachePath = legacyPath;
            }
        }

        long globalSize = 0;
        int globalFiles = 0;

        if (Directory.Exists(globalCachePath))
        {
            try
            {
                var files = Directory.GetFiles(globalCachePath, "*", SearchOption.AllDirectories);
                globalFiles = files.Length;
                foreach (var file in files)
                {
                    globalSize += new FileInfo(file).Length;
                }
            }
            catch { }
        }

        // Measure Steam per-game shader caches
        long steamSize = 0;
        int steamFiles = 0;
        string steamShaderCache = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache");
        if (Directory.Exists(steamShaderCache))
        {
            try
            {
                var files = Directory.GetFiles(steamShaderCache, "*", SearchOption.AllDirectories);
                steamFiles = files.Length;
                foreach (var file in files)
                {
                    steamSize += new FileInfo(file).Length;
                }
            }
            catch { }
        }

        long totalSize = globalSize + steamSize;
        int totalFiles = globalFiles + steamFiles;

        var status = DriverCacheStatus.Healthy;
        string? message = null;

        if (totalFiles == 0)
        {
            status = DriverCacheStatus.InvalidatedByUpdate;
            message = "Cache is currently empty or recently cleared after a driver update.";
        }

        return new DriverInfo(
            Vendor: GpuVendor.Nvidia,
            DeviceName: deviceName,
            DriverName: "NVIDIA Proprietary Driver",
            DriverVersion: driverVersion,
            ShaderCachePath: globalCachePath,
            CacheSizeBytes: globalSize,
            CachedFilesCount: globalFiles,
            SteamShaderCacheSizeBytes: steamSize,
            SteamShaderCacheFilesCount: steamFiles,
            TotalCacheSizeBytes: totalSize,
            Status: status,
            StatusMessage: message
        );
    }

    public async Task<(long Bytes, int Files, string Path)> GetGameCacheStatsAsync(string gameId, GameSource source, string? prefixPath = null)
    {
        return await Task.Run(() =>
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (source == GameSource.Steam)
            {
                string targetPath = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache", gameId);
                if (Directory.Exists(targetPath))
                {
                    try
                    {
                        var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories);
                        long bytes = 0;
                        foreach (var f in files)
                        {
                            bytes += new FileInfo(f).Length;
                        }
                        return (bytes, files.Length, targetPath);
                    }
                    catch { }
                }
            }
            else if (source == GameSource.Heroic && !string.IsNullOrEmpty(prefixPath))
            {
                string targetPath = Path.Combine(prefixPath, "shadercache");
                if (Directory.Exists(targetPath))
                {
                    try
                    {
                        var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories);
                        long bytes = 0;
                        foreach (var f in files)
                        {
                            bytes += new FileInfo(f).Length;
                        }
                        return (bytes, files.Length, targetPath);
                    }
                    catch { }
                }
            }

            return (0L, 0, string.Empty);
        });
    }

    public async Task ClearCacheAsync(CancellationToken cancellationToken = default)
    {
        var info = await GetDriverInfoAsync(cancellationToken);
        if (Directory.Exists(info.ShaderCachePath))
        {
            await Task.Run(() =>
            {
                var dirs = Directory.GetDirectories(info.ShaderCachePath);
                foreach (var dir in dirs)
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
                var files = Directory.GetFiles(info.ShaderCachePath);
                foreach (var file in files)
                {
                    try { File.Delete(file); } catch { }
                }
            }, cancellationToken);
        }
    }

    public async Task ClearGameCacheAsync(string gameId, GameSource source, string? prefixPath = null, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (source == GameSource.Steam)
            {
                string targetPath = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache", gameId);
                if (Directory.Exists(targetPath))
                {
                    try { Directory.Delete(targetPath, true); } catch { }
                }
            }
            else if (source == GameSource.Heroic && !string.IsNullOrEmpty(prefixPath))
            {
                string targetPath = Path.Combine(prefixPath, "shadercache");
                if (Directory.Exists(targetPath))
                {
                    try { Directory.Delete(targetPath, true); } catch { }
                }
            }
        }, cancellationToken);
    }

    public async Task ClearAllCachesAsync(CancellationToken cancellationToken = default)
    {
        // 1. Clear Global GPU Driver Cache
        await ClearCacheAsync(cancellationToken);

        // 2. Clear All Steam Game Shader Caches
        await Task.Run(() =>
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string steamShaderCache = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache");
            if (Directory.Exists(steamShaderCache))
            {
                var dirs = Directory.GetDirectories(steamShaderCache);
                foreach (var dir in dirs)
                {
                    try { Directory.Delete(dir, true); } catch { }
                }
            }
        }, cancellationToken);
    }

    public string GetRecommendedEnvironmentVariables()
    {
        return "__GL_SHADER_DISK_CACHE=1\n__GL_SHADER_DISK_CACHE_SKIP_CLEANUP=1\n__GL_SHADER_DISK_CACHE_SIZE=10737418240";
    }
}
