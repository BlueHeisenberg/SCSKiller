using SCSKiller.Linux.Core.Models;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SCSKiller.Linux.Core.Drivers;

public record WarmerEvent(string Event, int? Threads = null, int? Done = null, int? Total = null, int? Failed = null, string? Message = null);

public class ProtonRunner
{
    private static readonly string HomeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string? FindProtonBinaryForGame(GameInfo game, string? preferredVersion = null)
    {
        string steamRoot = Path.Combine(HomeDir, ".local", "share", "Steam");

        if (game.Source == GameSource.Steam && !string.IsNullOrEmpty(game.Id))
        {
            // 1. Check compatdata/<AppID>/config_info which records the exact Proton installation path used for the game's prefix
            var configInfoCandidates = new List<string>
            {
                Path.Combine(steamRoot, "steamapps", "compatdata", game.Id, "config_info")
            };
            if (!string.IsNullOrEmpty(game.PrefixPath))
            {
                string? compatDir = Path.GetDirectoryName(game.PrefixPath.TrimEnd(Path.DirectorySeparatorChar));
                if (!string.IsNullOrEmpty(compatDir))
                {
                    configInfoCandidates.Insert(0, Path.Combine(compatDir, "config_info"));
                }
            }

            foreach (var configInfo in configInfoCandidates)
            {
                if (File.Exists(configInfo))
                {
                    try
                    {
                        foreach (var line in File.ReadLines(configInfo))
                        {
                            int filesIdx = line.IndexOf("/files/", StringComparison.Ordinal);
                            if (filesIdx > 0)
                            {
                                string protonRoot = line.Substring(0, filesIdx);
                                string protonBin = Path.Combine(protonRoot, "proton");
                                if (File.Exists(protonBin))
                                    return protonBin;
                            }
                        }
                    }
                    catch { }
                }
            }

            // 2. Check Steam's config/config.vdf CompatToolMapping for this AppID (or "0" global default)
            string configVdf = Path.Combine(steamRoot, "config", "config.vdf");
            if (File.Exists(configVdf))
            {
                try
                {
                    string vdf = File.ReadAllText(configVdf);
                    string? compatBlock = ExtractVdfBlock(vdf, "CompatToolMapping");
                    if (!string.IsNullOrEmpty(compatBlock))
                    {
                        foreach (var key in new[] { game.Id, "0" })
                        {
                            var match = Regex.Match(compatBlock, $@"""{Regex.Escape(key)}""\s*\{{[^}}]*""name""\s+""([^""]+)""", RegexOptions.Singleline);
                            if (match.Success)
                            {
                                string toolName = match.Groups[1].Value;
                                string? resolved = ResolveCompatToolName(toolName, steamRoot);
                                if (resolved != null)
                                    return resolved;
                            }
                        }
                    }
                }
                catch { }
            }
        }

        return FindProtonBinary(preferredVersion);
    }

