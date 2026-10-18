using System.Diagnostics;
using SCSKiller.Core;
using SCSKiller.Core.App;
using Xunit.Abstractions;

namespace SCSKiller.Tests.Platform;

// The in-game countdown of a before and after run: the ini lines the app writes, and the proxy drawing it (selftest on WARP).
[Collection(TimingCollection.Name)]
public sealed class OverlayIniTests(ITestOutputHelper output) : IDisposable
{
    readonly string _root = Path.Combine(Planning.Ff7.TempDir("overlay-tests"), Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        var keys = Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*.exe", SearchOption.AllDirectories).Select(ScsKiller.FileKey).ToHashSet() : [];
        foreach (var k in keys)
            try { File.Delete(Path.Combine(TestEnv.Recorder, "refused", k)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        try { Directory.Delete(_root, true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        TestEnv.AssertLiveRecorderUntouched(keys);   // the selftests and the proxy in them wrote only to the test recorder folder
    }

    [Theory]
    [InlineData(BeforeAfterStage.BeforeArmed, "before")]
    [InlineData(BeforeAfterStage.BeforePlaying, "before")]
    [InlineData(BeforeAfterStage.AfterArmed, "after")]
    [InlineData(BeforeAfterStage.AfterPlaying, "after")]
    [InlineData(BeforeAfterStage.Compiling, null)]
    [InlineData(BeforeAfterStage.AfterReady, null)]
    [InlineData(BeforeAfterStage.Done, null)]
    public void The_ini_asks_for_the_overlay_only_while_a_run_that_shows_it_is_armed_or_playing(BeforeAfterStage stage, string? tag)
    {
        var lines = ScsKiller.OverlayIni(new BeforeAfterRecord { Stage = stage, Minutes = 7 });
        if (tag == null) Assert.Equal("", lines);
        else Assert.Equal($"overlay=1\r\nab_play_s=420\r\nab_tag={tag}\r\n", lines);
        Assert.Equal("", ScsKiller.OverlayIni(new BeforeAfterRecord { Stage = stage, Minutes = 7, Overlay = false }));
        Assert.Equal("", ScsKiller.OverlayIni(null));
    }

    /// <summary>The proxy's in-game panel on WARP (`selftest overlayunit`, `selftest overlay`): the font, layout, painting,
    /// formats, graph, feed, stutter rule and play clock alone; then drawn at the top right, pixel for pixel as painted (the
    /// graph's pixels in its colours), from the full time to "Done" once the play time is counted, through a resize to 720p
    /// and 4K that no held back buffer fails; the csv gets #swapchain and #overlay. Play counts from the first present.
    /// On D3D12 and D3D11 (the proxy as d3d11.dll, frames_only=1): 8-bit and 10-bit SDR, HDR10 (SetColorSpace1)
    /// and scRGB FP16 on D3D12. A compile blocking a frame of 50 ms or more in play flashes "SHADER COMPILATION STUTTER!"
    /// and turns its graph columns red. Frame generation's swap chain (FSR 3's present queue) puts dxgi's Present back, so
    /// from then on the panel isn't drawn, and under FSR 3 (fakenext as its loader) the report still gets the game's frames;
    /// a panel drawn on the game's swap chain before stays. Under a chained d3d12.dll as without it. Off with a bad ab_tag;
    /// nothing without the lines.</summary>
    [Fact]
    public void The_proxy_draws_the_panel_where_it_is_safe()
    {
        if (OwnBuild() is not { } bin) return;
        (string Out, string Log, string Csv) Run(string name, string arg, string ini, string dll = ScsKiller.Dll12, (string, string)? extra = null) =>
            Selftest(bin, name, arg, ini, dll, extra);
        Assert.Contains("overlayunit ok", Run("unit", "overlayunit", "").Out);

        const string on = "overlay=1\r\nab_play_s=4\r\nab_tag=before\r\n";
        // long enough for the stutter mode's presents: it counts stutters only in the play time
        const string longer = "overlay=1\r\nab_play_s=20\r\nab_tag=before\r\n";
        foreach (var (name, arg, ini, dll, drawn, fmt) in new[]
                 {
                     ("draw", "overlay draw", on, ScsKiller.Dll12, "1920x1080 format 28 on queue \"\": drawn on", 28),
                     ("draw10", "overlay draw10", on, ScsKiller.Dll12, "1920x1080 format 24 on queue \"\": drawn on", 24),
                     ("hdr", "overlay hdr", on, ScsKiller.Dll12, "1920x1080 format 24 on queue \"\": drawn on", 24),
                     ("fp16", "overlay fp16", on, ScsKiller.Dll12, "1920x1080 format 10 on queue \"\": drawn on", 10),
                     ("stutter", "overlay stutter", longer, ScsKiller.Dll12, "1920x1080 format 28 on queue \"\": drawn on", 28),
                     ("draw11", "overlay draw11", "frames_only=1\r\n" + on, ScsKiller.Dll11, "1920x1080 format 28 (D3D11): drawn on", 28),
                     ("draw11_10", "overlay draw11_10", "frames_only=1\r\n" + on, ScsKiller.Dll11, "1920x1080 format 24 (D3D11): drawn on", 24),
                 })
        {
            var (o, log, csv) = Run(name, arg, ini, dll);
            foreach (var line in new[] { "first 0", "done 0", "resize 0x00000000", "small 0", "big 0" }) Assert.Contains(line, o);
            Assert.DoesNotContain("d3d12 debug:", o);   // the D3D12 debug layer, where installed, found nothing in the overlay's use of the queue
            var secs = ini == longer ? 20 : 4;
            Assert.Contains($"overlay: on (before, {secs} s of play)", log);
            Assert.Contains(drawn, log);
            Assert.Matches(@"overlay: play counted from \d+\.\d s, the first present", log);
            Assert.Contains($"overlay: {secs} s of play counted, done", log);
            Assert.DoesNotContain("off for this launch", log);
            Assert.Contains($"#swapchain,1920,1080,{fmt}\n", csv);
            Assert.Single(csv.Split('\n'), l => l == "#overlay,1");
            Assert.Equal(name == "stutter", log.Contains("overlay: a shader compilation stutter"));
            if (name == "hdr") Assert.Contains("setcs 0x00000000", o);
            if (name == "hdr") Assert.Contains("colour space 12 (HDR10)", log);
            if (name == "stutter") Assert.Matches(@"flash 0 red [1-9]\d*", o);
        }

        // a mod's d3d12.dll chained (next=): drawn as without it
        var (nout, nlog, _) = Run("next", "overlay draw", $"next={ScsKiller.ChainName}\r\n" + on, extra: ("fakenext.dll", ScsKiller.ChainName));
        foreach (var line in new[] { "first 0", "done 0", "small 0", "big 0" }) Assert.Contains(line, nout);
        Assert.Contains("overlay: under a layer (next=), drawn on the system's swap chain", nlog);
        Assert.Contains("1920x1080 format 28 on queue \"\": drawn on", nlog);
        Assert.DoesNotContain("off for this launch", nlog);

        // Streamline's swap chain proxy (`frameswrap sl`) and ReShade 6.5's (`fwd`): drawn on the native chain behind it, the
        // one the Present hook sees
        foreach (var (how, found) in new[] { ("sl", "wraps"), ("fwd", "forwards to") })
        {
            var (_, slog, _) = Run(how, $"frameswrap 5 {how}", on);
            var native = System.Text.RegularExpressions.Regex.Match(slog, $@"frames: swap chain \w+ \(.*\) {found} (\w+)").Groups[1].Value;
            Assert.NotEmpty(native);
            Assert.Matches($@"overlay: swap chain {native} \d+x\d+ format \d+ on queue "".*"": drawn on", slog);
            Assert.Contains("overlay: first frame drawn", slog);
        }

        // frame generation's swap chain alone: not drawn
        var (_, glog, gcsv) = Run("fg", "overlay fg", on);
        Assert.Contains("frames: frame generation's swap chain: dxgi's Present unhooked for this launch", glog);
        Assert.Contains("not drawn: frame generation's swap chain unhooked dxgi's Present", glog);
        Assert.DoesNotContain("overlay: first frame drawn", glog);
        Assert.DoesNotContain("#overlay,1", gcsv);
        Assert.Contains("#framegen,1", gcsv);
        Assert.Contains("#frames,stopped", gcsv);   // no hook on the game's frames: none follow

        // FSR 3 frame generation: no panel on its swap chain (the game's drew before it), and the before and after report still
        // has the game's frames
        var (fout, flog, fcsv) = Run("ffx", "framesfg ffx play", on, extra: ("fakenext.dll", "amd_fidelityfx_loader_dx12.dll"));
        Assert.Contains("dxgi's Present unhooked for this launch, the game's frames timed on frame generation's own swap chain", flog);
        Assert.DoesNotContain("on queue \"AMD FSR PresentQueue\": drawn on", flog);
        Assert.Contains("not drawn: frame generation's swap chain unhooked dxgi's Present", flog);
        Assert.Contains("game 1", fout);
        Assert.DoesNotContain("#frames,stopped", fcsv);
        var report = FrameLog.Read(Path.Combine(_root, "ffx", FrameLog.FileName), Path.Combine(_root, "ffx", "scskiller_creates.csv"), playCapMs: 4000);
        Assert.True(report is { Frames: >= 3 }, $"{fout}\n{flog}");

        // FSR 3 makes its swap chain after the panel drew on the game's: the game's stays drawn on, FSR 3's isn't
        (_, flog, _) = Run("fg-late", "framesfg \"AMD FSR PresentQueue\"", on);
        Assert.Contains("64x64 format 28 on queue \"\": drawn on", flog);
        Assert.Contains("not drawn: frame generation's swap chain unhooked dxgi's Present", flog);
        Assert.DoesNotContain("off for this launch", flog);

        // not `overlay`, which presents for a minute when the play time never comes
        (_, flog, _) = Run("tag", "frames 5", "overlay=1\r\nab_play_s=4\r\nab_tag=later\r\n");
        Assert.Contains("overlay: off (ab_tag isn't before or after)", flog);

        (_, flog, fcsv) = Run("none", "frames 5", "");
        Assert.DoesNotContain("overlay:", flog);
        Assert.Contains("#swapchain,", fcsv);
    }

    /// <summary>The countdown's hook time per present on this PC's GPU (`selftest overlaybench`): a minute of a game-like frame
    /// (a clear on the swap chain's queue) and a present, the overlay on and off. Drawn, the overlay adds one
    /// ExecuteCommandLists a frame and nothing that waits.</summary>
    [Fact]
    [Trait("Needs", "Gpu")]
    public void The_countdown_costs_one_submit_a_frame()
    {
        if (OwnBuild() is not { } bin) return;
        (double Avg, double Max) Bench(string name, string ini)
        {
            var (o, log, _) = Selftest(bin, name, "overlaybench 66", ini);
            output.WriteLine($"{name}: {o.Trim()}");
            var m = System.Text.RegularExpressions.Regex.Match(o, @"hook (\d+) ns each on average, (\d+) ns at most");
            Assert.True(m.Success, o);
            Assert.Equal(ini.Length > 0, log.Contains("overlay: first frame drawn"));
            return (double.Parse(m.Groups[1].Value), double.Parse(m.Groups[2].Value));
        }
        var on = Bench("bench-on", "overlay=1\r\nab_play_s=600\r\nab_tag=before\r\n");
        var off = Bench("bench-off", "");
        Assert.True(on.Avg - off.Avg < 50_000, $"{on.Avg - off.Avg} ns more a present with the countdown");
        Assert.True(on.Max < 3_000_000, $"{on.Max} ns at most");
    }

    /// <summary>`selftest <paramref name="arg"/>` in a new folder <paramref name="name"/> beside the proxy as <paramref name="dll"/>
    /// (and the build's <paramref name="extra"/> file under another name), with <paramref name="ini"/> in its scskiller.ini:
    /// its output, log and csv.</summary>
    (string Out, string Log, string Csv) Selftest(string bin, string name, string arg, string ini, string dll = ScsKiller.Dll12,
        (string From, string To)? extra = null)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(bin, "selftest.exe"), Path.Combine(dir, "selftest.exe"));
        File.Copy(Path.Combine(bin, "d3d12.dll"), Path.Combine(dir, dll));
        if (extra is var (from, to)) File.Copy(Path.Combine(bin, from), Path.Combine(dir, to));
        File.WriteAllText(Path.Combine(dir, "scskiller.ini"), "[scskiller]\r\nmode=record\r\n" + ini);
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(dir, "selftest.exe"), arg) { RedirectStandardOutput = true })!;
        var o = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, o);
        string Read(string f) => File.Exists(Path.Combine(dir, f)) ? File.ReadAllText(Path.Combine(dir, f)).ReplaceLineEndings("\n") : "";
        return (o, Read("scskiller.log"), Read("scskiller_creates.csv"));
    }

    /// <summary>This checkout's proxy\build\Release (the repo root is the folder with SCSKiller.slnx above the test binaries),
    /// else null: not built here.</summary>
    static string? OwnBuild()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "SCSKiller.slnx")))
                return Path.Combine(d.FullName, "proxy", "build", "Release") is var p && File.Exists(Path.Combine(p, "selftest.exe")) ? p : null;
        return null;
    }
}
