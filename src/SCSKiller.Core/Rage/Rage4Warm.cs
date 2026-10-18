using System.Diagnostics;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.Rage;

/// <summary>Launches the 32-bit GTA IV/DXVK warm helper under the game's executable name. The helper reads the loose
/// RAGE FXC effects, feeds their D3D9 SM3 shaders to the game's own DXVK vulkan.dll, and exits without changing game files.</summary>
public static partial class Rage4Warm
{
    public const string Helper = @"x86\scskiller_gta4_warm.exe";
    public sealed record Result(int Done, int Total, int Failed, double Seconds);

    [GeneratedRegex(@"done=(\d+) failed=(\d+) seconds=([0-9,.]+)", RegexOptions.CultureInvariant)]
    private static partial Regex DoneLine();

    public static async Task<Result> Run(Game game, string workDir, IProgress<string>? log, CancellationToken ct)
    {
        var helper = Warming.NativeTools.Find(Helper) ?? throw new FileNotFoundException($"{Helper} not found next to the app or in proxy\\build\\Release");
        var dir = Path.GetDirectoryName(Path.GetFullPath(game.ExePath))!;
        if (!File.Exists(Path.Combine(dir, "vulkan.dll"))) throw new FileNotFoundException("GTA IV DXVK vulkan.dll is missing");

        var stageDir = Path.Combine(workDir, "gta4-d3d9-warm");
        Directory.CreateDirectory(stageDir);
        var staged = Path.Combine(stageDir, Path.GetFileName(game.ExePath));
        File.Copy(helper, staged, true); // NVIDIA's Vulkan cache is keyed on the launched exe name.

        try
        {
            var psi = new ProcessStartInfo(staged)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = stageDir
            };
            psi.ArgumentList.Add(dir);
            psi.Environment["DXVK_LOG_LEVEL"] = "warn";
            psi.Environment["DXVK_LOG_PATH"] = workDir;

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("couldn't start the GTA IV warm helper");
            var output = p.StandardOutput.ReadToEndAsync(ct);
            var error = p.StandardError.ReadToEndAsync(ct);
            try { await p.WaitForExitAsync(ct); }
            catch (OperationCanceledException)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                throw;
            }

            var stdout = await output;
            var stderr = await error;
            foreach (var line in stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
                if (line.StartsWith("fxc=") || line.StartsWith("dxvk=") || line.StartsWith("done=")) log?.Report($"{game.Name}: {line}");
            if (!string.IsNullOrWhiteSpace(stderr))
                foreach (var line in stderr.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Take(20)) log?.Report($"{game.Name}: {line}");

            var m = DoneLine().Match(stdout);
            if (p.ExitCode != 0 || !m.Success)
                throw new InvalidOperationException($"GTA IV D3D9/DXVK warm failed (exit {p.ExitCode})" + (string.IsNullOrWhiteSpace(stderr) ? "" : $": {stderr.Trim()}"));
            var done = int.Parse(m.Groups[1].Value);
            var failed = int.Parse(m.Groups[2].Value);
            var seconds = double.Parse(m.Groups[3].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
            return new(done, done, failed, seconds);
        }
        finally
        {
            try { File.Delete(staged); } catch { }
        }
    }
}