    private static string? ExtractVdfBlock(string vdf, string blockName)
    {
        var match = Regex.Match(vdf, $@"""{Regex.Escape(blockName)}""\s*\{{", RegexOptions.IgnoreCase);
        if (!match.Success) return null;

        int startBrace = match.Index + match.Length - 1;
        int depth = 0;
        bool inQuotes = false;

        for (int i = startBrace; i < vdf.Length; i++)
        {
            char c = vdf[i];
            if (c == '"' && (i == 0 || vdf[i - 1] != '\\'))
            {
                inQuotes = !inQuotes;
            }
            else if (!inQuotes)
            {
                if (c == '{') depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return vdf.Substring(startBrace, i - startBrace + 1);
                    }
                }
            }
        }

        return null;
    }

    private static string? ResolveCompatToolName(string toolName, string steamRoot)
    {
        if (string.IsNullOrWhiteSpace(toolName)) return null;

        var candidates = new List<string>
        {
            Path.Combine(steamRoot, "compatibilitytools.d", toolName, "proton"),
            Path.Combine("/usr/share/steam/compatibilitytools.d", toolName, "proton"),
            Path.Combine(steamRoot, "steamapps", "common", toolName, "proton")
        };

        if (toolName.Equals("proton_experimental", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "Proton - Experimental", "proton"));
        else if (toolName.StartsWith("proton_9", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "Proton 9.0", "proton"));
        else if (toolName.StartsWith("proton_8", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(steamRoot, "steamapps", "common", "Proton 8.0", "proton"));

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string? FindProtonBinary(string? preferredVersion = null)
    {
        var candidates = new List<string>();

        // 1. User specified preferred Proton in compatibilitytools.d
        if (!string.IsNullOrEmpty(preferredVersion))
        {
            candidates.Add(Path.Combine(HomeDir, ".local", "share", "Steam", "compatibilitytools.d", preferredVersion, "proton"));
            candidates.Add(Path.Combine("/usr/share/steam/compatibilitytools.d", preferredVersion, "proton"));
        }

        // 2. Custom GE-Proton / CachyOS Proton installations
        string userToolsDir = Path.Combine(HomeDir, ".local", "share", "Steam", "compatibilitytools.d");
        if (Directory.Exists(userToolsDir))
        {
            foreach (var dir in Directory.GetDirectories(userToolsDir).OrderByDescending(d => d))
            {
                candidates.Add(Path.Combine(dir, "proton"));
            }
        }

        string sysToolsDir = "/usr/share/steam/compatibilitytools.d";
        if (Directory.Exists(sysToolsDir))
        {
            foreach (var dir in Directory.GetDirectories(sysToolsDir).OrderByDescending(d => d))
            {
                candidates.Add(Path.Combine(dir, "proton"));
            }
        }

        // 3. Official Steam Proton releases
        string steamCommon = Path.Combine(HomeDir, ".local", "share", "Steam", "steamapps", "common");
        if (Directory.Exists(steamCommon))
        {
            candidates.Add(Path.Combine(steamCommon, "Proton - Experimental", "proton"));
            candidates.Add(Path.Combine(steamCommon, "Proton 9.0 (Beta)", "proton"));
            candidates.Add(Path.Combine(steamCommon, "Proton 9.0", "proton"));
            candidates.Add(Path.Combine(steamCommon, "Proton 8.0", "proton"));
        }

        // 4. System path proton
        candidates.Add("/usr/bin/proton");

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    public static string? FindWarmerBinary()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Native", "scskiller_warm.exe"),
            Path.Combine(AppContext.BaseDirectory, "scskiller_warm.exe"),
            Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "bin", "scskiller_warm.exe"),
            Path.Combine(Directory.GetCurrentDirectory(), "src", "SCSKiller.Linux.Core", "Native", "scskiller_warm.exe")
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    public static async Task<bool> RunWarmerAsync(
        GameInfo game,
        string workDir,
        Action<WarmerEvent> onEvent,
        CancellationToken cancellationToken = default)
    {
        string? warmerExe = FindWarmerBinary();
        if (warmerExe == null)
        {
            onEvent(new WarmerEvent("error", Message: "Native warmer executable (scskiller_warm.exe) not found."));
            return false;
        }

        string? protonPath = FindProtonBinaryForGame(game);
        if (protonPath == null)
        {
            onEvent(new WarmerEvent("error", Message: "Proton runtime not found on this system."));
            return false;
        }

        string compatClient = Path.Combine(HomeDir, ".local", "share", "Steam");
        string compatData;
        if (!string.IsNullOrEmpty(game.PrefixPath))
        {
            // Proton expects STEAM_COMPAT_DATA_PATH to be the parent of 'pfx' if 'pfx' is passed
            compatData = game.PrefixPath.EndsWith("/pfx", StringComparison.Ordinal) || game.PrefixPath.EndsWith("\\pfx", StringComparison.Ordinal)
                ? Path.GetDirectoryName(game.PrefixPath)!
                : game.PrefixPath;
        }
        else if (game.Source == GameSource.Steam)
        {
            compatData = Path.Combine(compatClient, "steamapps", "compatdata", game.Id);
        }
        else
        {
            compatData = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "prefixes", game.Id);
        }

        string gameCompatData = compatData;
        if (!Directory.Exists(Path.Combine(compatData, "pfx")))
        {
            string sharedPrefix = Path.Combine(SCSKiller.Linux.Core.AppPaths.DataDir, "proton_prefix");
            if (Directory.Exists(Path.Combine(sharedPrefix, "pfx")))
            {
                compatData = sharedPrefix;
            }
        }
        Directory.CreateDirectory(compatData);

        // Target cache directory where driver will write compiled pipelines
        string targetCachePath = game.Source == GameSource.Steam
            ? Path.Combine(compatClient, "steamapps", "shadercache", game.Id)
            : (!string.IsNullOrEmpty(game.PrefixPath)
                ? Path.Combine(game.PrefixPath, "shadercache", game.Id)
                : Path.Combine(compatClient, "steamapps", "shadercache", game.Id));

        Directory.CreateDirectory(targetCachePath);
        string vkd3dCache = Path.Combine(targetCachePath, "VKD3D_shader_cache");
        string dxvkCache = Path.Combine(targetCachePath, "DXVK_state_cache");
        string nvidiaBase = Path.Combine(targetCachePath, "nvidiav1");
        Directory.CreateDirectory(nvidiaBase);
        string glCache = Path.Combine(nvidiaBase, "GLCache");
        Directory.CreateDirectory(vkd3dCache);
        Directory.CreateDirectory(dxvkCache);
        Directory.CreateDirectory(glCache);

        string amdCache = Path.Combine(targetCachePath, "AMDv1");
        Directory.CreateDirectory(amdCache);
        string mesaCache = targetCachePath;
        Directory.CreateDirectory(mesaCache);

        string exeName = !string.IsNullOrEmpty(game.ExecutablePath) && game.ExecutablePath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFileName(game.ExecutablePath)
            : "game.exe";

        // If the game ships with Agility SDK D3D12 directory, link it into workDir
        string? gameExeDir = !string.IsNullOrEmpty(game.ExecutablePath) ? Path.GetDirectoryName(game.ExecutablePath) : null;
        string workD3D12 = Path.Combine(workDir, "D3D12");
        if (!string.IsNullOrEmpty(gameExeDir))
        {
            string gameD3D12 = Path.Combine(gameExeDir, "D3D12");
            if (Directory.Exists(gameD3D12) && !Directory.Exists(workD3D12))
            {
                try
                {
                    Directory.CreateSymbolicLink(workD3D12, gameD3D12);
                }
                catch
                {
                    try { CopyDirectory(gameD3D12, workD3D12); } catch { }
                }
            }
        }

        // Ensure the warmer uses the exact same VKD3D-Proton and DXVK DLLs as the target Proton runtime / game prefix
        var stagedDlls = StageProtonRuntimeDlls(game, protonPath, gameCompatData, compatData, workDir);

        // Prepare existing driver caches so the warmer appends to the merged cache and invalidates stale Proton builds
        PrepareDriverCachesForWarmer(targetCachePath, vkd3dCache, glCache, protonPath, workDir);

        // Copy warmer binary to match the game's executable name so VKD3D-Proton and drivers key the cache identically
        string targetExe = Path.Combine(workDir, exeName);
        try
        {
            File.Copy(warmerExe, targetExe, overwrite: true);
        }
        catch
        {
            targetExe = warmerExe;
        }

        int threads = Math.Max(2, Environment.ProcessorCount);

        var startInfo = new ProcessStartInfo
        {
            FileName = "nice",
            Arguments = $"-n 10 \"{protonPath}\" runinprefix \"{targetExe}\" \"{workDir}\" \"{exeName}\" --threads {threads}",
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        startInfo.EnvironmentVariables["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = compatClient;
        startInfo.EnvironmentVariables["STEAM_COMPAT_DATA_PATH"] = compatData;
        startInfo.EnvironmentVariables["STEAM_COMPAT_SHADER_PATH"] = targetCachePath;
        if (game.Source == GameSource.Steam)
        {
            startInfo.EnvironmentVariables["SteamAppId"] = game.Id;
            startInfo.EnvironmentVariables["SteamGameId"] = game.Id;
        }
        startInfo.EnvironmentVariables["WINEDLLOVERRIDES"] = "d3d12,d3d12core,dxgi,d3d11=n,b";
        startInfo.EnvironmentVariables["PROTON_USE_XALIA"] = "0";
        startInfo.EnvironmentVariables["VKD3D_SHADER_CACHE_PATH"] = vkd3dCache;
        startInfo.EnvironmentVariables["DXVK_STATE_CACHE_PATH"] = dxvkCache;
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_PATH"] = nvidiaBase;
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE"] = "1";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_APP_NAME"] = "steamapp_shader_cache";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME"] = "steam_shader_cache;steamapp_merged_shader_cache";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_SKIP_CLEANUP"] = "1";
        startInfo.EnvironmentVariables["__GL_SHADER_DISK_CACHE_SIZE"] = "10737418240";
        startInfo.EnvironmentVariables["AMD_VK_PIPELINE_CACHE_PATH"] = amdCache;
        startInfo.EnvironmentVariables["AMD_VK_PIPELINE_CACHE_FILENAME"] = "steamapp_shader_cache";
        startInfo.EnvironmentVariables["AMD_VK_USE_PIPELINE_CACHE"] = "1";
        startInfo.EnvironmentVariables["MESA_SHADER_CACHE_DIR"] = mesaCache;
        startInfo.EnvironmentVariables["MESA_SHADER_CACHE_MAX_SIZE"] = "10G";
        startInfo.EnvironmentVariables["MESA_GLSL_CACHE_DIR"] = mesaCache;
        startInfo.EnvironmentVariables["MESA_GLSL_CACHE_MAX_SIZE"] = "10G";
        startInfo.EnvironmentVariables["MESA_DISK_CACHE_SINGLE_FILE"] = "1";

        using var process = new Process { StartInfo = startInfo };

        process.OutputDataReceived += (s, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            string line = e.Data.Trim();
            if (line.StartsWith('{') && line.EndsWith('}'))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string ev = root.GetProperty("event").GetString() ?? "";

                    int? th = root.TryGetProperty("threads", out var thProp) ? thProp.GetInt32() : null;
                    int? done = root.TryGetProperty("done", out var dProp) ? dProp.GetInt32() : null;
                    int? total = root.TryGetProperty("total", out var tProp) ? tProp.GetInt32() : null;
                    int? failed = root.TryGetProperty("failed", out var fProp) ? fProp.GetInt32() : null;
                    string? msg = root.TryGetProperty("message", out var mProp) ? root.GetProperty("message").GetString() : null;

                    onEvent(new WarmerEvent(ev, th, done, total, failed, msg));
                }
                catch
                {
                    // Ignore non-json lines
                }
            }
        };

        process.ErrorDataReceived += (s, e) =>
        {
            // Wine / Proton diagnostic logs
        };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var reg = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
            });

            await process.WaitForExitAsync(cancellationToken);

            FinalizeDriverCaches(targetCachePath, vkd3dCache, glCache, gameExeDir);

            try
            {
                EnsureProtonLocalShaderCacheSetting(gameCompatData, protonPath);
                if (!string.Equals(compatData, gameCompatData, StringComparison.Ordinal))
                {
                    EnsureProtonLocalShaderCacheSetting(compatData, protonPath);
                }
            }
            catch { }

            return process.ExitCode == 0;
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
            return false;
        }
        catch (Exception ex)
        {
            onEvent(new WarmerEvent("error", Message: ex.Message));
            return false;
        }
        finally
        {
            try
            {
                if (targetExe != warmerExe && File.Exists(targetExe))
                    File.Delete(targetExe);
            }
            catch { }

            foreach (var dll in stagedDlls)
            {
                try { if (File.Exists(dll)) File.Delete(dll); } catch { }
            }
        }
    }

    private static List<string> StageProtonRuntimeDlls(GameInfo game, string protonPath, string gameCompatData, string activeCompatData, string workDir)
    {
        var staged = new List<string>();
        string? protonDir = Path.GetDirectoryName(protonPath);
        if (string.IsNullOrEmpty(protonDir)) return staged;

        string? wineLib64 = new[]
        {
            Path.Combine(protonDir, "files", "lib64", "wine"),
            Path.Combine(protonDir, "dist", "lib64", "wine"),
            Path.Combine(protonDir, "files", "lib", "wine"),
            Path.Combine(protonDir, "dist", "lib", "wine")
        }.FirstOrDefault(Directory.Exists);

        string gameSys32 = Path.Combine(gameCompatData, "pfx", "drive_c", "windows", "system32");
        string activeSys32 = Path.Combine(activeCompatData, "pfx", "drive_c", "windows", "system32");

        bool useVkd3dLowLatency = IsLowLatencyConfigured(game, "PROTON_VKD3D_LOWLATENCY");
        bool useDxvkLowLatency = IsLowLatencyConfigured(game, "PROTON_DXVK_LOWLATENCY");

        string vkd3dSubDir = useVkd3dLowLatency && wineLib64 != null && Directory.Exists(Path.Combine(wineLib64, "vkd3d-low-latency", "x86_64-windows"))
            ? Path.Combine("vkd3d-low-latency", "x86_64-windows")
            : Path.Combine("vkd3d-proton", "x86_64-windows");

        string dxvkSubDir = useDxvkLowLatency && wineLib64 != null && Directory.Exists(Path.Combine(wineLib64, "dxvk-low-latency", "x86_64-windows"))
            ? Path.Combine("dxvk-low-latency", "x86_64-windows")
            : Path.Combine("dxvk", "x86_64-windows");

        var dllMappings = new (string DllName, string ProtonSubDir)[]
        {
            ("d3d12.dll", vkd3dSubDir),
            ("d3d12core.dll", vkd3dSubDir),
            ("dxgi.dll", dxvkSubDir),
            ("d3d11.dll", dxvkSubDir)
        };

        foreach (var (dllName, subDir) in dllMappings)
        {
            string? sourceDll = null;

            // 1. Prefer the game prefix's own system32 DLL if Proton has already installed a non-symlink native DLL there
            if (Directory.Exists(gameSys32))
            {
                string candidate = Path.Combine(gameSys32, dllName);
                var fi = new FileInfo(candidate);
                if (fi.Exists && fi.LinkTarget == null && fi.Length > 65536)
                    sourceDll = candidate;
            }

            // 2. Otherwise resolve from the target Proton runtime directory
            if (sourceDll == null && wineLib64 != null)
            {
                string candidate = Path.Combine(wineLib64, subDir, dllName);
                if (File.Exists(candidate))
                    sourceDll = candidate;
            }

            if (sourceDll == null) continue;

            // Keep active prefix system32 in sync when using the fallback shared prefix
            if (!string.Equals(activeCompatData, gameCompatData, StringComparison.Ordinal) && Directory.Exists(activeSys32))
            {
                try
                {
                    File.Copy(sourceDll, Path.Combine(activeSys32, dllName), overwrite: true);
                }
                catch { }
            }

            // Stage directly into workDir so Wine loads the exact matching DLL for scskiller_warm.exe
            string destDll = Path.Combine(workDir, dllName);
            if (!File.Exists(destDll))
            {
                try
                {
                    File.Copy(sourceDll, destDll, overwrite: false);
                    staged.Add(destDll);
                }
                catch { }
            }
        }

        return staged;
    }

    private static bool IsLowLatencyConfigured(GameInfo game, string envVarName)
    {
        if (string.Equals(Environment.GetEnvironmentVariable(envVarName), "1", StringComparison.Ordinal))
            return true;

        var bgmodCandidates = new List<string>();
        if (!string.IsNullOrEmpty(game.Name))
        {
            bgmodCandidates.Add(Path.Combine(HomeDir, ".local", "share", "goverlay", "gameconfig", game.Name, "bgmod.conf"));
        }
        bgmodCandidates.Add(Path.Combine(HomeDir, ".local", "share", "goverlay", "gameconfig", "global", "bgmod.conf"));

        foreach (var conf in bgmodCandidates)
        {
            if (File.Exists(conf))
            {
                try
                {
                    string text = File.ReadAllText(conf);
                    if (Regex.IsMatch(text, $@"^\s*{Regex.Escape(envVarName)}\s*=\s*1\b", RegexOptions.Multiline))
                        return true;
                }
                catch { }
            }
        }

        return false;
    }

    private static void PrepareDriverCachesForWarmer(string targetCachePath, string vkd3dCache, string glCache, string protonPath, string workDir)
    {
        try
        {
            string d3d12CorePath = Path.Combine(workDir, "d3d12core.dll");
            string d3d12CoreSig = File.Exists(d3d12CorePath)
                ? $"{new FileInfo(d3d12CorePath).Length}"
                : "default";
            string currentStamp = $"{protonPath}|{d3d12CoreSig}";
            string stampFile = Path.Combine(targetCachePath, "scskiller_runtime.stamp");

            bool stampChanged = true;
            if (File.Exists(stampFile))
            {
                string oldStamp = File.ReadAllText(stampFile).Trim();
                if (string.Equals(oldStamp, currentStamp, StringComparison.Ordinal))
                {
                    stampChanged = false;
                }
            }

            File.WriteAllText(stampFile, currentStamp);

            if (Directory.Exists(glCache))
            {
                var leafDirs = Directory.GetFiles(glCache, "*.bin", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToList();

                foreach (var dir in leafDirs)
                {
                    if (dir == null) continue;

                    string mergedBin = Path.Combine(dir, "steamapp_merged_shader_cache.bin");
                    string mergedToc = Path.Combine(dir, "steamapp_merged_shader_cache.toc");
                    string steamRoBin = Path.Combine(dir, "steam_shader_cache.bin");
                    string steamRoToc = Path.Combine(dir, "steam_shader_cache.toc");

                    if (stampChanged)
                    {
                        try { if (File.Exists(mergedBin)) File.Delete(mergedBin); } catch { }
                        try { if (File.Exists(mergedToc)) File.Delete(mergedToc); } catch { }
                        try { if (File.Exists(steamRoBin)) File.Delete(steamRoBin); } catch { }
                        try { if (File.Exists(steamRoToc)) File.Delete(steamRoToc); } catch { }
                    }

                    // Remove any 32-byte empty stub buckets before the warmer starts
                    foreach (var binFile in Directory.GetFiles(dir, "steamapp_shader_cache*.bin"))
                    {
                        var fi = new FileInfo(binFile);
                        if (fi.Length <= 64)
                        {
                            string tocFile = Path.ChangeExtension(binFile, ".toc");
                            try { fi.Delete(); } catch { }
                            try { if (File.Exists(tocFile)) File.Delete(tocFile); } catch { }
                        }
                    }

                    string bucket0Bin = Path.Combine(dir, "steamapp_shader_cache0.bin");
                    string bucket0Toc = Path.Combine(dir, "steamapp_shader_cache0.toc");

                    // Pre-seed steamapp_shader_cache0.bin/.toc from steamapp_merged_shader_cache.bin/.toc
                    // or from the largest game-written steamapp_shader_cacheN.bin/.toc so the warmer appends new misses
                    if (!stampChanged && File.Exists(mergedBin) && File.Exists(mergedToc) && new FileInfo(mergedBin).Length > 64)
                    {
                        try
                        {
                            File.Move(mergedBin, bucket0Bin, overwrite: true);
                            File.Move(mergedToc, bucket0Toc, overwrite: true);
                        }
                        catch { }
                    }
                    else if (!File.Exists(bucket0Bin))
                    {
                        var existingBuckets = Directory.GetFiles(dir, "steamapp_shader_cache*.bin")
                            .Select(f => new FileInfo(f))
                            .Where(f => f.Length > 64)
                            .OrderByDescending(f => f.Length)
                            .ToList();

                        if (existingBuckets.Count > 0)
                        {
                            var best = existingBuckets[0];
                            string bestToc = Path.ChangeExtension(best.FullName, ".toc");
                            if (File.Exists(bestToc))
                            {
                                try
                                {
                                    File.Move(best.FullName, bucket0Bin, overwrite: true);
                                    File.Move(bestToc, bucket0Toc, overwrite: true);
                                }
                                catch { }
                            }
                        }
                    }
                }
            }

            if (Directory.Exists(vkd3dCache))
            {
                bool promotedWrite = false;
                foreach (var writeFile in Directory.GetFiles(vkd3dCache, "*.cache.write"))
                {
                    if (new FileInfo(writeFile).Length > 48)
                    {
                        string finalCacheFile = writeFile.Substring(0, writeFile.Length - ".write".Length);
                        try
                        {
                            MergeOrReplaceVkd3dCache(writeFile, finalCacheFile);
                            File.Delete(writeFile);
                            promotedWrite = true;
                        }
                        catch { }
                    }
                }

                if (stampChanged && !promotedWrite)
                {
                    foreach (var cacheFile in Directory.GetFiles(vkd3dCache, "*.cache*"))
                    {
                        try { File.Delete(cacheFile); } catch { }
                    }
                }
            }
        }
        catch { }
    }

    public static void FinalizeDriverCaches(string targetCachePath, string vkd3dCache, string glCache, string? gameExeDir = null)
    {
        // 1. Flatten nested GLCache/GLCache if NVIDIA driver created one
        try
        {
            string nestedGlCache = Path.Combine(targetCachePath, "nvidiav1", "GLCache", "GLCache");
            if (Directory.Exists(nestedGlCache))
            {
                CopyDirectory(nestedGlCache, glCache);
                Directory.Delete(nestedGlCache, recursive: true);
            }
        }
        catch { }

        // 2. Promote NVIDIA per-process writable buckets (steamapp_shader_cache0/1/2.bin/.toc)
        // into lock-free shared read-only caches (steamapp_merged_shader_cache.bin/.toc).
        // In libnvidia-glcore.so, writable buckets (%s%d) use exclusive lockf() locks, so Wine background processes
        // can lock buckets 0 and 1 and prevent the game process from reading them.
        // Read-only caches (%s) in __GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME are opened without lockf() by every process.
        try
        {
            if (Directory.Exists(glCache))
            {
                var leafDirs = Directory.GetFiles(glCache, "*.bin", SearchOption.AllDirectories)
                    .Select(Path.GetDirectoryName)
                    .Where(d => !string.IsNullOrEmpty(d))
                    .Distinct()
                    .ToList();

                foreach (var dir in leafDirs)
                {
                    if (dir == null) continue;

                    var writableBins = Directory.GetFiles(dir, "steamapp_shader_cache*.bin")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Length)
                        .ToList();

                    // Remove 32-byte empty stub buckets
                    foreach (var stub in writableBins.Where(f => f.Length <= 64).ToList())
                    {
                        string stubToc = Path.ChangeExtension(stub.FullName, ".toc");
                        try { stub.Delete(); } catch { }
                        try { if (File.Exists(stubToc)) File.Delete(stubToc); } catch { }
                        writableBins.Remove(stub);
                    }

                    if (writableBins.Count == 0) continue;

                    string mergedBin = Path.Combine(dir, "steamapp_merged_shader_cache.bin");
                    string mergedToc = Path.Combine(dir, "steamapp_merged_shader_cache.toc");
                    string steamRoBin = Path.Combine(dir, "steam_shader_cache.bin");
                    string steamRoToc = Path.Combine(dir, "steam_shader_cache.toc");

                    // The largest writable bucket from the warmer run is promoted to steamapp_merged_shader_cache
                    var primaryBin = writableBins[0];
                    string primaryToc = Path.ChangeExtension(primaryBin.FullName, ".toc");
                    if (File.Exists(primaryToc))
                    {
                        try
                        {
                            File.Move(primaryBin.FullName, mergedBin, overwrite: true);
                            File.Move(primaryToc, mergedToc, overwrite: true);
                        }
                        catch { }
                    }

                    // Only populate steam_shader_cache if it doesn't exist yet (never overwrite existing non-empty steam_shader_cache)
                    long steamRoLen = File.Exists(steamRoBin) ? new FileInfo(steamRoBin).Length : 0;
                    if (steamRoLen <= 64)
                    {
                        if (writableBins.Count > 1)
                        {
                            var secondaryBin = writableBins[1];
                            string secondaryToc = Path.ChangeExtension(secondaryBin.FullName, ".toc");
                            if (File.Exists(secondaryToc))
                            {
                                try
                                {
                                    File.Move(secondaryBin.FullName, steamRoBin, overwrite: true);
                                    File.Move(secondaryToc, steamRoToc, overwrite: true);
                                }
                                catch { }
                            }
                        }
                        else if (File.Exists(mergedBin) && File.Exists(mergedToc))
                        {
                            try
                            {
                                File.Copy(mergedBin, steamRoBin, overwrite: true);
                                File.Copy(mergedToc, steamRoToc, overwrite: true);
                            }
                            catch { }
                        }
                    }
                }
            }
        }
        catch { }

        // 3. Promote VKD3D-Proton .cache.write files into finalized .cache files and sync to gameExeDir
        try
        {
            if (Directory.Exists(vkd3dCache))
            {
                foreach (var writeFile in Directory.GetFiles(vkd3dCache, "*.cache.write"))
                {
                    var writeInfo = new FileInfo(writeFile);
                    if (writeInfo.Length == 0) continue;

                    string finalCacheFile = writeFile.Substring(0, writeFile.Length - ".write".Length);
                    try
                    {
                        MergeOrReplaceVkd3dCache(writeFile, finalCacheFile);
                        File.Delete(writeFile);
                    }
                    catch
                    {
                        try { File.Copy(writeFile, finalCacheFile, overwrite: true); } catch { }
                    }
                }

                if (!string.IsNullOrEmpty(gameExeDir) && Directory.Exists(gameExeDir))
                {
                    var largestCache = Directory.GetFiles(vkd3dCache, "*.cache")
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Length)
                        .FirstOrDefault();

                    if (largestCache != null && largestCache.Length > 48)
                    {
                        try
                        {
                            File.Copy(largestCache.FullName, Path.Combine(gameExeDir, "vkd3d-proton.cache"), overwrite: true);
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    private static void MergeOrReplaceVkd3dCache(string writeFile, string finalCacheFile)
    {
        if (!File.Exists(finalCacheFile))
        {
            File.Copy(writeFile, finalCacheFile, overwrite: true);
            return;
        }

        byte[] writeBytes = File.ReadAllBytes(writeFile);
        byte[] finalBytes = File.ReadAllBytes(finalCacheFile);

        if (writeBytes.Length <= 48) return;
        if (finalBytes.Length <= 48)
        {
            File.WriteAllBytes(finalCacheFile, writeBytes);
            return;
        }

        // Compare 48-byte vkd3d_pipeline_blob header (magic, version, vkd3d_build, vendor, device, driver_version, uuid)
        bool sameHeader = writeBytes.AsSpan(0, 48).SequenceEqual(finalBytes.AsSpan(0, 48));
        if (!sameHeader)
        {
            // Runtime / driver changed: replace with the fresh cache
            File.WriteAllBytes(finalCacheFile, writeBytes);
            return;
        }

        // Same header: VKD3D-Proton cache after byte 48 is a concatenation of stream chunks.
        // Append new stream chunks from writeFile to finalCacheFile if writeFile is not already identical.
        if (writeBytes.Length != finalBytes.Length || !writeBytes.AsSpan().SequenceEqual(finalBytes))
        {
            using var fs = new FileStream(finalCacheFile, FileMode.Append, FileAccess.Write, FileShare.None);
            fs.Write(writeBytes, 48, writeBytes.Length - 48);
        }
    }

    private static void EnsureProtonLocalShaderCacheSetting(string compatData, string? protonPath = null)
    {
        const string settingsContent =
            "import os\n\n" +
            "_shader_path = os.environ.get(\"STEAM_COMPAT_SHADER_PATH\", \"\")\n" +
            "user_settings = {\n" +
            "    \"PROTON_LOCAL_SHADER_CACHE\": \"1\",\n" +
            "    \"__GL_SHADER_DISK_CACHE\": \"1\",\n" +
            "    \"__GL_SHADER_DISK_CACHE_SIZE\": \"10737418240\",\n" +
            "    \"__GL_SHADER_DISK_CACHE_SKIP_CLEANUP\": \"1\",\n" +
            "}\n" +
            "if _shader_path:\n" +
            "    for _sub in (\"nvidiav1\", \"VKD3D_shader_cache\", \"DXVK_state_cache\", \"AMDv1\", \"mesa\"):\n" +
            "        try:\n" +
            "            os.makedirs(os.path.join(_shader_path, _sub), exist_ok=True)\n" +
            "        except Exception:\n" +
            "            pass\n" +
            "    user_settings[\"__GL_SHADER_DISK_CACHE_PATH\"] = os.path.join(_shader_path, \"nvidiav1\")\n" +
            "    user_settings[\"__GL_SHADER_DISK_CACHE_APP_NAME\"] = \"steamapp_shader_cache\"\n" +
            "    user_settings[\"__GL_SHADER_DISK_CACHE_READ_ONLY_APP_NAME\"] = \"steam_shader_cache;steamapp_merged_shader_cache\"\n" +
            "    user_settings[\"VKD3D_SHADER_CACHE_PATH\"] = os.path.join(_shader_path, \"VKD3D_shader_cache\")\n" +
            "    user_settings[\"DXVK_STATE_CACHE_PATH\"] = os.path.join(_shader_path, \"DXVK_state_cache\")\n" +
            "    user_settings[\"AMD_VK_PIPELINE_CACHE_PATH\"] = os.path.join(_shader_path, \"AMDv1\")\n" +
            "    user_settings[\"AMD_VK_PIPELINE_CACHE_FILENAME\"] = \"steamapp_shader_cache\"\n" +
            "    user_settings[\"AMD_VK_USE_PIPELINE_CACHE\"] = \"1\"\n" +
            "    user_settings[\"MESA_SHADER_CACHE_DIR\"] = _shader_path\n" +
            "    user_settings[\"MESA_SHADER_CACHE_MAX_SIZE\"] = \"10G\"\n" +
            "    user_settings[\"MESA_GLSL_CACHE_DIR\"] = _shader_path\n" +
            "    user_settings[\"MESA_GLSL_CACHE_MAX_SIZE\"] = \"10G\"\n" +
            "    user_settings[\"MESA_DISK_CACHE_SINGLE_FILE\"] = \"1\"\n";

        var targetDirs = new List<string>();
        if (Directory.Exists(compatData))
        {
            targetDirs.Add(compatData);
        }

        if (!string.IsNullOrEmpty(protonPath))
        {
            string? protonDir = Path.GetDirectoryName(protonPath);
            if (!string.IsNullOrEmpty(protonDir) && Directory.Exists(protonDir))
                targetDirs.Add(protonDir);
        }

        string userCompatTools = Path.Combine(HomeDir, ".local", "share", "Steam", "compatibilitytools.d");
        if (Directory.Exists(userCompatTools))
        {
            targetDirs.AddRange(Directory.GetDirectories(userCompatTools));
        }

        string steamCommon = Path.Combine(HomeDir, ".local", "share", "Steam", "steamapps", "common");
        if (Directory.Exists(steamCommon))
        {
            foreach (var dir in Directory.GetDirectories(steamCommon, "Proton*"))
            {
                if (File.Exists(Path.Combine(dir, "proton")))
                    targetDirs.Add(dir);
            }
        }

        foreach (var dir in targetDirs.Distinct())
        {
            try
            {
                string settingsFile = Path.Combine(dir, "user_settings.py");
                if (!File.Exists(settingsFile) || !File.ReadAllText(settingsFile).Contains("STEAM_COMPAT_SHADER_PATH", StringComparison.Ordinal))
                {
                    File.WriteAllText(settingsFile, settingsContent);
                }
            }
            catch { }
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(dir));
            CopyDirectory(dir, dest);
        }
    }
}
