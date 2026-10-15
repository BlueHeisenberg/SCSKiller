using System.Diagnostics;
using SCSKiller.Linux.Core.Models;

namespace SCSKiller.Linux.Core.Drivers;

public class MesaBackend : IDriverBackend
{
    private readonly GpuVendor _vendor;

    public MesaBackend(GpuVendor vendor = GpuVendor.Amd)
    {
        _vendor = vendor;
    }

    public GpuVendor Vendor => _vendor;

    public async Task<DriverInfo> GetDriverInfoAsync(CancellationToken cancellationToken = default)
    {
        string deviceName = _vendor == GpuVendor.Amd ? "AMD Radeon GPU" : "Intel Graphics";
        string driverVersion = "Mesa";

        // 1. Try vulkaninfo --summary first (works on pure Wayland without mesa-utils/glxinfo)
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "vulkaninfo",
                    Arguments = "--summary",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
            await proc.WaitForExitAsync(cancellationToken);

            foreach (var line in output.Split('\n'))
            {
                if (line.Contains("deviceName", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
                {
                    var name = line.Split('=', 2)[1].Trim();
                    if (!name.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase))
                        deviceName = name;
                }
                else if (line.Contains("driverInfo", StringComparison.OrdinalIgnoreCase) && line.Contains('='))
                {
                    var info = line.Split('=', 2)[1].Trim();
                    if (!string.IsNullOrWhiteSpace(info))
                        driverVersion = info;
                }
            }
        }
        catch { }

        // 2. Fallback to glxinfo -B if deviceName is still generic
        if (deviceName is "AMD Radeon GPU" or "Intel Graphics")
        {
            try
            {
                using var proc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "glxinfo",
                        Arguments = "-B",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                proc.Start();
                var output = await proc.StandardOutput.ReadToEndAsync(cancellationToken);
                await proc.WaitForExitAsync(cancellationToken);

                foreach (var line in output.Split('\n'))
                {
                    if (line.Contains("OpenGL renderer string:", StringComparison.OrdinalIgnoreCase))
                    {
                        deviceName = line.Split(':', 2)[1].Trim();
                    }
                    else if (line.Contains("OpenGL version string:", StringComparison.OrdinalIgnoreCase))
                    {
                        driverVersion = line.Split(':', 2)[1].Trim();
                    }
                }
            }
            catch { }
        }

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string? envCacheDir = Environment.GetEnvironmentVariable("MESA_SHADER_CACHE_DIR");
        string[] candidateDirs = new[]
        {
            !string.IsNullOrWhiteSpace(envCacheDir) ? Path.Combine(envCacheDir, "mesa_shader_cache_db") : "",
            !string.IsNullOrWhiteSpace(envCacheDir) ? Path.Combine(envCacheDir, "mesa_shader_cache_sf") : "",
            !string.IsNullOrWhiteSpace(envCacheDir) ? envCacheDir : "",
            Path.Combine(home, ".cache", "mesa_shader_cache_db"),
            Path.Combine(home, ".cache", "mesa_shader_cache_sf"),
            Path.Combine(home, ".cache", "mesa_shader_cache")
        }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();

        string cachePath = candidateDirs.FirstOrDefault(Directory.Exists)
            ?? Path.Combine(home, ".cache", "mesa_shader_cache_db");

        long globalSize = 0;
        int globalFiles = 0;

        foreach (var dir in candidateDirs.Where(Directory.Exists))
        {
            try
            {
                var files = Directory.GetFiles(dir, "*", SearchOption.AllDirectories);
                globalFiles += files.Length;
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
        var status = (Directory.Exists(cachePath) || totalSize > 0) ? DriverCacheStatus.Healthy : DriverCacheStatus.DirectoryMissing;
        string? message = status == DriverCacheStatus.DirectoryMissing ? "Mesa shader cache directory not yet created." : null;

        return new DriverInfo(
            Vendor: _vendor,
            DeviceName: deviceName,
            DriverName: _vendor == GpuVendor.Amd ? "Mesa RADV (ACO)" : "Mesa ANV",
            DriverVersion: driverVersion,
            ShaderCachePath: cachePath,
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
            long totalBytes = 0;
            int totalFiles = 0;
            string foundPath = string.Empty;

            if (source == GameSource.Steam)
            {
                string targetPath = Path.Combine(home, ".local", "share", "Steam", "steamapps", "shadercache", gameId);
                if (Directory.Exists(targetPath))
                {
                    try
                    {
                        var files = Directory.GetFiles(targetPath, "*", SearchOption.AllDirectories);
                        foreach (var f in files) totalBytes += new FileInfo(f).Length;
                        totalFiles += files.Length;
                        foundPath = targetPath;
                    }
                    catch { }
                }
            }

            if (!string.IsNullOrEmpty(prefixPath))
            {
                string prefixCache = Path.Combine(prefixPath, "shadercache");
                if (Directory.Exists(prefixCache))
                {
                    try
                    {
                        var files = Directory.GetFiles(prefixCache, "*", SearchOption.AllDirectories);
                        foreach (var f in files) totalBytes += new FileInfo(f).Length;
                        totalFiles += files.Length;
                        if (string.IsNullOrEmpty(foundPath)) foundPath = prefixCache;
                    }
                    catch { }
                }
            }

            return (totalBytes, totalFiles, foundPath);
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
        }, cancellationToken);
    }

    public async Task ClearAllCachesAsync(CancellationToken cancellationToken = default)
    {
        // 1. Clear Mesa Driver Shader Cache
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
        return "MESA_SHADER_CACHE_MAX_SIZE=10G\nRADV_PERFTEST=aco";
    }
}
