using System.Diagnostics;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;
using SCSKiller.Core;
using SCSKiller.Core.Planning;
using SCSKiller.Core.Vendors;
using SCSKiller.Core.Warming;

namespace SCSKiller.Tests.Platform;

// Each test runs the fake warm through cmd and PowerShell (seconds to start on a loaded PC) and some assert on reaction
// times (the stuck-stop limit, pause): alone, not next to the thread-pool-saturating index tests; waits are generous.
[Collection(TimingCollection.Name)]
public class WarmerTests : IDisposable
{
    // Stand-in for scskiller_warm.exe: prints canned protocol lines; the scenario comes from <workdir>\mode.txt.
    const string FakeScript = """
        $work = $args[0]; $exe = $args[1]; $ev = $null
        for ($i = 2; $i -lt $args.Count; $i++) { if ($args[$i] -eq '--stop-event') { $ev = $args[$i + 1] } }
        Set-Content (Join-Path $work 'args.txt') ($args -join "`n")
        [Console]::OutputEncoding = [Text.Encoding]::UTF8   # as scskiller_warm writes it
        function Say($s) { [Console]::Out.WriteLine($s); [Console]::Out.Flush() }
        $mode = (Get-Content (Join-Path $work 'mode.txt')).Trim()
        Say 'scskiller_warm: a banner line that is not JSON'
        Say ('{"event":"stage","stage":"' + ((Join-Path $work "stage-$PID-1") -replace '\\', '\\') + '"}')
        if ($mode -eq 'error') { Say '{"event":"error","message":"no D3D12 device"}'; exit 3 }   # before start: its log is in the stage
        if ($mode -eq 'aftermath') {   # the child dies in the game's Aftermath before its items, as scskiller_warm reports it
            Set-Content (Join-Path $work "run-$PID.txt") ($args -join ' ')
            if ([array]::IndexOf($args, '--aftermath') -ge 0) { Say '{"event":"error","message":"the warm process died (exit code 0xC0000005)"}'; exit 1 }
            Say ('{"event":"start","total":1000,"adapter":"Fake GPU","exe":"' + $exe + '"}')
            Say '{"event":"done","done":1000,"total":1000,"failed":0,"seconds":0.1,"stopped":false}'; exit 0
        }
        if ($mode -eq 'retryfail') {   # a retry, then the second process fails before its start
            Set-Content (Join-Path $work "run-$PID.txt") ($args -join ' ')
            if ([array]::IndexOf($args, '--rt-threads') -lt 0) { Say '{"event":"retry","from":600,"rtThreads":8,"failedItem":-1,"done":600,"total":1000,"failed":1,"seconds":1.0}'; exit 3 }
            Say '{"event":"error","message":"no D3D12 device"}'; exit 1
        }
        Say ('{"event":"start","total":1000,"adapter":"Fake GPU","exe":"' + $exe + '"}')
        if ($mode -eq 'complete') {
            Say '{"event":"progress","done":500,"total":1000,"failed":1,"rate":480.5}'
            Say '{"event":"done","done":1000,"total":1000,"failed":2,"seconds":2.1,"stopped":false}'
            exit 0
        }
        if ($mode -eq 'passcrash') {   # pass 1 blames cc (a removal, then done); the later passes get it with the given keys
            Set-Content (Join-Path $work "run-$PID.txt") ($args -join ' ')
            $p = $args[[array]::IndexOf($args, '--pass') + 1]; $iso = [array]::IndexOf($args, '--isolate')
            if ($p -eq '1' -and $iso -lt 0) { Say '{"event":"retry","from":1,"rtThreads":2,"failedItem":-1,"done":1,"total":7,"failed":0,"seconds":0.1,"reason":"removed","crashed":[],"isolate":[1]}'; exit 3 }
            if ($p -eq '1') { Say '{"event":"done","done":7,"total":7,"failed":0,"seconds":0.1,"stopped":false,"crashed":["00000000000000000000000000000000000000cc"]}'; exit 0 }
            Say '{"event":"done","done":7,"total":7,"failed":0,"seconds":0.1,"stopped":false}'; exit 0
        }        if ($mode -eq 'passes') {   # 7 items in the whole-run numbering of each pass; one failure per pass
            Set-Content (Join-Path $work "run-$PID.txt") ($args -join ' ')
            Say '{"event":"progress","done":3,"total":7,"failed":0,"rate":5}'
            Say '{"event":"done","done":7,"total":7,"failed":1,"seconds":0.1,"stopped":false}'; exit 0
        }
        if ($mode -eq 'nodone') { Say '{"event":"progress","done":10,"total":1000,"failed":0,"rate":5}'; exit 0 }
        if ($mode -eq 'stuck') { Set-Content (Join-Path $work 'pid.tmp') $PID; Move-Item (Join-Path $work 'pid.tmp') (Join-Path $work 'pid.txt'); while ($true) { Say '{"event":"progress","done":7,"total":1000,"failed":0,"rate":5}'; Start-Sleep -Milliseconds 100 } }   # deaf to the stop event
        if ($mode -eq 'retrystuck') {   # a retry, then the second process is deaf to the stop event
            if ([array]::IndexOf($args, '--rt-threads') -lt 0) { Say '{"event":"retry","from":600,"rtThreads":8,"failedItem":-1,"done":600,"total":1000,"failed":1,"seconds":1.0}'; exit 3 }
            Set-Content (Join-Path $work 'pid.tmp') $PID; Move-Item (Join-Path $work 'pid.tmp') (Join-Path $work 'pid.txt')   # appears whole
            while ($true) { Say '{"event":"progress","done":650,"total":1000,"failed":0,"rate":5}'; Start-Sleep -Milliseconds 100 }
        }
        if ($mode -eq 'retry') {   # 32 threads: a fault -> retry from 600 on 8; there a fault alone at 650 -> from 700 on 8 again, 650 skipped; then done
            Set-Content (Join-Path $work "run-$PID.txt") ($args -join ' ')
            $rt = [array]::IndexOf($args, '--rt-threads'); $rt = if ($rt -ge 0) { $args[$rt + 1] } else { '' }
            $sk = [array]::IndexOf($args, '--skip')
            if ($rt -eq '') { Say '{"event":"progress","done":550,"total":1000,"failed":1,"rate":400}'; Say '{"event":"retry","from":600,"rtThreads":8,"failedItem":-1,"done":600,"total":1000,"failed":1,"seconds":1.0}'; exit 3 }
            if ($rt -eq '8' -and $sk -lt 0) { Say '{"event":"retry","from":700,"rtThreads":8,"failedItem":650,"done":700,"total":1000,"failed":2,"driverSkipped":5,"seconds":1.0}'; exit 3 }
            Say '{"event":"progress","done":900,"total":1000,"failed":0,"driverSkipped":3,"rate":300}'
            Start-Sleep -Milliseconds 400
            Say '{"event":"done","done":1000,"total":1000,"failed":0,"driverSkipped":4,"seconds":1.0,"stopped":false}'; exit 0
        }
        if ($mode -eq 'removed') {   # removed with 600, 610, 611 in flight -> 610 removes it alone -> done without it
            Set-Content (Join-Path $work "run-$PID.txt") ($args -join ' ')
            $iso = [array]::IndexOf($args, '--isolate'); $iso = if ($iso -ge 0) { $args[$iso + 1] } else { '' }
            if ($iso -eq '') { Say '{"event":"retry","from":600,"rtThreads":4,"failedItem":-1,"done":600,"total":1000,"failed":1,"seconds":1.0,"reason":"removed","crashed":["00000000000000000000000000000000000000bb"],"isolate":[600,610,611]}'; exit 3 }
            if ($iso -ne '611') { Say '{"event":"retry","from":601,"rtThreads":4,"failedItem":-1,"done":601,"total":1000,"failed":0,"seconds":1.0,"reason":"removed","crashed":["00000000000000000000000000000000000000aa"],"isolate":[611]}'; exit 3 }
            Say '{"event":"done","done":1000,"total":1000,"failed":0,"seconds":1.0,"stopped":false,"crashed":["00000000000000000000000000000000000000AA","00000000000000000000000000000000000000bb"]}'; exit 0
        }
        if ($mode -eq 'storm') {   # each process fails one more state object (a known item) at the threads it is given; 11 in all
            $sk = [array]::IndexOf($args, '--skip'); $skipped = if ($sk -ge 0) { @($args[$sk + 1].Split(',')).Count } else { 0 }
            $rt = [array]::IndexOf($args, '--rt-threads'); $rt = if ($rt -ge 0) { $args[$rt + 1] } else { '32' }
            Set-Content (Join-Path $work "run-$skipped.txt") ($args -join ' ')
            if ($skipped -lt 11) { Say ('{"event":"retry","from":' + (600 + $skipped) + ',"rtThreads":' + $rt + ',"failedItem":' + (650 + $skipped) + ',"done":' + (600 + $skipped) + ',"total":1000,"failed":0,"seconds":1.0}'); exit 3 }
            Say '{"event":"done","done":1000,"total":1000,"failed":0,"seconds":1.0,"stopped":false}'; exit 0
        }
        if ($mode -eq 'hang') {   # creates keep hanging: a retry from 600 skipping the hung 610 and 611, then done
            Set-Content (Join-Path $work "run-$PID.txt") ($args -join ' ')
            if ([array]::IndexOf($args, '--skip') -lt 0) { Say '{"event":"retry","from":600,"rtThreads":0,"failedItem":-1,"done":600,"total":1000,"failed":1,"driverSkipped":18,"seconds":1.0,"reason":"hang","crashed":[],"isolate":[],"skip":[610,611]}'; exit 3 }
            Say '{"event":"done","done":1000,"total":1000,"failed":0,"driverSkipped":2,"seconds":1.0,"stopped":false,"crashed":[]}'; exit 0
        }
        if ($mode -eq 'hangloop') { Say '{"event":"retry","from":10,"rtThreads":0,"failedItem":-1,"done":10,"total":1000,"failed":0,"driverSkipped":20,"seconds":1.0,"reason":"hang","crashed":[],"isolate":[],"skip":[]}'; exit 3 }
        if ($mode -in 'removedloop', 'faultloop') { Say ('{"event":"retry","from":10,"rtThreads":0,"failedItem":-1,"done":10,"total":1000,"failed":1,"seconds":1.0,"reason":"' + ($mode -replace 'loop') + '","crashed":[],"isolate":[10,11]}'); exit 3 }
        $h = [System.Threading.EventWaitHandle]::OpenExisting($ev); $done = 0
        while (-not $h.WaitOne(0)) { $done++; Say ('{"event":"progress","done":' + $done + ',"total":1000,"failed":0,"rate":20}'); Start-Sleep -Milliseconds 50 }
        Say ('{"event":"done","done":' + $done + ',"total":1000,"failed":0,"seconds":1.0,"stopped":true}')
        if ($mode -eq 'flush') { Start-Sleep -Seconds 3 }   # the driver writing its cache as the process exits
        exit 0
        """;

