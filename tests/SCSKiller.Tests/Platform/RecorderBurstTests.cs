using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SCSKiller.Core.Planning;
using SCSKiller.Tests.Planning;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Platform;

/// <summary>`selftest burst` (WARP): a game's startup compile, 32 threads creating 150,000 PSOs at once, every fourth the desc
/// of an earlier one, through the recording proxy. The recorder writes its files in batches, not per create under its lock
/// (which held such a burst to about 10,000 creates/s), and everything it recorded is on disk at exit, before "#end".</summary>
public class RecorderBurstTests(ITestOutputHelper output)
{
    const int N = 150000, Threads = 32, Shaders = 256;

    (string Out, string Dir) Burst(string mode)
    {
        var dir = Directory.CreateTempSubdirectory("scsk-burst-").FullName;
        File.Copy(Path.Combine(Ff7.ProxyBin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(Ff7.ProxyBin, "d3d12.dll"), Path.Combine(dir, "d3d12.dll"));
        var psi = new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), $"burst {N} {Threads} {mode} {Shaders}") { RedirectStandardOutput = true };
        psi.Environment["SCSKILLER_TEST_RECORDER_DIR"] = Path.Combine(dir, "recorder");
        psi.Environment["SCSKILLER_TEST_LEDGER_DIR"] = Path.Combine(dir, "ledger");
        psi.Environment["SCSKILLER_DATA_DIR"] = Path.Combine(dir, "data");
        using var p = Process.Start(psi)!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        output.WriteLine(o.Trim());
        Assert.True(p.ExitCode == 0, o);
        return (o, dir);
    }

    [Fact]
    public void A_startup_burst_is_recorded_in_batched_writes_and_all_of_it_is_on_disk_at_exit()
    {
        if (!File.Exists(Path.Combine(Ff7.ProxyBin, "selftest.exe"))) return;
        static long Writes(string o) => long.Parse(Regex.Match(o, @"(\d+) writes").Groups[1].Value, CultureInfo.InvariantCulture);
        var plain = Writes(Burst("plain").Out);   // WARP's own writes
        var (o, dir) = Burst("rec");
        Assert.True(Writes(o) < plain + N / 50, $"{Writes(o)} file writes for {N} creates, {plain} without the recorder");
        var csv = File.ReadAllLines(Path.Combine(dir, "scskiller_creates.csv"));
        Assert.Equal(N, csv.Count(l => !l.StartsWith('#')));
        Assert.StartsWith("#end,", csv[^1]);
        Assert.DoesNotContain('\n', File.ReadAllText(Path.Combine(dir, "scskiller_creates.csv")).Replace("\r\n", ""));   // as the text-mode stream wrote it
        var descs = Enumerable.Range(0, N).Select(i => i % 4 == 3 ? i / 2 : i).Distinct().Count();
        Assert.Equal(descs, PsoDb.Read(Path.Combine(dir, "scskiller.db")).Count(r => r.Tag == 'G'));
    }
}
