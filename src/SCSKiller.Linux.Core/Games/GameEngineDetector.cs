using SCSKiller.Linux.Core.Models;
using SCSKiller.Linux.Core.Pipelines;

namespace SCSKiller.Linux.Core.Games;

public static class GameEngineDetector
{
    private static readonly string[] IgnoredExeSubstrings =
    {
        "CrashReport",
        "UnityCrashHandler",
        "DXSETUP",
        "vcredist",
        "vc_redist",
        "EasyAntiCheat",
        "BEService",
        "BattlEye",
        "EpicWebHelper",
        "UnrealCEFSubProcess",
        "D3D12StateObjectCompiler",
        "d3dconfig",
        "7za.exe",
        "REDprelauncher",
        "ErrorReporter",
        "InstallerMessage",
        "WinMTR-Report",
        "minidump-stackwalk",
        "benchmark",
        ".original.exe"
    };

    public static (GameEngine Engine, int EstimatedShaders) Detect(string installPath)
    {
        if (!Directory.Exists(installPath))
        {
            return (GameEngine.Unknown, 0);
        }

        try
        {
            // 1. REDengine (Cyberpunk 2077 / The Witcher 3)
            if (File.Exists(Path.Combine(installPath, "bin", "x64", "Cyberpunk2077.exe")) ||
                File.Exists(Path.Combine(installPath, "r6", "config", "settings.json")))
            {
                return (GameEngine.RedEngine, 12000);
            }
            if (File.Exists(Path.Combine(installPath, "bin", "x64", "witcher3.exe")) ||
                File.Exists(Path.Combine(installPath, "bin", "x64_dx12", "witcher3.exe")) ||
                File.Exists(Path.Combine(installPath, "content", "content0", "shaderdx12_0.cache")))
            {
                return (GameEngine.RedEngine, 8500);
            }

            // 2. FromSoftware (Elden Ring, Nightreign, Dark Souls III, Dark Souls Remastered)
            if (File.Exists(Path.Combine(installPath, "Game", "eldenring.exe")) ||
                File.Exists(Path.Combine(installPath, "eldenring.exe")) ||
                File.Exists(Path.Combine(installPath, "Game", "nightreign.exe")) ||
                File.Exists(Path.Combine(installPath, "nightreign.exe")) ||
                File.Exists(Path.Combine(installPath, "Game", "DarkSoulsIII.exe")) ||
                File.Exists(Path.Combine(installPath, "DarkSoulsRemastered.exe")))
            {
                return (GameEngine.FromSoftware, 9500);
            }

            // 3. Capcom RE Engine
            if (Directory.EnumerateFiles(installPath, "re_chunk_*.pak", SearchOption.TopDirectoryOnly).Any())
            {
                return (GameEngine.ReEngine, 7200);
            }

            // 4. Gaijin Dagor Engine & Remedy Northlight & Eidos Dawn Engine
            if (Directory.Exists(Path.Combine(installPath, "compiledShaders")) ||
                Directory.Exists(Path.Combine(installPath, "data", "shaders", "build", "pc_dxil")) ||
                File.Exists(Path.Combine(installPath, "bin", "rawpso.store2")))
            {
                return (GameEngine.DirectX12, 8500);
            }

            // 5. Unreal Engine 4 & 5
            bool hasEngineDir = Directory.Exists(Path.Combine(installPath, "Engine"));
            var ucasFiles = Directory.EnumerateFiles(installPath, "*.ucas", SearchOption.AllDirectories).Take(5).ToList();
            var pakFiles = Directory.EnumerateFiles(installPath, "*.pak", SearchOption.AllDirectories).Take(10).ToList();

            if (ucasFiles.Count > 0)
            {
                return (GameEngine.UnrealEngine5, 14000);
            }
            if (pakFiles.Count > 0 || hasEngineDir)
            {
                return (GameEngine.UnrealEngine4, 8000);
            }

            // 6. Unity
            var dataDirs = Directory.EnumerateDirectories(installPath, "*_Data", SearchOption.TopDirectoryOnly).ToList();
            if (dataDirs.Count > 0 || File.Exists(Path.Combine(installPath, "UnityPlayer.dll")))
            {
                return (GameEngine.Unity, 4500);
            }

            // 7. Generic DirectX 12
            var dx12Files = Directory.EnumerateFiles(installPath, "d3d12*.dll", SearchOption.AllDirectories).Take(1).ToList();
            if (dx12Files.Count > 0)
            {
                return (GameEngine.DirectX12, 6000);
            }
        }
        catch
        {
            // Ignore access errors on individual directories
        }

        return (GameEngine.DirectX11, 3500);
    }

    public static string? FindExecutable(string installPath)
    {
        if (!Directory.Exists(installPath)) return null;

        try
        {
            var exeFiles = Directory.EnumerateFiles(installPath, "*.exe", SearchOption.AllDirectories)
                .Where(f => !IgnoredExeSubstrings.Any(ig => f.Contains(ig, StringComparison.OrdinalIgnoreCase)) &&
                            !f.Contains("/Engine/Binaries/", StringComparison.OrdinalIgnoreCase) &&
                            !f.Contains("\\Engine\\Binaries\\", StringComparison.OrdinalIgnoreCase) &&
                            !f.Contains("/crashreporter/", StringComparison.OrdinalIgnoreCase) &&
                            !f.Contains("\\crashreporter\\", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (exeFiles.Count == 0) return null;

            var shippingExe = exeFiles.FirstOrDefault(f => f.Contains("Shipping", StringComparison.OrdinalIgnoreCase));
            string candidate = shippingExe
                ?? exeFiles.FirstOrDefault(f => f.Contains("Win64", StringComparison.OrdinalIgnoreCase) || f.Contains("x64_dx12", StringComparison.OrdinalIgnoreCase) || f.Contains("x64", StringComparison.OrdinalIgnoreCase))
                ?? exeFiles[0];

            try
            {
                string resolved = SCSKiller.Core.Games.GameFiles.GameExe(installPath, candidate);
                if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                    return resolved;
            }
            catch { }

            return candidate;
        }
        catch
        {
            return null;
        }
    }
}