    readonly string _dir = Path.Combine(Path.GetTempPath(), "scskiller-warm-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string _exe;
    readonly DateTime _started = DateTime.UtcNow;
    static readonly Game Game = new("test:1", "Fake", Store.Other, @"C:\nowhere", @"C:\nowhere\Binaries\Win64\Fake-Win64-Shipping.exe");
    static readonly UnsupportedVendor Vendor = new(new GpuInfo(GpuVendor.Unknown, "Fake GPU", "1.0", 0x1234ABCD, 0));

    public WarmerTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "fake_warm.ps1"), FakeScript);
        _exe = Path.Combine(_dir, "fake_warm.cmd");
        File.WriteAllText(_exe, "@powershell -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0fake_warm.ps1\" %*\r\n@exit /b %ERRORLEVEL%\r\n");
    }

    public void Dispose()
    {
        TestD3DSCache.Clean(_dir, _started);   // the staged exes' D3DSCache folders
        // a killed fake warm's console host can hold its working directory a moment after the warm itself has exited
        for (var i = 0; ; i++)
            try { Directory.Delete(_dir, true); return; }
            catch (IOException) when (i < 50) { Thread.Sleep(100); }
    }

    string Work(string mode)
    {
        var w = Path.Combine(_dir, mode);
        Directory.CreateDirectory(w);
        File.WriteAllText(Path.Combine(w, "mode.txt"), mode);
        return w;
    }

    sealed class Counter : IProgress<WarmProgress>
    {
        public int Count;
        public WarmProgress? Last;
        public void Report(WarmProgress p) { Last = p; Interlocked.Increment(ref Count); }
    }

    [Fact]
    public void Parser_reads_every_event_and_ignores_noise()
    {
        var start = WarmEvent.Parse("""{"event":"start","total":117197,"adapter":"NVIDIA GeForce","exe":"ff7rebirth_.exe"}""")!;
        Assert.Equal(("start", 117197L), (start.Event, start.Total));
        var stage = WarmEvent.Parse("""{"event":"stage","stage":"C:\\w\\stage-12-1"}""")!;
        Assert.Equal(("stage", @"C:\w\stage-12-1"), (stage.Event, stage.Stage));
        var p = WarmEvent.Parse("""{"event":"progress","done":64210,"total":117197,"failed":3,"rate":480.2}""")!;
        Assert.Equal((64210L, 117197L, 3L, 480.2), (p.Done, p.Total, p.Failed, p.Rate));
        var done = WarmEvent.Parse("""{"event":"done","done":117197,"total":117197,"failed":5,"seconds":288.1,"stopped":false}""")!;
        Assert.Equal((117197L, 5L, 288.1, false), (done.Done, done.Failed, done.Seconds, done.Stopped));
        Assert.True(WarmEvent.Parse("""{"event":"done","done":10,"total":20,"failed":0,"seconds":1,"stopped":true}""")!.Stopped);
        const string same = """{"event":"done","done":10,"total":20,"failed":0,"seconds":1,"stopped":true,"crashed":[]}""";
        Assert.Equal(WarmEvent.Parse(same), WarmEvent.Parse(same));   // WarmOutput.Pump folds equal lines
        Assert.Equal("boom", WarmEvent.Parse("""{"event":"error","message":"boom"}""")!.Message);
        Assert.Null(WarmEvent.Parse("scskiller_warm: warming the cache of 'x.exe'"));
        Assert.Null(WarmEvent.Parse("{not json"));
        Assert.Null(WarmEvent.Parse("""{"done":1}"""));
    }

    [Fact]
    public async Task Completed_run_reports_progress_and_passes_the_protocol_arguments()
    {
        var work = Work("complete");
        var c = new Counter();
        var r = await new Warmer(Vendor, _exe).Start(Game, work, new WarmOptions(7, WarmPriority.Idle, 42, 6144), c).Completion;
        Assert.Equal(WarmOutcome.Completed, r.Outcome);
        Assert.Equal((1000L, 1000L, 2L), (r.Done, r.Total, r.Failed));
        Assert.InRange(c.Count, 1, 2);   // lines closer together than the report interval fold into the latest
        Assert.Equal(1000, c.Last!.Done);
        Assert.Null(r.Error);
        Assert.Matches(@"\\stage-\d+-1\\scskiller\.log$", r.LogPath);   // the stage event's folder
        Assert.StartsWith(work, r.LogPath);
        var a = File.ReadAllLines(Path.Combine(work, "args.txt"));
        Assert.Equal(work, a[0]);
        Assert.Equal("Fake-Win64-Shipping.exe", a[1]);
        Assert.Equal(["--threads", "7", "--priority", "idle", "--start", "42", "--stop-event"], a[2..9]);
        Assert.Equal(["--adapter-luid", "000000001234ABCD", "--memory-mb", "6144"], a[10..14]);
        Assert.Equal(["--stage-path", @"nowhere\Binaries\Win64\Fake-Win64-Shipping.exe"], a[14..]);
    }

    [Fact]
    public async Task A_run_launches_on_the_adapter_it_was_started_for()
    {
        var work = Work("complete");
        await new Warmer(Vendor, _exe).Start(Game, work, new WarmOptions(1, WarmPriority.Idle), null, Vendor.Gpu with { AdapterLuid = 0xABC }).Completion;
        var a = File.ReadAllLines(Path.Combine(work, "args.txt"));
        Assert.Equal("0000000000000ABC", a[Array.IndexOf(a, "--adapter-luid") + 1]);
    }

    [Theory]
    [InlineData(@"C:\Games\Hogwarts Legacy", @"C:\Games\Hogwarts Legacy\Phoenix\Binaries\Win64\HogwartsLegacy.exe", @"Hogwarts Legacy\Phoenix\Binaries\Win64\HogwartsLegacy.exe")]
    [InlineData(@"C:\Games\Hogwarts Legacy\", @"C:\Games\Hogwarts Legacy\Phoenix\Binaries\Win64\HogwartsLegacy.exe", @"Hogwarts Legacy\Phoenix\Binaries\Win64\HogwartsLegacy.exe")]
    [InlineData(@"C:\Games\Hogwarts Legacy", @"C:\Games\Hogwarts Legacy\HOGWARTSLEGACY.EXE", @"Hogwarts Legacy\HOGWARTSLEGACY.EXE")]
    [InlineData(@"D:\Steam\common\Tiny Tina's Wonderlands", @"D:\Steam\common\Tiny Tina's Wonderlands\OakGame\Binaries\Win64\Wonderlands.exe", @"Tiny Tina's Wonderlands\OakGame\Binaries\Win64\Wonderlands.exe")]
    [InlineData(@"D:\Games\Ōkami HD v1.2 (GOG)", @"D:\Games\Ōkami HD v1.2 (GOG)\..data\bin.x64\okami.exe", @"Ōkami HD v1.2 (GOG)\..data\bin.x64\okami.exe")]
    [InlineData(@"d:\games\hogwarts legacy", @"D:\Games\Hogwarts Legacy\Phoenix\HogwartsLegacy.exe", @"hogwarts legacy\Phoenix\HogwartsLegacy.exe")]
    public void The_stage_path_is_the_install_folder_and_the_exe_path_inside_it(string install, string exe, string expected)
    {
        Assert.Equal(expected, Warmer.StagePath(Game with { InstallDir = install, ExePath = exe }, @"C:\Users\x\AppData\Local\SCSKiller\games\steam_990080\work", out var why));
        Assert.Null(why);
    }

    [Theory]
    [InlineData(@"C:\Games\Hogwarts Legacy", @"C:\Games\Hogwarts Legacy2\HogwartsLegacy.exe", "outside")]
    [InlineData(@"C:\Games\Hogwarts Legacy", @"D:\Hogwarts Legacy\HogwartsLegacy.exe", "outside")]
    [InlineData(@"C:\Games\Hogwarts Legacy", @"C:\Games\HogwartsLegacy.exe", "outside")]
    [InlineData(@"D:\", @"D:\Binaries\Game.exe", "drive root")]
    public void An_exe_outside_its_install_gets_no_stage_path(string install, string exe, string reason)
    {
        Assert.Null(Warmer.StagePath(Game with { InstallDir = install, ExePath = exe }, @"C:\work", out var why));
        Assert.Contains(reason, why);
    }

    [Fact]
    public void A_stage_path_that_would_reach_MAX_PATH_is_left_out()
    {
        var install = @"C:\Games\" + new string('i', 60);
        var g = Game with { InstallDir = install, ExePath = Path.Combine(install, new string('d', 60), "Game.exe") };
        var work = @"C:\" + new string('w', 86);
        Assert.NotNull(Warmer.StagePath(g, work + "www", out _));   // 92 + @"\stage-4294967295-99\" + 60 + 1 + 60 + 1 + 24 = 259
        Assert.Null(Warmer.StagePath(g, work + "wwww", out var why));
        Assert.Contains("longer than 259", why);
    }

    [Fact]
    public async Task An_exe_outside_its_install_warms_without_a_stage_path_and_logs_why()
    {
        var work = Work("complete");
        var log = new List<string>();
        var warmer = new Warmer(Vendor, _exe) { Log = new Progress(log.Add) };
        await warmer.Start(Game with { ExePath = @"C:\elsewhere\Fake-Win64-Shipping.exe" }, work, new WarmOptions(1, WarmPriority.BelowNormal), null).Completion;
        Assert.DoesNotContain("--stage-path", File.ReadAllLines(Path.Combine(work, "args.txt")));
        Assert.Equal([@"Fake: the compile runs without the install's folder layout: C:\elsewhere\Fake-Win64-Shipping.exe is outside C:\nowhere"], log);
    }

    sealed class Progress(Action<string> a) : IProgress<string> { public void Report(string s) { lock (a) a(s); } }

    /// <summary>The real scskiller_warm on WARP (no GPU cache): with --stage-path the child runs from
    /// stage\&lt;install folder&gt;\&lt;dir&gt;\&lt;exe&gt; (the proxy logs its process's path), its log ends up in stage\ and the
    /// staged folders are gone; a path that would reach MAX_PATH stages the exe alone. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task The_real_warm_runs_its_child_from_the_stage_path_and_removes_it()
    {
        var bin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");
        if (!File.Exists(Path.Combine(bin, "scskiller_warm.exe"))) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(bin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!.StandardOutput.ReadToEnd().Trim();
        var vendor = new UnsupportedVendor(new GpuInfo(GpuVendor.Unknown, "WARP", "1.0", Convert.ToInt64(luid, 16), 0));
        var install = Path.Combine(_dir, "games", "Fake Game's (x) é.v2");
        var exe = $"scsk-stage-{Guid.NewGuid():N}"[..20] + ".exe";
        var game = Game with { InstallDir = install, ExePath = Path.Combine(install, "Bin", "Win64", exe) };
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        var r = await new Warmer(vendor, Path.Combine(bin, "scskiller_warm.exe")).Start(game, work, new WarmOptions(1, WarmPriority.BelowNormal), null).Completion.WaitAsync(Patience);
        Assert.Equal(WarmOutcome.Completed, r.Outcome);
        var stage = Path.GetDirectoryName(r.LogPath)!;
        Assert.StartsWith(Path.Combine(work, "stage-"), stage);   // the run's own new staging folder
        var loaded = File.ReadLines(r.LogPath, System.Text.Encoding.Latin1).First(l => l.Contains("loaded into "));
        Assert.EndsWith("loaded into " + Path.Combine("Win64", exe), loaded);   // never the full path
        Assert.Empty(Directory.GetDirectories(stage));
        Assert.Equal(["scskiller.log", "scskiller_creates.csv", Recordings.FailedFile], Directory.GetFiles(stage).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Empty(File.ReadAllBytes(Path.Combine(stage, Recordings.FailedFile)));   // every item created
        Assert.False(Directory.Exists(install));

        var p = Process.Start(new ProcessStartInfo(Path.Combine(bin, "scskiller_warm.exe"),
            [work, exe, "--adapter-luid", luid, "--stage-path", Path.Combine(new string('x', 120), new string('y', 120), exe)]) { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEndAsync();
        Assert.Contains("\"done\"", await p.StandardOutput.ReadToEndAsync());
        await p.WaitForExitAsync();
        Assert.Contains("--stage-path too long", await err);
        var flat = Assert.Single(Directory.GetDirectories(work, "stage-*"), d => d != stage);   // a second run stages in a new folder
        Assert.EndsWith("loaded into " + Path.Combine(Path.GetFileName(flat), exe), File.ReadLines(Path.Combine(flat, "scskiller.log")).First(l => l.Contains("loaded into ")));
        Assert.Equal(["scskiller.log", "scskiller_creates.csv", Recordings.FailedFile], Directory.GetFiles(flat).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Empty(Directory.GetDirectories(flat));
        Assert.Equal(["scskiller.log", "scskiller_creates.csv", Recordings.FailedFile], Directory.GetFiles(stage).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(@".\D3D12\", @"C:\G\Bin\D3D12")]
    [InlineData(@".\D3D12_0\", @"C:\G\Bin\D3D12_0")]
    [InlineData(@"D3D12-REDIST", @"C:\G\Bin\D3D12-REDIST")]
    [InlineData(@".\", @"C:\G\Bin")]
    [InlineData(@"..data\D3D12\", @"C:\G\Bin\..data\D3D12")]
    [InlineData(@"..\D3D12\", null)]
    [InlineData(@".\D3D12\..\..\Windows\System32\", null)]
    [InlineData(@"C:\Windows\System32\", null)]
    [InlineData(@"\Windows\System32", null)]
    [InlineData(@"C:Windows", null)]
    [InlineData(@"\server\share\D3D12", null)]
    [InlineData("", null)]
    public void The_Agility_folder_must_be_inside_the_exe_s_folder(string sdkPath, string? expected) =>
        Assert.Equal(expected, Warmer.AgilityFolder(@"C:\G\Bin", sdkPath));

    [Fact]
    public void An_exe_whose_D3D12SDKPath_can_t_be_read_gets_the_D3D12_folder_next_to_it()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "agility-fallback")).FullName;
        var exe = Path.Combine(bin, "game.exe");
        string Core(string folder)
        {
            var d = Directory.CreateDirectory(Path.Combine(bin, folder)).FullName;
            File.WriteAllText(Path.Combine(d, "D3D12Core.dll"), "");
            return d;
        }
        File.WriteAllText(exe, "packed");
        Assert.Null(Warmer.AgilityDir(exe));
        var d3d12 = Core("D3D12");
        Assert.Equal(d3d12, Warmer.AgilityDir(exe));   // not a PE
        File.WriteAllBytes(exe, Planning.MiddlewarePackTests.Pe(null));
        Assert.Equal(d3d12, Warmer.AgilityDir(exe));   // no exports

        // export directory at RVA 0x1000 (file offset 0x200): one name, D3D12SDKPath, whose variable points at .\D3D12_0\
        var pe = Planning.MiddlewarePackTests.Pe(null);
        void Put(int offset, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pe.AsSpan(offset), v);
        Put(0x58 + 112, 0x1000);
        Put(0x200 + 20, 1); Put(0x200 + 24, 1); Put(0x200 + 28, 0x1040); Put(0x200 + 32, 0x1044); Put(0x200 + 36, 0x1048);
        Put(0x240, 0x1050); Put(0x244, 0x1060); Put(0x250, 0x1080);   // ImageBase 0: the variable's VA is an RVA
        "D3D12SDKPath"u8.CopyTo(pe.AsSpan(0x260));
        @".\D3D12_0\"u8.CopyTo(pe.AsSpan(0x280));
        File.WriteAllBytes(exe, pe);
        Assert.Null(Warmer.AgilityDir(exe));   // the export's folder has no runtime: the game doesn't load D3D12's either
        Assert.Equal(Core("D3D12_0"), Warmer.AgilityDir(exe));
    }

    [Fact]
    public void An_exe_whose_D3D12SDKPath_can_t_be_read_gets_D3D12_x64_when_D3D12_has_no_runtime()
    {
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "agility-x64")).FullName;
        var exe = Path.Combine(bin, "game.exe");
        File.WriteAllText(exe, "packed");
        var x64 = Directory.CreateDirectory(Path.Combine(bin, "D3D12", "x64")).FullName;
        File.WriteAllText(Path.Combine(x64, "D3D12Core.dll"), "");
        Assert.Equal(x64, Warmer.AgilityDir(exe));
        File.WriteAllText(Path.Combine(bin, "D3D12", "D3D12Core.dll"), "");
        Assert.Equal(Path.Combine(bin, "D3D12"), Warmer.AgilityDir(exe));
    }

    /// <summary>The real scskiller_warm on WARP for a game shipping the Agility SDK (its exe a copy of scskiller_warm, which
    /// exports D3D12SDKPath .\D3D12\): the folder is passed (--d3d12), its runtime DLLs staged next to the child's exe and
    /// removed after, read-only ones included. A D3D12Core.dll that doesn't load or is over the size cap leaves the system's
    /// runtime; one that makes no device (fakenext.dll, SDK 100000) runs the child again on the system's. SCSKILLER_AGILITY_DIR
    /// (a game's Agility SDK folder, e.g. The Witcher 3's bin\x64_dx12\D3D12_0) also checks the child runs on that runtime
    /// when it is newer than the system's, else on the system's. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task The_real_warm_runs_on_the_game_s_Agility_runtime()
    {
        var bin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");
        if (!File.Exists(Path.Combine(bin, "scskiller_warm.exe"))) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(bin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!.StandardOutput.ReadToEnd().Trim();
        var vendor = new UnsupportedVendor(new GpuInfo(GpuVendor.Unknown, "WARP", "1.0", Convert.ToInt64(luid, 16), 0));
        var install = Path.Combine(_dir, "games", "Agility");
        var exe = Path.Combine(install, "Bin", $"scsk-agility-{Guid.NewGuid():N}"[..20] + ".exe");
        var core = Path.Combine(install, "Bin", "D3D12");
        var dll = Path.Combine(core, "D3D12Core.dll");
        Directory.CreateDirectory(core);
        File.Copy(Path.Combine(bin, "scskiller_warm.exe"), exe);
        var game = Game with { InstallDir = install, ExePath = exe };
        Assert.Null(Warmer.AgilityDir(exe));   // no D3D12Core.dll there
        static uint Sdk(string dll)
        {
            using var pe = Core.Carved.PeFile.Open(dll);
            return Core.Carved.PeFile.ExportData(pe, "D3D12SDKVersion")!.Value.ReadUInt32();
        }
        async Task<(string Runtime, string Log)> Run()
        {
            var work = Directory.CreateDirectory(Path.Combine(_dir, "work-" + Guid.NewGuid().ToString("N")[..6])).FullName;
            var log = new List<string>();
            var r = await new Warmer(vendor, Path.Combine(bin, "scskiller_warm.exe")) { Log = new Progress(log.Add) }
                .Start(game, work, new WarmOptions(1, WarmPriority.BelowNormal), null).Completion.WaitAsync(Patience);
            Assert.Equal(WarmOutcome.Completed, r.Outcome);
            var stage = Path.GetDirectoryName(r.LogPath)!;
            Assert.Empty(Directory.GetDirectories(stage));
            Assert.DoesNotContain(log, l => l.Contains("removing the staged"));
            var lines = File.ReadAllLines(r.LogPath, System.Text.Encoding.Latin1);
            return (lines.Single(l => l.Contains("warm: D3D12 runtime ")), string.Join('\n', log.Concat(lines.Where(l => l.Contains("isn't the one loaded")))));
        }
        void Put(string from)
        {
            if (File.Exists(dll)) File.SetAttributes(dll, FileAttributes.Normal);
            File.Copy(from, dll, true);
            File.SetAttributes(dll, FileAttributes.ReadOnly);
        }
        var system = Path.Combine(Environment.SystemDirectory, "D3D12Core.dll");
        var onSystem = $@"<system32>\D3D12Core.dll (SDK {Sdk(system)})";
        try
        {
            File.WriteAllText(Path.Combine(_dir, "notadll"), "not a dll");
            Put(Path.Combine(_dir, "notadll"));
            Assert.Equal(core, Warmer.AgilityDir(exe));
            var (runtime, log) = await Run();
            Assert.Contains(onSystem, runtime, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"staging the game's D3D12 runtime from {core} failed", log);

            using (var big = File.Create(Path.Combine(_dir, "big"))) big.SetLength((64 << 20) + 1);
            Put(Path.Combine(_dir, "big"));
            (runtime, log) = await Run();
            Assert.Contains(onSystem, runtime, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"staging the game's D3D12 runtime from {core} failed", log);

            Put(Path.Combine(bin, "fakenext.dll"));
            (runtime, log) = await Run();
            Assert.Contains(onSystem, runtime, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("D3D12 device creation failed on the game's D3D12 runtime (SDK 100000", log);

            if (Environment.GetEnvironmentVariable("SCSKILLER_AGILITY_DIR") is not { Length: > 0 } agility) return;
            File.SetAttributes(dll, FileAttributes.Normal);
            foreach (var f in Directory.GetFiles(agility, "*.dll")) File.Copy(f, Path.Combine(core, Path.GetFileName(f)), true);
            (runtime, log) = await Run();
            var (sdk, systemSdk) = (Sdk(dll), Sdk(system));
            Assert.Contains(sdk > systemSdk ? $@" D3D12\D3D12Core.dll (SDK {sdk})" : onSystem, runtime, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(sdk <= systemSdk, log.Contains($"the game's D3D12 runtime (SDK {sdk}) staged in D3D12\\ isn't the one loaded"));
        }
        finally { File.SetAttributes(dll, FileAttributes.Normal); }
    }

    /// <summary>The real scskiller_warm on WARP with a layer (--layer): its dlls, ini and add-ons are staged next to the
    /// child's exe and go with the other staged inputs; anything else in the folder (an exe, game data, a log) is left out,
    /// and the folder stays as it was. A layer file named like a staged input (ReShade as d3d12.dll, the proxy's name), or
    /// an ini pointing ReShade at another folder, stops the run before it starts. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task The_real_warm_stages_only_the_layer_s_files_and_removes_them()
    {
        var bin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");
        if (!File.Exists(Path.Combine(bin, "scskiller_warm.exe"))) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(bin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!.StandardOutput.ReadToEnd().Trim();
        var exe = $"scsk-layer-{Guid.NewGuid():N}"[..20] + ".exe";
        var layer = Path.Combine(_dir, "layer");
        Directory.CreateDirectory(layer);
        string[] files = ["Game.exe", "ReShade.ini", "ReShade.log", "data.pak", "fake.addon64", "helper.dll"];
        foreach (var f in files) File.WriteAllText(Path.Combine(layer, f), f == "ReShade.ini" ? "[GENERAL]\r\nNoReloadOnInit=1\r\n" : f);
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        async Task<(string Out, string Err)> Run()
        {
            var p = Process.Start(new ProcessStartInfo(Path.Combine(bin, "scskiller_warm.exe"), [work, exe, "--adapter-luid", luid, "--layer", layer])
                { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var (o, e) = (p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync());
            await p.WaitForExitAsync().WaitAsync(Patience);
            return (await o, await e);
        }
        var (o1, _) = await Run();
        Assert.Contains("\"done\"", o1);
        var stage = Assert.Single(Directory.GetDirectories(work, "stage-*"));
        Assert.Equal(["scskiller.log", "scskiller_creates.csv", Recordings.FailedFile], Directory.GetFiles(stage).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(files.Order(StringComparer.Ordinal), Directory.GetFiles(layer).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        File.WriteAllText(Path.Combine(layer, "ReShade.ini"), "[ADDON]\r\nAddonPath=C:\\Games\\X\r\n");
        Assert.Contains("sets [ADDON] AddonPath", (await Run()).Out);
        File.WriteAllText(Path.Combine(layer, "ReShade.ini"), "[GENERAL]\r\n");
        File.WriteAllText(Path.Combine(layer, "d3d12.dll"), "ReShade");
        Assert.Contains("staging the layer's d3d12.dll failed", (await Run()).Out);
    }

    /// <summary>The real scskiller_warm passes its options on so that the child's argv reads them back as given: a folder
    /// ending in a backslash (here --layer's) doesn't escape its closing quote and swallow the options after it (the
    /// adapter: WARP). Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task The_real_warm_passes_a_folder_ending_in_a_backslash_on_to_its_child()
    {
        var bin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");
        if (!File.Exists(Path.Combine(bin, "scskiller_warm.exe"))) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(bin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true })!.StandardOutput.ReadToEnd().Trim();
        var layer = Directory.CreateDirectory(Path.Combine(_dir, "layer")).FullName;
        var work = Directory.CreateDirectory(Path.Combine(_dir, "work")).FullName;
        var exe = $"scsk-quote-{Guid.NewGuid():N}"[..20] + ".exe";
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(bin, "scskiller_warm.exe"), [work, exe, "--layer", layer + "\\", "--adapter-luid", luid])
            { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var (o, _) = (p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync());
        await p.WaitForExitAsync().WaitAsync(Patience);
        Assert.Contains("\"adapter\":\"Microsoft Basic Render Driver\"", await o);
        Assert.Equal(0, p.ExitCode);
    }

    /// <summary>The real scskiller_warm whose child can't start (an app id no package has, then a command line too long for
    /// CreateProcess) prints its error and exits 1: the output relays it started for the packaged child are joined, not left
    /// to std::terminate, a fail-fast crash. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task The_real_warm_that_cant_start_its_child_ends_with_its_error()
    {
        var bin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");
        if (!File.Exists(Path.Combine(bin, "scskiller_warm.exe"))) return;
        var work = Directory.CreateDirectory(Path.Combine(_dir, "work")).FullName;
        var exe = $"scsk-nochild-{Guid.NewGuid():N}"[..20] + ".exe";
        // each pair is 9 characters here and 13 quoted on the child's command line: this one fits, the child's doesn't
        string[] args = [work, exe, "--package", "SCSKiller.NoSuchPackage_0000000000000!App", .. Enumerable.Repeat<string[]>(["--skip", "1"], 3000).SelectMany(a => a)];
        using var p = Process.Start(new ProcessStartInfo(Path.Combine(bin, "scskiller_warm.exe"), args)
            { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var (o, _) = (p.StandardOutput.ReadToEndAsync(), p.StandardError.ReadToEndAsync());
        await p.WaitForExitAsync().WaitAsync(Patience);
        Assert.Contains("launching the staged exe failed", await o);
        Assert.Equal(1, p.ExitCode);
    }

    /// <summary>A careful warm (pass file) of a recording with a layer's 'W' records, through the real scskiller_warm on WARP:
    /// the pass file has an entry per item the proxy replays, so every pass runs ('W' is none). Needs this checkout's proxy
    /// built.</summary>
    [Fact]
    public async Task A_careful_warm_of_a_layered_recording_runs_every_pass()
    {
        var bin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");
        if (!File.Exists(Path.Combine(bin, "scskiller_warm.exe"))) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(bin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!.StandardOutput.ReadToEnd().Trim();
        var vendor = new UnsupportedVendor(new GpuInfo(GpuVendor.Unknown, "WARP", "1.0", Convert.ToInt64(luid, 16), 0));
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        string H(int i) => Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData([(byte)i]));
        using (var db = File.Create(Path.Combine(work, "scskiller.db")))
            foreach (var rs in new[] { 1, 2, 3 })   // one compute shader on three root signatures: siblings, in passes of their own
            {
                PsoDb.Write(db, 'C', PsoDb.Compute(H(rs), H(9)));
                if (rs > 1) PsoDb.Write(db, 'W', Convert.FromHexString(new PsoDb.Rec('C', PsoDb.Compute(H(rs), H(9))).Key + H(1)));
            }
        Assert.True(WarmPasses.Write(work) > 1);
        var exe = $"scsk-careful-{Guid.NewGuid():N}"[..20] + ".exe";
        var r = await new Warmer(vendor, Path.Combine(bin, "scskiller_warm.exe")).Start(Game with { ExePath = Path.Combine(_dir, exe) }, work, new WarmOptions(1, WarmPriority.BelowNormal), null).Completion.WaitAsync(Patience);
        Assert.Null(r.Error);
        Assert.Equal(WarmOutcome.Completed, r.Outcome);
        Assert.Equal(3, r.Total);
    }

    /// <summary>The folder the Layer hook gives for a game (a copy of its layer, made in the run's work folder) goes to
    /// scskiller_warm as --layer.</summary>
    [Fact]
    public async Task A_layer_folder_goes_to_the_warm_as_layer()
    {
        var work = Work("complete");
        string? asked = null;
        await new Warmer(Vendor, _exe) { Layer = (_, w) => Path.Combine(asked = w, "layer") }.Start(Game, work, new WarmOptions(1, WarmPriority.BelowNormal), null).Completion;
        Assert.Equal(work, asked);
        Assert.Equal(["--layer", Path.Combine(work, "layer")], File.ReadAllLines(Path.Combine(work, "args.txt")).SkipWhile(x => x != "--layer").Take(2));
    }

    /// <summary>NVIDIA warms with the segment-heap build (its staged copy is that exe), the other vendors with the NT-heap
    /// one; the segment-heap build stages the proxy from its parent folder. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task NVIDIA_warms_on_the_segment_heap_build_and_the_other_vendors_on_the_NT_heap_one()
    {
        Assert.Equal(@"segheap\scskiller_warm.exe", Warmer.ExeFor(GpuVendor.Nvidia));
        Assert.Equal("scskiller_warm.exe", Warmer.ExeFor(GpuVendor.Amd));
        Assert.Equal("scskiller_warm.exe", Warmer.ExeFor(GpuVendor.Unknown));
        var bin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");
        if (!File.Exists(Path.Combine(bin, "segheap", "scskiller_warm.exe"))) return;
        bool Segment(string exe) => System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(bin, exe))).Contains(">SegmentHeap</heapType>");
        Assert.True(Segment(Warmer.ExeFor(GpuVendor.Nvidia)));
        Assert.False(Segment(Warmer.ExeFor(GpuVendor.Amd)));

        var luid = Process.Start(new ProcessStartInfo(Path.Combine(bin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!.StandardOutput.ReadToEnd().Trim();
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        var exeName = $"scsk-heap-{Guid.NewGuid():N}"[..20] + ".exe";
        var p = Process.Start(new ProcessStartInfo(Path.Combine(bin, Warmer.ExeFor(GpuVendor.Nvidia)), [work, exeName, "--adapter-luid", luid])
            { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEndAsync();
        var o = await p.StandardOutput.ReadToEndAsync();
        Assert.Contains("\"done\"", o);
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        // segheap\ has no proxy of its own: the warm ran on its parent folder's, which the proxy's log shows
        Assert.False(File.Exists(Path.Combine(bin, "segheap", "d3d12.dll")));
        Assert.Contains("loaded into ", File.ReadAllText(Path.Combine(TestEnv.WarmStage(o, work), "scskiller.log")));
    }

    static readonly AgsRegistration Townfall = new("Townfall", "UnrealEngine5.6");

    [Fact]
    public void AGS_arguments_only_on_AMD_for_a_registering_game()
    {
        const string dll = @"C:\app\native\amd_ags_x64.dll";
        Assert.Equal(["--ags", dll, "--ags-app", "Townfall", "--ags-engine", "UnrealEngine5.6"], Warmer.AgsArgs(GpuVendor.Amd, Townfall, dll, out var why));
        Assert.Null(why);
        Assert.Empty(Warmer.AgsArgs(GpuVendor.Nvidia, Townfall, dll, out why));
        Assert.Empty(Warmer.AgsArgs(GpuVendor.Amd, null, dll, out why));
        Assert.Null(why);
        Assert.Empty(Warmer.AgsArgs(GpuVendor.Amd, Townfall, null, out why));
        Assert.Contains("amd_ags_x64.dll not found", why);
        Assert.Contains("AGS app name Townfall", why);
    }

    // what the recorder logged in Mafia: The Old Country
    const string AftermathLog = """
        [      0.3s] aftermath: EnableGpuCrashDumps hooked, DX12_Initialize hooked
        [      1.3s] aftermath: EnableGpuCrashDumps(version 0x217, apis 0x3, flags 0x1, shader debug info callback set) = 0x1
        [      1.4s] aftermath: DX12_Initialize(version 0x217, feature flags 0xa) = 0x1: shader debug info (another driver cache key while crash dumps are on)
        """;

    [Fact]
    public void Aftermath_arguments_only_on_NVIDIA_for_an_init_that_changes_the_key()
    {
        const string dll = @"C:\game\GFSDK_Aftermath_Lib.x64.dll";
        var mafia = new AftermathInit(0x217, 0xa, 3, 1);
        Assert.Equal(["--aftermath", dll, "--aftermath-init", "217,a,3,1"], Warmer.AftermathArgs(GpuVendor.Nvidia, mafia, dll));
        Assert.Empty(Warmer.AftermathArgs(GpuVendor.Amd, mafia, dll));
        Assert.Empty(Warmer.AftermathArgs(GpuVendor.Nvidia, mafia, null));
        Assert.Empty(Warmer.AftermathArgs(GpuVendor.Nvidia, mafia with { Features = 2 }, dll));   // no shader debug info
        Assert.Empty(Warmer.AftermathArgs(GpuVendor.Nvidia, mafia with { Apis = 0 }, dll));       // no crash dumps
    }

    /// <summary>The recorder's logged init wins over the rules, which win over an Unreal config; none without the game's DLL.</summary>
    [Fact]
    public void Aftermath_init_from_the_recorder_log_then_the_rules()
    {
        Directory.CreateDirectory(_dir);
        var log = Path.Combine(_dir, "scskiller.log");
        var mafia = Game with { Id = "steam:1941540" };
        var e = new EngineInfo("Unreal", "5.4", null, "D3D12", false, null, false, false, AftermathShaderDebug: true);
        Assert.Equal(new AftermathInit(0x217, 0xa, 3, 1), NvAftermath.Of(mafia, null, log, "x.dll"));   // no log yet: the embedded rule
        Assert.Null(NvAftermath.Of(Game, null, log, "x.dll"));
        Assert.Null(NvAftermath.Of(Game, e, log, Path.Combine(_dir, "none.dll")));   // an Unreal config, but no Aftermath DLL version to pass
        Assert.Null(NvAftermath.Of(mafia, null, log, null));

        File.WriteAllText(log, AftermathLog.Replace("0x217", "0x218"));
        Assert.Equal(new AftermathInit(0x218, 0xa, 3, 1), NvAftermath.FromLog(log));
        Assert.Equal(new AftermathInit(0x218, 0xa, 3, 1), NvAftermath.Of(Game, null, log, "x.dll"));
        File.AppendAllText(log, "\n[ 9.0s] aftermath: DX12_Initialize(version 0x218, feature flags 0x2) = 0x1\n"
            + "[ 9.1s] aftermath: DX12_Initialize(version 0x218, feature flags 0x8) = 0xbad00005\n");   // a failed call doesn't count
        Assert.Equal(new AftermathInit(0x218, 2, 3, 1), NvAftermath.FromLog(log));
        Assert.Null(NvAftermath.Of(mafia, null, log, "x.dll"));   // the game's own last init has no shader debug info: the rule doesn't apply
        File.WriteAllText(log, AftermathLog.Split('\n')[2]);   // no crash dumps logged: not known
        Assert.Null(NvAftermath.FromLog(log));
    }

    [Fact]
    public void Aftermath_server_rules_by_game_id()
    {
        var server = RulesFile.TryParse("""
            {"rules": [
              {"id": "nv-aftermath", "requires": ["nv.aftermath.v1"], "match": {"family": "Any"}, "aftermath": {"steam:1": {"version": 536, "features": 8, "apis": 3, "dumpFlags": 1}}},
              {"id": "no-id", "requires": ["nv.aftermath.v1"], "match": {"family": "Any"}, "aftermath": {"1": {"version": 536, "features": 8}}},
              {"id": "no-cap", "requires": ["amd.ags.v1"], "match": {"family": "Any"}, "aftermath": {"steam:2": {"version": 536, "features": 8}}},
              {"id": "newer", "requires": ["nv.aftermath.v2"], "match": {"family": "Any"}, "aftermath": {"steam:3": {"version": 536, "features": 8}}}
            ]}
            """)!;
        Assert.Equal(["nv-aftermath"], server.Rules.Select(r => r.Id));
        Assert.Equal(["no-id: bad aftermath", "no-cap: bad aftermath", "newer: needs nv.aftermath.v2"], server.Dropped);
        Assert.DoesNotContain("aftermath", System.Text.Json.JsonSerializer.Serialize(RulesFile.Embedded.Rules[0], new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));
        try
        {
            RulesFile.Current = RulesFile.WithServer(server);
            Assert.Equal(new AftermathInit(536, 8, 3, 1), NvAftermath.Rule("steam:1"));
            Assert.Null(NvAftermath.Rule("steam:1941540"));   // the server's rule replaces the embedded one by id
        }
        finally { RulesFile.Current = RulesFile.Embedded; }
        Assert.Equal(new AftermathInit(0x217, 0xa, 3, 1), NvAftermath.Rule("steam:1941540"));
    }

    /// <summary>A warm process that dies starting the game's Aftermath, before its first item, is run again once without it,
    /// from the first item: a resumed warm's earlier items are under the Aftermath key.</summary>
    [Fact]
    public async Task A_warm_that_dies_starting_aftermath_runs_again_without_it()
    {
        var work = Work("aftermath");
        var exeDir = Path.Combine(_dir, "game");
        Directory.CreateDirectory(exeDir);
        File.WriteAllBytes(Path.Combine(exeDir, NvAftermath.DllName), []);
        var game = Game with { InstallDir = _dir, ExePath = Path.Combine(exeDir, "Fake-Win64-Shipping.exe") };
        var warmer = new Warmer(new UnsupportedVendor(Vendor.Gpu with { Vendor = GpuVendor.Nvidia }), _exe)
            { Aftermath = _ => new AftermathInit(0x217, 0xa, 3, 1), Log = new Lines() };
        var r = await warmer.Start(game, work, new WarmOptions(4, WarmPriority.Idle, 500, 0), null).Completion;
        Assert.Equal((WarmOutcome.Completed, true), (r.Outcome, r.AftermathDropped));
        var runs = Directory.GetFiles(work, "run-*.txt").Select(File.ReadAllText).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Single(runs, a => a.Contains("--aftermath-init 217,a,3,1") && a.Contains("--start 500 "));
        Assert.Single(runs, a => !a.Contains("--aftermath") && a.Contains("--start 0 "));
        var log = (Lines)warmer.Log!;
        lock (log.All) Assert.Contains(log.All, l => l.Contains("while starting NVIDIA Aftermath (the warm process died (exit code 0xC0000005)): compiling without it"));
    }

    sealed class Lines : IProgress<string>
    {
        public readonly List<string> All = [];
        public void Report(string l) { lock (All) All.Add(l); }
    }

    sealed class AmdVendor(GpuInfo gpu) : IGpuVendorBackend
    {
        public GpuVendor Vendor => GpuVendor.Amd;
        public GpuInfo Gpu => gpu;
        public VendorCaps Caps => new("fake-amd", true, false, false);
        public CacheUsage GetCacheUsage() => new("", 0, true);
        public CacheLimit? GetCacheLimit() => null;
        public void SetCacheLimit(CacheLimit limit) => throw new NotSupportedException();
    }

    [Fact]
    public async Task The_warm_passes_the_AGS_registration_on_AMD_only()
    {
        foreach (var amd in new[] { true, false })
        {
            var work = Work("complete");
            var log = new List<string>();
            var warmer = new Warmer(amd ? new AmdVendor(Vendor.Gpu) : Vendor, _exe) { Log = new Progress(log.Add), Ags = _ => Townfall };
            await warmer.Start(Game, work, new WarmOptions(1, WarmPriority.BelowNormal), null).Completion;
            var a = File.ReadAllLines(Path.Combine(work, "args.txt"));
            var bundled = NativeTools.Find(AmdAgs.DllName);
            if (amd && bundled != null)
                Assert.Equal(["--ags", bundled, "--ags-app", "Townfall", "--ags-engine", "UnrealEngine5.6"], a.SkipWhile(x => x != "--ags").ToArray());
            else Assert.DoesNotContain("--ags", a);
            if (amd && bundled == null) Assert.Contains(log, l => l.Contains("amd_ags_x64.dll not found"));
        }
    }

    /// <summary>The real scskiller_warm on WARP with an AGS DLL that isn't there: it says why and warms on a plain device.</summary>
    [Fact]
    public async Task The_real_warm_falls_back_to_a_plain_device_when_AGS_fails()
    {
        var bin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");
        if (!File.Exists(Path.Combine(bin, "scskiller_warm.exe"))) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(bin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!.StandardOutput.ReadToEnd().Trim();
        var work = Path.Combine(_dir, "agswork");
        Directory.CreateDirectory(work);
        var exe = $"scsk-ags-{Guid.NewGuid():N}"[..18] + ".exe";
        var missing = Path.Combine(_dir, "nowhere", AmdAgs.DllName);
        var p = Process.Start(new ProcessStartInfo(Path.Combine(bin, "scskiller_warm.exe"),
            [work, exe, "--adapter-luid", luid, "--ags", missing, "--ags-app", "Townfall", "--ags-engine", "UnrealEngine5.6"]) { StandardOutputEncoding = System.Text.Encoding.UTF8, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEndAsync();
        var output = await p.StandardOutput.ReadToEndAsync().WaitAsync(Patience);
        await p.WaitForExitAsync();
        Assert.Equal(0, p.ExitCode);
        Assert.Contains("\"done\"", output);
        Assert.Contains($"AGS app Townfall: loading {missing} failed", await err);
        Assert.Contains("a plain device", await err);
    }

    /// <summary>The real scskiller_warm on WARP: with a pass file each process creates only its pass's items (three D3D11
    /// items whose shader is missing: each fails once, in its own pass) and counts the others done; a pass file that doesn't
    /// match the dbs is an error. Needs this checkout's proxy built.</summary>
    [Fact]
    public async Task The_real_warm_creates_only_its_passs_items()
    {
        var bin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");
        if (!File.Exists(Path.Combine(bin, "scskiller_warm.exe"))) return;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(bin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!.StandardOutput.ReadToEnd().Trim();
        var vendor = new UnsupportedVendor(new GpuInfo(GpuVendor.Unknown, "WARP", "1.0", Convert.ToInt64(luid, 16), 0));
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        using (var gen = File.Create(Path.Combine(work, "scskiller_gen.db")))
            for (var i = 1; i <= 3; i++) Core.Planning.PsoDb.Write(gen, '1', Core.Planning.PsoDb.D3D11Item(Stage.Vertex, $"{i:x40}"));
        var game = Game with { ExePath = Path.Combine(_dir, $"scsk-pass-{Guid.NewGuid():N}"[..20] + ".exe") };
        var warmer = new Warmer(vendor, Path.Combine(bin, "scskiller_warm.exe"));

        File.WriteAllBytes(Path.Combine(work, WarmPasses.FileName), [1, WarmPasses.FastPass, 1]);
        var r = await warmer.Start(game, work, new WarmOptions(1, WarmPriority.BelowNormal), null).Completion.WaitAsync(Patience);
        Assert.Equal((WarmOutcome.Completed, 3L, 3L, 3L), (r.Outcome, r.Done, r.Total, r.Failed));
        Assert.Contains("(pass 255, 2 items of other passes)", File.ReadAllText(r.LogPath));

        File.WriteAllBytes(Path.Combine(work, WarmPasses.FileName), [1, WarmPasses.FastPass, 1, 1]);
        r = await warmer.Start(game, work, new WarmOptions(1, WarmPriority.BelowNormal), null).Completion.WaitAsync(Patience);
        Assert.Equal((WarmOutcome.Failed, "scskiller_pass.bin has 4 items, the dbs 3"), (r.Outcome, r.Error));
    }

    /// <summary>The real scskiller_warm on WARP: 32 D3D11 items (missing shaders: each fails at once) on one worker, with
    /// SCSKILLER_TEST_HANG11 = <paramref name="hang"/> (an 8 s stuck limit), paused for 10 s once the first batch of 16 is
    /// done, so while the worker is in item 16. Null: this checkout's proxy isn't built.</summary>
    async Task<(WarmResult R, string Log)?> PausedInItem16(string hang)
    {
        var bin = Path.Combine(TestEnv.RepoRoot, "proxy", "build", "Release");
        if (!File.Exists(Path.Combine(bin, "scskiller_warm.exe"))) return null;
        var luid = Process.Start(new ProcessStartInfo(Path.Combine(bin, "selftest.exe"), "warpluid") { RedirectStandardOutput = true, Environment = { ["SCSKILLER_SELFTEST_UNARMED"] = "1" } })!.StandardOutput.ReadToEnd().Trim();
        var vendor = new UnsupportedVendor(new GpuInfo(GpuVendor.Unknown, "WARP", "1.0", Convert.ToInt64(luid, 16), 0));
        var work = Path.Combine(_dir, "work");
        Directory.CreateDirectory(work);
        using (var gen = File.Create(Path.Combine(work, "scskiller_gen.db")))
            for (var i = 1; i <= 32; i++) Core.Planning.PsoDb.Write(gen, '1', Core.Planning.PsoDb.D3D11Item(Stage.Vertex, $"{i:x40}"));
        var game = Game with { ExePath = Path.Combine(_dir, $"scsk-pause-{Guid.NewGuid():N}"[..20] + ".exe") };
        var c = new Counter();
        var run = new Warmer(vendor, Path.Combine(bin, "scskiller_warm.exe")) { Environment = new Dictionary<string, string> { ["SCSKILLER_TEST_HANG11"] = hang } }
            .Start(game, work, new WarmOptions(1, WarmPriority.BelowNormal), c);
        await Until(() => c.Last?.Done >= 16);
        run.Pause();
        await Task.Delay(TimeSpan.FromSeconds(10));
        run.Resume();
        var r = await run.Completion.WaitAsync(Patience);
        return (r, File.ReadAllText(r.LogPath));
    }

    /// <summary>A pause suspends the process, and the time it spends suspended doesn't count toward the stuck limit: item 16
    /// is held for 5 s (a suspended Sleep doesn't run down its timeout), 10 s of pause during it.</summary>
    [Fact]
    public async Task A_paused_warm_does_not_count_the_pause_toward_the_stuck_limit()
    {
        if (await PausedInItem16("16,8000,5000") is not { } run) return;
        var (r, log) = run;
        Assert.Equal((WarmOutcome.Completed, 32L), (r.Outcome, r.Done));
        Assert.DoesNotContain("is stuck", log);
    }

    /// <summary>An item that starts as the warm resumes, before the supervisor has counted the pause, and hangs is abandoned
    /// within the limit of its start: item 16 is held until the process has been suspended, so it returns as the 10 s pause
    /// ends, and item 17 never returns. Its stamp is pinned to one made before the supervisor counted the pause (otherwise a
    /// race between the two threads at the resume), so counted from it, it would take 10 + 8 s. The warm logs that it moved
    /// item 17's start back; without that line nothing was tested. Timed by the log's own stamps, from the supervisor
    /// counting the pause to it abandoning item 17: the process's start and exit are not in it.</summary>
    [Fact]
    public async Task An_item_that_hangs_right_after_a_pause_is_found_within_the_limit()
    {
        if (await PausedInItem16("16,8000,0,1") is not { } run) return;
        var (r, log) = run;
        Assert.Equal(WarmOutcome.Completed, r.Outcome);
        Assert.DoesNotContain("drawing item 16", log);
        var lines = log.Split('\n');
        int stuck = Array.FindIndex(lines, l => l.Contains("stuck") && l.Contains("drawing item 17"));
        Assert.True(stuck >= 0, "item 17 was not abandoned");
        int resumed = Array.FindLastIndex(lines, stuck, l => l.Contains("warm: suspended for"));
        Assert.True(resumed >= 0, "the pause was not counted");
        Assert.True(Array.FindIndex(lines, resumed, stuck - resumed, l => l.Contains("stamped after a resume")) >= 0,
            "item 17's start was not moved back to the resume: its stamp didn't carry the pause");
        static double At(string line) => double.Parse(Regex.Match(line, @"^\[\s*([\d.]+)s\]").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var found = At(lines[stuck]) - At(lines[resumed]);
        Assert.True(found < 13, $"abandoned {found:F1} s after the pause was counted");   // 8 with the limit counted from the resume, 18 from the stamp
    }

    sealed class PackageKeyedVendor : IGpuVendorBackend
    {
        public GpuVendor Vendor => GpuVendor.Nvidia;
        public GpuInfo Gpu => WarmerTests.Vendor.Gpu;
        public VendorCaps Caps => new("fake-1", true, true, false, PackageKeyed: true);
        public CacheUsage GetCacheUsage() => new("", 0, true);
        public CacheLimit? GetCacheLimit() => null;
        public void SetCacheLimit(CacheLimit limit) => throw new NotSupportedException();
    }

    [Fact]
    public async Task An_xbox_game_warms_with_its_package_identity_where_the_vendor_keys_packaged_games_by_it()
    {
        var content = Path.Combine(_dir, "Content");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "appxmanifest.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications><Application Id="AppUEGameShipping" Executable="GameLaunchHelper.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>
            </Package>
            """);
        var xbox = new Game("xbox:Publisher.Game_3275kfvn8vcwc", "Fake", Store.Xbox, content, Path.Combine(content, "Fake-WinGDK-Shipping.exe"));
        Assert.Equal("Publisher.Game_3275kfvn8vcwc!AppUEGameShipping", Core.Games.XboxSource.AppUserModelId(xbox));
        Assert.Null(Core.Games.XboxSource.AppUserModelId(Game));                               // not an Xbox game
        Assert.Null(Core.Games.XboxSource.AppUserModelId(xbox with { InstallDir = _dir }));    // no manifest

        foreach (var (vendor, package) in new (IGpuVendorBackend, bool)[] { (new PackageKeyedVendor(), true), (Vendor, false) })
        {
            var work = Work("complete");
            await new Warmer(vendor, _exe).Start(xbox, work, new WarmOptions(1, WarmPriority.BelowNormal), null).Completion;
            var a = File.ReadAllLines(Path.Combine(work, "args.txt"));
            Assert.DoesNotContain("--stage-path", a);
            Assert.Equal(package ? ["--package", "Publisher.Game_3275kfvn8vcwc!AppUEGameShipping"] : [], a.SkipWhile(x => x != "--package").ToArray());
        }
    }

    /// <summary>A crash storm: every process fails one more known state object. The first eight keep the ray tracing threads,
    /// then each retry lowers them a quarter (the threads never rise, and each retry skips one more item, so it ends).</summary>
    [Fact]
    public async Task A_crash_storm_of_known_state_objects_lowers_the_ray_tracing_threads_after_eight_skips()
    {
        var work = Work("storm");
        var notes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var r = await new Warmer(Vendor, _exe).Start(Game, work, new WarmOptions(32, WarmPriority.BelowNormal), new Tee(new Counter(), p => { if (p.Note != null) notes.Add(p.Note); }))
            .Completion.WaitAsync(Patience);
        Assert.Equal(WarmOutcome.Completed, r.Outcome);
        var threads = Enumerable.Range(0, 12).Select(n => Regex.Match(File.ReadAllText(Path.Combine(work, $"run-{n}.txt")), @"--rt-threads (\d+)")
            is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0).ToArray();
        Assert.Equal(new[] { 0, 32, 32, 32, 32, 32, 32, 32, 32, 8, 2, 1 }, threads);
        Assert.Contains("skipping a ray tracing state object that crashed (8 threads)", notes);
        Assert.Contains("skipping a ray tracing state object that crashed (1 thread)", notes);
    }

    /// <summary>A process poisoned by a ray tracing fault ends with "retry": the warm goes on in a new process from its point,
    /// with its ray tracing threads and the items that failed alone skipped, until "done"; failures add up; meanwhile the
    /// progress says it's retrying.</summary>
    [Fact]
    public async Task Retry_events_relaunch_with_fewer_ray_tracing_threads_until_done()
    {
        var work = Work("retry");
        var notes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var c = new Counter();
        var skips = new System.Collections.Concurrent.ConcurrentBag<long>();
        var r = await new Warmer(Vendor, _exe).Start(Game, work, new WarmOptions(32, WarmPriority.BelowNormal),
                new Tee(c, p => { if (p.Note != null) notes.Add(p.Note); skips.Add(p.DriverSkipped); }))
            .Completion.WaitAsync(Patience);
        Assert.Equal(WarmOutcome.Completed, r.Outcome);
        Assert.Null(r.Error);
        Assert.Equal((1000L, 3L, 9L), (r.Done, r.Failed, r.DriverSkipped));   // 1 + 2 before the retry points, 0 after; skips 5 + 4
        Assert.Contains(8L, skips);   // the last process's progress: its 3 and the 5 before
        var runs = Directory.GetFiles(work, "run-*.txt").Select(File.ReadAllText).OrderBy(a => a.Contains("--skip") ? 2 : a.Contains("--rt-threads") ? 1 : 0).ToArray();
        Assert.Equal(3, runs.Length);
        Assert.DoesNotContain("--rt-threads", runs[0]);
        Assert.Contains("--start 600", runs[1]);
        Assert.Contains("--rt-threads 8", runs[1]);
        Assert.DoesNotContain("--skip", runs[1]);
        Assert.Contains("--start 700", runs[2]);
        Assert.Contains("--rt-threads 8 --skip 650", runs[2]);   // a known item keeps the threads
        Assert.Contains("skipping a ray tracing state object that crashed (8 threads)", notes);
        var e = WarmEvent.Parse("""{"event":"retry","from":7,"rtThreads":2,"failedItem":5,"done":7,"total":9,"failed":1,"driverSkipped":2,"seconds":0.1}""")!;
        Assert.Equal(("retry", 7L, 2, 5L, 2L), (e.Event, e.From, e.RtThreads, e.FailedItem, e.DriverSkipped));
    }

    /// <summary>A process whose device was removed ends with a "removed" retry: the next one gets the items that were in
    /// flight to create alone and the same ray tracing threads; the keys of what crashes the driver (the given ones and the
    /// blamed one) are skipped from then on and come back in the result, apart from the failed count.</summary>
    [Fact]
    public async Task A_removed_device_relaunches_with_the_items_in_flight_alone_and_skips_what_crashes_the_driver()
    {
        var work = Work("removed");
        var notes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var bb = new string('0', 38) + "bb";
        var r = await new Warmer(Vendor, _exe).Start(Game, work, new WarmOptions(32, WarmPriority.BelowNormal, SkipKeys: [bb]),
            new Tee(new Counter(), p => { if (p.Note != null) notes.Add(p.Note); })).Completion.WaitAsync(Patience);
        Assert.Equal(WarmOutcome.Completed, r.Outcome);
        Assert.Null(r.Error);
        Assert.Equal((1000L, 1L), (r.Done, r.Failed));
        Assert.Equal([new string('0', 38) + "aa", bb], r.Crashed!.Select(k => k.ToLowerInvariant()).Order());
        var runs = Directory.GetFiles(work, "run-*.txt").Select(File.ReadAllText).OrderBy(a => a.Contains("--isolate 611") ? 2 : a.Contains("--isolate") ? 1 : 0).ToArray();
        Assert.Equal(3, runs.Length);
        Assert.EndsWith($"--skip-keys {bb}", runs[0].TrimEnd());
        Assert.Contains("--start 600", runs[1]);
        Assert.Contains("--rt-threads 4", runs[1]);   // not a quarter: the driver wasn't poisoned by ray tracing
        Assert.EndsWith($"--skip-keys {bb} --isolate 600,610,611", runs[1].TrimEnd());
        Assert.Contains("--start 601", runs[2]);
        Assert.EndsWith($"--skip-keys {new string('0', 38)}aa,{bb} --isolate 611", runs[2].TrimEnd());
        Assert.Contains("recovering from a GPU driver crash", notes);
        var e = WarmEvent.Parse("""{"event":"retry","from":7,"rtThreads":0,"failedItem":-1,"done":7,"total":9,"failed":0,"seconds":0.1,"reason":"removed","crashed":["ab"],"isolate":[7,8]}""")!;
        Assert.Equal(("removed", "ab", 8L), (e.Reason, Assert.Single(e.Crashed!), e.Isolate![1]));
    }

    /// <summary>A replay that faults again and again with no item to blame ends the same way.</summary>
    [Theory]
    [InlineData("removedloop")]
    [InlineData("faultloop")]
    public async Task A_device_removed_again_and_again_stops_after_the_recoveries_with_an_error(string mode)
    {
        var work = Work(mode);
        var r = await new Warmer(Vendor, _exe) { MaxRecoveries = 2 }.Start(Game, work, new WarmOptions(4, WarmPriority.BelowNormal), null).Completion.WaitAsync(Patience);
        Assert.Equal(WarmOutcome.Failed, r.Outcome);
        Assert.Equal("the GPU driver kept crashing with no single pipeline to blame, so the compile stopped", r.Error);
        Assert.Equal((10L, 3L), (r.Done, r.Failed));   // one failure per process, the last one's counted once
        Assert.True(r.Resumable);   // the next compile goes on from item 10
    }

    /// <summary>A process whose creates kept hanging ends with a "hang" retry: the next one starts at its point with the same
    /// threads and the hung items after it skipped, no crash keys; the hung items count as skipped for that compile.</summary>
    [Fact]
    public async Task A_hang_retry_relaunches_with_the_hung_items_skipped_and_no_crash_keys()
    {
        var work = Work("hang");
        var notes = new System.Collections.Concurrent.ConcurrentBag<string>();
        var r = await new Warmer(Vendor, _exe).Start(Game, work, new WarmOptions(20, WarmPriority.BelowNormal),
            new Tee(new Counter(), p => { if (p.Note != null) notes.Add(p.Note); })).Completion.WaitAsync(Patience);
        Assert.Equal((WarmOutcome.Completed, 1000L, 1L, 20L), (r.Outcome, r.Done, r.Failed, r.DriverSkipped));
        Assert.Null(r.Crashed);
        var runs = Directory.GetFiles(work, "run-*.txt").Select(File.ReadAllText).OrderBy(a => a.Contains("--skip")).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.Contains("--threads 20 ", runs[1]);
        Assert.Contains("--start 600", runs[1]);
        Assert.Contains("--skip 610,611", runs[1]);
        Assert.DoesNotContain("--rt-threads", runs[1]);
        Assert.DoesNotContain("--skip-keys", runs[1]);
        Assert.DoesNotContain("--isolate", runs[1]);
        Assert.Contains("skipping pipelines that hung the GPU driver", notes);
    }

    /// <summary>Hang retries that go on forever stop after the recoveries, at a point the next compile resumes from.</summary>
    [Fact]
    public async Task Hang_retries_again_and_again_stop_after_the_recoveries_resumable()
    {
        var work = Work("hangloop");
        var r = await new Warmer(Vendor, _exe) { MaxRecoveries = 2 }.Start(Game, work, new WarmOptions(4, WarmPriority.BelowNormal), null).Completion.WaitAsync(Patience);
        Assert.Equal((WarmOutcome.Failed, Warmer.HungError, 10L, 60L), (r.Outcome, r.Error, r.Done, r.DriverSkipped));
        Assert.True(r.Resumable);
        Assert.Null(r.Crashed);
    }

    // Stand-in for scskiller_warm.exe in cmd alone (PowerShell is seconds per process): process n is a removal with nothing
    // blamed when odd and blames item n/2's key when even, up to <crashers> keys, then "done" or removals that blame none.
    // Every retry lists the given key bb, as scskiller_warm lists the known keys it skipped. reason.txt: each retry's reason,
    // or "mix": a removal when n/2 is even, a fault when odd.
    const string CrashScript = """
        @echo off
        setlocal enabledelayedexpansion
        set /p n=<"%~1\count.txt"
        set /p c=<"%~1\crashers.txt"
        set /p tail=<"%~1\end.txt"
        set /p reason=<"%~1\reason.txt"
        set /a n+=1, k=n/2, odd=n%%2, last=2*c, m=k%%2
        if "!reason!"=="mix" if !m!==0 (set reason=removed) else (set reason=fault)
        >"%~1\count.txt" echo !n!
        set key=0000000000000000000000000000000000000000!k!
        set key=!key:~-40!
        set bb=00000000000000000000000000000000000000bb
        if !n! GTR !last! if "!tail!"=="done" (
          echo {"event":"done","done":1000,"total":1000,"failed":0,"seconds":0.1,"stopped":false,"crashed":["!bb!"]}
          exit /b 0
        )
        if !n! GTR !last! set odd=1
        if !odd!==1 (
          echo {"event":"retry","from":!n!,"rtThreads":0,"failedItem":-1,"done":!n!,"total":1000,"failed":0,"seconds":0.1,"reason":"!reason!","crashed":["!bb!"],"isolate":[!n!]}
          exit /b 3
        )
        echo {"event":"retry","from":!n!,"rtThreads":0,"failedItem":-1,"done":!n!,"total":1000,"failed":0,"seconds":0.1,"reason":"!reason!","crashed":["!bb!","!key!"],"isolate":[!n!]}
        exit /b 3
        """;

    async Task<(WarmResult Result, int Processes)> Crashers(string name, int crashers, string tail, int maxRecoveries, string reason = "removed")
    {
        var work = Work(name);
        File.WriteAllText(Path.Combine(work, "count.txt"), "0");
        File.WriteAllText(Path.Combine(work, "reason.txt"), reason);
        File.WriteAllText(Path.Combine(work, "crashers.txt"), crashers.ToString());
        File.WriteAllText(Path.Combine(work, "end.txt"), tail);
        var exe = Path.Combine(_dir, "fake_crash.cmd");
        File.WriteAllText(exe, CrashScript.ReplaceLineEndings("\r\n"));
        var r = await new Warmer(Vendor, exe) { MaxRecoveries = maxRecoveries }
            .Start(Game, work, new WarmOptions(32, WarmPriority.BelowNormal, SkipKeys: [new string('0', 38) + "bb"]), null).Completion.WaitAsync(Patience);
        return (r, int.Parse(File.ReadAllText(Path.Combine(work, "count.txt")).Trim()));
    }

    /// <summary>A removal whose relaunch blames a new item doesn't count against the recoveries: 40 items that remove the
    /// device, each found by a removal and a relaunch, are all skipped in one compile.</summary>
    [Fact]
    public async Task Removals_that_keep_blaming_new_items_finish_the_compile()
    {
        var (r, processes) = await Crashers("crashers", 40, "done", 20);
        Assert.Equal(WarmOutcome.Completed, r.Outcome);
        Assert.Null(r.Error);
        Assert.Equal(41, r.Crashed!.Count);
        Assert.Equal(81, processes);
    }

    [Fact]
    public async Task Removals_that_stop_blaming_new_items_stop_at_the_recoveries()
    {
        var (r, processes) = await Crashers("crashersloop", 3, "loop", 2);
        Assert.Equal(WarmOutcome.Failed, r.Outcome);
        Assert.Equal("4 pipelines that crash the GPU driver were skipped. Compiling again continues where it stopped", r.Error);
        Assert.Equal(4, r.Crashed!.Count);
        Assert.True(r.Resumable);
        Assert.Equal(9, processes);   // 6 that found the 3, then 3 in a row that found none
    }

    /// <summary>A pipeline whose create raises an exception in the driver (an access violation) is found and skipped like one
    /// that removes the device: 4 of them in one compile, and 10 mixed with removals.</summary>
    [Theory]
    [InlineData("fault", 4, 9)]
    [InlineData("mix", 10, 21)]
    public async Task Faults_that_keep_blaming_new_items_finish_the_compile(string reason, int crashers, int processesExpected)
    {
        var (r, processes) = await Crashers("faulters-" + reason, crashers, "done", 20, reason);
        Assert.Equal(WarmOutcome.Completed, r.Outcome);
        Assert.Null(r.Error);
        Assert.Equal(crashers + 1, r.Crashed!.Count);
        Assert.Equal(processesExpected, processes);
    }

    [Fact]
    public async Task Faults_that_stop_blaming_new_items_stop_at_the_recoveries()
    {
        var (r, processes) = await Crashers("faultersloop", 3, "loop", 2, "mix");
        Assert.Equal(WarmOutcome.Failed, r.Outcome);
        Assert.Equal("4 pipelines that crash the GPU driver were skipped. Compiling again continues where it stopped", r.Error);
        Assert.Equal(9, processes);   // 6 that found the 3, then 3 in a row that found none
    }

    [Fact]
    public async Task Removals_that_keep_blaming_new_items_stop_at_the_process_ceiling()
    {
        var (r, processes) = await Crashers("crashersmax", 200, "done", 20);
        Assert.Equal(WarmOutcome.Failed, r.Outcome);
        Assert.Equal("129 pipelines that crash the GPU driver were skipped. Compiling again continues where it stopped", r.Error);
        Assert.Equal(256, processes);
    }

    /// <summary>An exception while the warm is watched (the cache folder reset under a progress sample) ends its process
    /// instead of leaving it compiling.</summary>
    [Fact]
    public async Task A_failure_while_watching_a_warm_ends_its_process()
    {
        var work = Work("stuck");
        var run = new Warmer(new FailingCache(), _exe).Start(Game, work, new WarmOptions(1, WarmPriority.BelowNormal), new Counter());
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => run.Completion.WaitAsync(Patience));
        var pid = int.Parse(File.ReadAllText(Path.Combine(work, "pid.txt")).Trim());
        try { Assert.True(Process.GetProcessById(pid).WaitForExit(Patience)); }
        catch (ArgumentException) { }   // already gone
    }

    sealed class FailingCache : IGpuVendorBackend
    {
        int calls;
        public GpuVendor Vendor => GpuVendor.Unknown;
        public GpuInfo Gpu { get; } = new(GpuVendor.Unknown, "Fake GPU", "1.0", 1, 0);
        public VendorCaps Caps { get; } = new("fake", true, true, false);
        public CacheUsage GetCacheUsage() => Interlocked.Increment(ref calls) == 1 ? new("", 0, true) : throw new DirectoryNotFoundException("cache folder gone");
        public CacheLimit? GetCacheLimit() => null;
        public void SetCacheLimit(CacheLimit limit) => throw new NotSupportedException();
    }


    /// <summary>A careful warm: one process per pass with --pass, in order, the careful ones on CarefulThreads and the fast one
    /// on Threads; progress and Done count the whole warm's items, failures add up; a StartAt inside pass 2 starts there, at
    /// that pass's item.</summary>
    [Fact]
    public async Task A_work_folder_with_passes_runs_one_process_per_pass()
    {
        foreach (var (startAt, expected) in new[]
                 {
                     (0L, new[] { "--pass 1 --start 0 --threads 2", "--pass 2 --start 0 --threads 2", "--pass 255 --start 0 --threads 8" }),
                     (3L, ["--pass 2 --start 2 --threads 2", "--pass 255 --start 0 --threads 8"]),
                 })
        {
            var work = Work("passes");
            foreach (var old in Directory.GetFiles(work, "run-*.txt")) File.Delete(old);
            File.WriteAllBytes(Path.Combine(work, WarmPasses.FileName), WarmPasses.Split(["a", "b", "a", null, "a", "b", "c"], 4));   // 1 1 2 1 F F F
            var notes = new System.Collections.Concurrent.ConcurrentBag<string>();
            var c = new Counter();
            var r = await new Warmer(Vendor, _exe).Start(Game, work, new WarmOptions(8, WarmPriority.BelowNormal, startAt, CarefulThreads: 2),
                new Tee(c, p => { if (p.Note != null) notes.Add(p.Note); })).Completion.WaitAsync(Patience);
            Assert.Equal(WarmOutcome.Completed, r.Outcome);
            Assert.Equal((7L, 7L, (long)expected.Length), (r.Done, r.Total, r.Failed));
            string Run(string args) => $"--pass {Regex.Match(args, @"--pass (\d+)").Groups[1]} --start {Regex.Match(args, @"--start (\d+)").Groups[1]} --threads {Regex.Match(args, @"--threads (\d+)").Groups[1]}";
            Assert.Equal(expected, Directory.GetFiles(work, "run-*.txt").Select(f => Run(File.ReadAllText(f))).Order());
            Assert.Contains("careful compile: the plan's other pipelines at full speed", notes);
            Assert.Equal((7L, 7L), (c.Last!.Done, c.Last.Total));
        }
    }
    /// <summary>Every pass of a careful warm gets the keys that crash the driver: the given ones and the ones an earlier pass
    /// blamed; a removed device inside a pass recovers in that pass (the same --pass, the items in flight alone) and the
    /// remaining passes still run.</summary>
    [Fact]
    public async Task Each_pass_skips_the_crash_keys_and_a_removed_device_recovers_inside_its_pass()
    {
        var work = Work("passcrash");
        File.WriteAllBytes(Path.Combine(work, WarmPasses.FileName), WarmPasses.Split(["a", "b", "a", null, "a", "b", "c"], 4));   // 1 1 2 1 F F F
        var bb = new string('0', 38) + "bb";
        var cc = new string('0', 38) + "cc";
        var r = await new Warmer(Vendor, _exe).Start(Game, work, new WarmOptions(8, WarmPriority.BelowNormal, SkipKeys: [bb], CarefulThreads: 2), null)
            .Completion.WaitAsync(Patience);
        Assert.Equal((WarmOutcome.Completed, 7L), (r.Outcome, r.Done));
        Assert.Equal([cc], r.Crashed!.Select(k => k.ToLowerInvariant()));
        var runs = Directory.GetFiles(work, "run-*.txt").Select(File.ReadAllText).ToList();
        string? Arg(string a, string name) => Regex.Match(a, name + @" (\S+)") is { Success: true } m ? m.Groups[1].Value : null;
        Assert.Equal(["1", "1", "2", "255"], runs.Select(a => Arg(a, "--pass")!).Order());
        Assert.All(runs, a => Assert.Contains(bb, Arg(a, "--skip-keys")));
        Assert.Equal("1", Arg(runs.Single(a => a.Contains("--isolate")), "--isolate"));
        Assert.All(runs.Where(a => Arg(a, "--pass") is "2" or "255"), a => Assert.Equal($"{bb},{cc}", Arg(a, "--skip-keys")));
    }
    /// <summary>Every pass process of a careful warm runs like a normal warm of that game: on AMD with its AGS registration
    /// and its stage path (an Xbox package: AGS without a stage path); on NVIDIA without AGS.</summary>
    [Fact]
    public async Task Each_pass_gets_the_games_AGS_registration_and_stage_path_like_a_normal_warm()
    {
        var content = Path.Combine(_dir, "XContent");
        Directory.CreateDirectory(content);
        File.WriteAllText(Path.Combine(content, "appxmanifest.xml"), """
            <?xml version="1.0" encoding="utf-8"?>
            <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
              <Applications><Application Id="App" Executable="Game.exe" EntryPoint="Windows.FullTrustApplication" /></Applications>
            </Package>
            """);
        var xbox = new Game("xbox:P.G_1", "G", Store.Xbox, content, Path.Combine(content, "Game.exe"));
        var bundled = NativeTools.Find(AmdAgs.DllName);
        foreach (var (vendor, game) in new (IGpuVendorBackend, Game)[] { (new AmdVendor(Vendor.Gpu), Game), (new PackageKeyedVendor(), Game), (new AmdVendor(Vendor.Gpu), xbox) })
        {
            var work = Work("passes");
            foreach (var old in Directory.GetFiles(work, "run-*.txt")) File.Delete(old);
            File.WriteAllBytes(Path.Combine(work, WarmPasses.FileName), WarmPasses.Split(["a", "b", "a", null, "a", "b", "c"], 4));   // 1 1 2 1 F F F
            var r = await new Warmer(vendor, _exe) { Ags = _ => Townfall }.Start(game, work, new WarmOptions(8, WarmPriority.BelowNormal, CarefulThreads: 4), null)
                .Completion.WaitAsync(Patience);
            Assert.Equal(WarmOutcome.Completed, r.Outcome);
            var runs = Directory.GetFiles(work, "run-*.txt").Select(File.ReadAllText).ToList();
            Assert.Equal(3, runs.Count);   // passes 1, 2 and the fast one
            var amd = vendor.Vendor == GpuVendor.Amd;   // an Xbox app game too
            var ags = amd && bundled != null ? $"--ags {bundled} --ags-app Townfall --ags-engine UnrealEngine5.6" : null;
            foreach (var a in runs)
            {
                if (ags != null) Assert.Contains(ags, a); else Assert.DoesNotContain("--ags", a);
                if (game == Game) Assert.Contains(@"--stage-path nowhere\Binaries\Win64\Fake-Win64-Shipping.exe", a);
                else Assert.DoesNotContain("--stage-path", a);
                Assert.Contains("--pass", a);
            }
        }
    }
    sealed class Tee(IProgress<WarmProgress> a, Action<WarmProgress> b) : IProgress<WarmProgress>
    {
        public void Report(WarmProgress p) { a.Report(p); b(p); }
    }

    [Fact]
    public async Task Progress_carries_cache_growth_sampled_at_most_every_few_seconds()
    {
        var vendor = new GrowingCache();
        var c = new Counter();
        var r = await new Warmer(vendor, _exe).Start(Game, Work("complete"), new WarmOptions(1, WarmPriority.BelowNormal), c).Completion;
        Assert.InRange(c.Count, 1, 2);        // progress + done, 0.1 s apart (usually folded into one report): one sample serves both
        Assert.Equal(100, c.Last!.CacheGrowthBytes);
        Assert.Equal(200, r.CacheGrowthBytes); // measured again after exit
        Assert.Equal(3, vendor.Calls);
    }

    sealed class GrowingCache : IGpuVendorBackend
    {
        public int Calls;
        public GpuVendor Vendor => GpuVendor.Unknown;
        public GpuInfo Gpu { get; } = new(GpuVendor.Unknown, "Fake GPU", "1.0", 1, 0);
        public VendorCaps Caps { get; } = new("fake", true, true, false);
        public CacheUsage GetCacheUsage() => new("", 1000 + 100 * (Interlocked.Increment(ref Calls) - 1), true);
        public CacheLimit? GetCacheLimit() => null;
        public void SetCacheLimit(CacheLimit limit) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Error_line_and_exit_code_fail_the_run()
    {
        var r = await new Warmer(Vendor, _exe).Start(Game, Work("error"), new WarmOptions(1, WarmPriority.BelowNormal), null).Completion;
        Assert.Equal(WarmOutcome.Failed, r.Outcome);
        Assert.Equal("no D3D12 device", r.Error);
        Assert.Matches(@"\\stage-\d+-1\\scskiller\.log$", r.LogPath);   // failed before its start: the stage event came first
    }

    [Fact]
    public async Task A_process_that_fails_before_its_start_reports_its_own_log_not_an_earlier_ones()
    {
        var work = Work("retryfail");
        var r = await new Warmer(Vendor, _exe).Start(Game, work, new WarmOptions(32, WarmPriority.BelowNormal), null).Completion.WaitAsync(Patience);
        Assert.Equal(WarmOutcome.Failed, r.Outcome);
        var second = Directory.GetFiles(work, "run-*.txt").Single(f => File.ReadAllText(f).Contains("--rt-threads"));
        var pid = Path.GetFileNameWithoutExtension(second)["run-".Length..];
        Assert.Equal(Path.Combine(work, $"stage-{pid}-1", "scskiller.log"), r.LogPath);
    }

    [Fact]
    public async Task Exit_without_done_is_a_failure()
    {
        var r = await new Warmer(Vendor, _exe).Start(Game, Work("nodone"), new WarmOptions(1, WarmPriority.BelowNormal), null).Completion;
        Assert.Equal(WarmOutcome.Failed, r.Outcome);
        Assert.Equal(10, r.Done);
        Assert.False(r.Resumable);   // not a clean point: what it compiled may not be written
    }

    /// <summary>The tree a job takes in and a pause suspends: an older process whose exited parent's pid the warm now has
    /// (Windows reuses pids) isn't its child, nor anything under it.</summary>
    [Fact]
    public void A_process_tree_leaves_out_an_older_process_naming_a_reused_pid()
    {
        (int, int, long?)[] procs =
        [
            (100, 1, 50),     // the warm
            (200, 100, 60),   // its staged child
            (300, 200, 60),   // started in the same tick
            (400, 100, 10),   // the runner's launcher: its parent exited long ago and 100 was reused
            (500, 400, 20),   // what the launcher started
            (600, 100, null), // couldn't be opened
            (700, 600, 70),
        ];
        Assert.Equal([100, 200, 300], ProcessTree.Tree(100, procs));
        Assert.Equal([100], ProcessTree.Tree(100, [(100, 1, null), (200, 100, 60)]));   // the root's time unknown: nothing under it
        Assert.Equal([100], ProcessTree.Tree(100, [(200, 100, 60)]));                   // exited
    }

    [Fact]
    public async Task Warms_and_their_children_are_in_a_job_that_kills_them_when_its_owner_ends()
    {
        // cmd -> ping under a name of its own (other tests or other programs may run ping): a warm and the staged child it starts
        var name = $"scsk-ping-{Guid.NewGuid():N}"[..20];
        var ping = Path.Combine(_dir, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ping);
        var pinger = Path.Combine(_dir, "pinger.cmd");
        File.WriteAllText(pinger, $"@\"{ping}\" -t 127.0.0.1\r\n");
        int[] Pings() => Process.GetProcessesByName(name).Select(p => p.Id).ToArray();
        var run = new Warmer(Vendor, pinger).Start(Game, Work("job"), new WarmOptions(1, WarmPriority.BelowNormal), null);
        await Until(() => Pings().Length == 1);
        var child = Pings()[0];
        Assert.True(WarmJob.Current.Contains(child));   // the Warmer's start path put the whole tree in this process's job

        // what this process ending does to its job: a job of our own, with the same limits, closed
        var job = new WarmJob();
        var tree = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"\"{ping}\" -t 127.0.0.1\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        await Until(() => Pings().Length == 2);
        var grandchild = Process.GetProcessById(Pings().Single(id => id != child));
        job.Add(tree.Id);   // after it started ping: Add takes the running descendants too
        Assert.True(job.Contains(grandchild.Id));
        job.Dispose();
        Assert.True(grandchild.WaitForExit(30_000), "ping survived its job's owner");
        Assert.True(tree.WaitForExit(30_000));

        run.Stop();   // cmd ignores the stop event: StuckAfter (30 s) would end it; don't wait for that here
        Process.GetProcessById(child).Kill();
        await run.Completion.WaitAsync(Patience);   // cmd exits after ping: the work folder is free again
    }

    [Fact]
    public async Task A_warm_deaf_to_the_stop_event_is_ended_and_says_so()
    {
        var c = new Counter();
        var run = new Warmer(Vendor, _exe) { StuckAfter = TimeSpan.FromSeconds(2) }.Start(Game, Work("stuck"), new WarmOptions(1, WarmPriority.BelowNormal), c);
        await Until(() => c.Count >= 1);
        var clock = Stopwatch.StartNew();
        run.Stop();
        var r = await run.Completion.WaitAsync(Patience);
        Assert.Equal((WarmOutcome.Failed, Warmer.StuckError), (r.Outcome, r.Error));
        Assert.True(clock.Elapsed.TotalSeconds >= 1.5, $"ended after {clock.Elapsed}: before StuckAfter");
    }

    [Fact]
    public async Task A_relaunched_warm_is_in_the_job_too_and_is_ended_if_it_ignores_the_stop()
    {
        var c = new Counter();
        var work = Work("retrystuck");
        var run = new Warmer(Vendor, _exe) { StuckAfter = TimeSpan.FromSeconds(2) }.Start(Game, work, new WarmOptions(1, WarmPriority.BelowNormal), c);
        await Until(() => File.Exists(Path.Combine(work, "pid.txt")));
        Assert.True(WarmJob.Current.Contains(int.Parse(File.ReadAllText(Path.Combine(work, "pid.txt")).Trim())));   // the second process's powershell
        run.Stop();
        var r = await run.Completion.WaitAsync(Patience);
        Assert.Equal((WarmOutcome.Failed, Warmer.StuckError), (r.Outcome, r.Error));
    }

    [Fact]
    public async Task A_warm_that_stopped_and_is_still_writing_its_cache_is_not_ended()
    {
        var c = new Counter();
        var run = new Warmer(Vendor, _exe) { StuckAfter = TimeSpan.FromSeconds(20) }.Start(Game, Work("flush"), new WarmOptions(1, WarmPriority.BelowNormal), c);
        await Until(() => c.Count >= 1);
        run.Stop();
        var r = await run.Completion.WaitAsync(Patience);   // done at once, then 3 s "writing the cache"
        Assert.Equal(WarmOutcome.Stopped, r.Outcome);
        Assert.Null(r.Error);
    }

    [Fact]
    public async Task Pause_suspends_resume_continues_stop_sets_the_event()
    {
        var c = new Counter();
        var run = new Warmer(Vendor, _exe).Start(Game, Work("stop"), new WarmOptions(1, WarmPriority.BelowNormal), c);
        await Until(() => c.Count >= 3);

        run.Pause();
        await Until(() => Steady(c, TimeSpan.FromSeconds(1)));   // lines already in the pipe drain and the last one is reported
        var paused = c.Count;
        await Task.Delay(1500);
        Assert.Equal(paused, c.Count);

        run.Resume();
        await Until(() => c.Count > paused + 2);

        run.Stop();
        var r = await run.Completion.WaitAsync(Patience);
        Assert.Equal(WarmOutcome.Stopped, r.Outcome);
        Assert.True(r.Done >= c.Last!.Done);
    }

    [Fact]
    public async Task Stop_while_paused_still_stops()
    {
        var c = new Counter();
        var run = new Warmer(Vendor, _exe).Start(Game, Work("stop"), new WarmOptions(1, WarmPriority.BelowNormal), c);
        await Until(() => c.Count >= 1);
        run.Pause();
        run.Stop();
        var r = await run.Completion.WaitAsync(Patience);
        Assert.Equal(WarmOutcome.Stopped, r.Outcome);
    }

    /// <summary>For anything the fake warm has to do: PowerShell alone can take seconds to start on a loaded PC.</summary>
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    static async Task Until(Func<bool> cond)
    {
        for (var t = DateTime.Now; !cond(); await Task.Delay(50))
            if (DateTime.Now - t > Patience) throw new TimeoutException();
    }

    /// <summary>True once the count hasn't changed for <paramref name="quiet"/> (call repeatedly, e.g. from Until).</summary>
    static bool Steady(Counter c, TimeSpan quiet)
    {
        var before = c.Count;
        Thread.Sleep(quiet);
        return c.Count == before;
    }
}
