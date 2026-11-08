using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;
using SCSKiller.Core.Carved;
using SCSKiller.Core.Games;

namespace SCSKiller.Core.Vulkan;

public static partial class Hookshot
{
    static partial void StartRun(Game game, string workDir, GpuInfo gpu, IProgress<WarmProgress>? progress, IProgress<string>? log, ref IWarmRun? run) =>
        run = new HiddenRun(game, workDir, gpu, progress, log);

    /// <summary>In the game's own data folder while a hidden run may leave crash markers: the exe, the port's data folder as it resolved,
    /// then the markers absent before the run. Gone once the run ended them itself.</summary>
    const string PendingFile = "hookshot-markers.txt";

    static partial void RecoverAll(string gamesDir, IProgress<string>? log)
    {
        try
        {
            if (!Directory.Exists(gamesDir)) return;
            foreach (var dir in Directory.EnumerateDirectories(gamesDir)) RecoverPending(Path.Combine(dir, PendingFile), log);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Report($"Hookshot markers: {e.Message}"); }
    }

    /// <summary>A pending record's markers go, unless a run of that exe (by path) may own them now; the record goes with them.</summary>
    static void RecoverPending(string pending, IProgress<string>? log)
    {
        try
        {
            if (!File.Exists(pending)) return;
            var lines = File.ReadAllLines(pending);
            if (lines.Length < 2 || OtherRuns(lines[0], lines[1])) return;
            foreach (var marker in lines.Skip(2).Where(m => Markers.Contains(m)))
            {
                var path = Path.Combine(lines[1], marker);
                if (!File.Exists(path)) continue;
                File.Delete(path);
                log?.Report($"{path}: left by a hidden precompile that ended with SCSKiller, taken out");
            }
            File.Delete(pending);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { log?.Report($"{pending}: {e.Message}"); }
    }

    static readonly string[] Markers = [NotesFile + ".running", "hookshot_prepared.bin.warming"];

    /// <summary>A process of <paramref name="exe"/> runs on the data folder <paramref name="dir"/>, none of this SCSKiller's hidden runs; one
    /// that can't be read counts as running. The name only narrows the list, the path decides.</summary>
    static bool OtherRuns(string exe, string dir)
    {
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
            using (p)
            {
                if (Ours(p.Id)) continue;
                try
                {
                    if (p.MainModule?.FileName is not { } image || string.Equals(GameFiles.FinalPath(CarvedReader.HookshotDataDir(image)), dir, StringComparison.OrdinalIgnoreCase)) return true;
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException) { return true; }
            }
        return false;
    }

    /// <summary>The exe runs --prepare-shaders first: no window, "@@" lines on stdout, done once "@@done shaders" and its own exit 0.
    /// An older build exits without an "@@" line ("cannot open --prepare-shaders") and gets the run below instead:
    /// hookshot.exe --user &lt;work&gt;\hookshot-user with RX_VK_PRECOMPILE=1 HOS_HEADLESS=1 HOS_AUDIO=0 on a desktop of its
    /// own (the "Preparing shaders" console it opens shows nowhere), in a job of its own, its log (stderr) in the work folder.
    /// Done once "vk: precompile:" is logged and then the 600th present or a save of mkw_vk_pipelines.bin, at most a minute
    /// later: the port saves that cache every 300 presents and not at exit. Then the job ends it, never anything by name. An exit
    /// of its own before that is a failure.</summary>
    sealed class HiddenRun : IWarmRun
    {
        static readonly TimeSpan PrecompileLimit = TimeSpan.FromMinutes(30), SaveLimit = TimeSpan.FromMinutes(1);
        static readonly Regex Presents = new(@"vk: (\d+) presents", RegexOptions.CultureInvariant);
        const string PrecompileLine = "vk: precompile: ", SkippedMarker = "hookshot_prepare_skipped", NotesDroppedLine = "did not get through the precompile", DriverFile = "hookshot-driver.txt", PipelineCache = "mkw_vk_pipelines.bin";

        readonly Game _game;
        readonly string _work, _logPath, _driver;
        string _dir;
        readonly IProgress<WarmProgress>? _progress;
        readonly IProgress<string>? _log;
        readonly Stopwatch _clock = new(), _phase = new();
        volatile bool _stop;
        bool _paused;
        nint _process;
        string[] _absent = [];
        string _pending = "";
        readonly object _gate = new();

        public HiddenRun(Game game, string workDir, GpuInfo gpu, IProgress<WarmProgress>? progress, IProgress<string>? log)
        {
            (_game, _work, _dir, _progress, _log) = (game, workDir, CarvedReader.HookshotDataDir(game.ExePath), progress, log);
            _logPath = Path.Combine(workDir, "hookshot.log");
            _driver = $"{gpu.Name} {gpu.DriverVersion}";
            Completion = Task.Run(Go);
        }

        public Task<WarmResult> Completion { get; }

        // the app may pause twice (the queue's pause, then its watch): one suspend, one resume
        public void Pause()
        {
            lock (_gate)
            {
                if (_paused) return;
                _paused = true;
                _phase.Stop();
                if (_process != 0) NtSuspendProcess(_process);
            }
        }

        public void Resume()
        {
            lock (_gate)
            {
                if (!_paused) return;
                _paused = false;
                if (_process == 0) return;
                NtResumeProcess(_process);
                _phase.Start();
            }
        }

        public void Stop()
        {
            _stop = true;
            Resume();
        }

        WarmResult End(WarmOutcome outcome, string? error) => new(outcome, outcome == WarmOutcome.Completed ? 1 : 0, 1, 0, _clock.Elapsed, 0, _logPath, error,
            Resumable: outcome == WarmOutcome.Stopped);

        WarmResult Go()
        {
            _clock.Start();
            // the port deletes a link, not what it points to: the delete protection below holds only a plain file in a plain folder
            if (Linked(_dir) || Linked(Path.GetDirectoryName(_game.ExePath)!) || Linked(Path.Combine(_dir, NotesFile)))
                return End(WarmOutcome.Failed, $"the game folder, its bin folder or its {NotesFile} is a link: the port's precompile isn't run over it");
            _dir = GameFiles.FinalPath(_dir) ?? _dir;   // the folder as it resolves now: a marker removal later goes to the same one
            _pending = Path.Combine(Path.GetDirectoryName(_work)!, PendingFile);
            RecoverPending(_pending, _log);
            if (EnvValue(_dir, "RX_VK_PRECOMPILE") == 0) return End(WarmOutcome.Failed, "hookshot_env.txt turns the port's precompile off (RX_VK_PRECOMPILE=0)");
            var user = Path.Combine(_work, "hookshot-user");
            Directory.CreateDirectory(user);
            var driverFile = Path.Combine(Path.GetDirectoryName(_work)!, DriverFile);
            // the port's marker holds its translator's stamp only: after a driver change its driver part would be skipped
            var again = File.Exists(Path.Combine(_dir, "hookshot_prepared.bin")) && (File.Exists(driverFile) ? File.ReadAllText(driverFile) : null) != _driver;
            if (again && EnvValue(_dir, "RX_VK_PREPARE_AGAIN") == 0)
                return End(WarmOutcome.Failed, "hookshot_env.txt keeps the port from preparing its shaders again for this driver (RX_VK_PREPARE_AGAIN=0)");
            var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string?)e.Value ?? "";
            (env["RX_VK_PRECOMPILE"], env["HOS_HEADLESS"], env["HOS_AUDIO"], env["HOOKSHOT_NO_UPDATE_CHECK"]) = ("1", "1", "0", "1");
            if (again) env["RX_VK_PREPARE_AGAIN"] = "1";
            if (VulkanLayer.Key(_game) is { } key) env["DISABLE_" + VulkanLayer.LayerName(key)] = "1";
            // held without delete sharing while the port runs: a crash marker from an earlier start, or a player's start meeting
            // ours, makes the port delete its notes, which then fails
            FileStream? notes = null;
            try { notes = new FileStream(Path.Combine(_dir, NotesFile), FileMode.Open, FileAccess.Read, FileShare.ReadWrite); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return End(WarmOutcome.Failed, $"{NotesFile} can't be held for the precompile: {e.Message}"); }
            var desktopName = $"scskiller-hookshot-{Environment.ProcessId}";
            var desktop = CreateDesktopW(desktopName, null, 0, 0, 0x10000000 /* GENERIC_ALL */, 0);
            if (desktop == 0)
            {
                notes?.Dispose();
                return End(WarmOutcome.Failed, $"the hidden desktop for hookshot.exe couldn't be made (error {Marshal.GetLastWin32Error()})");
            }
            try
            {
                if (Launch(desktopName, env, user, again, driverFile, prepare: true) is { } prepared) return prepared;
                // without --prepare-shaders this marker makes the port skip its precompile
                if (File.Exists(Path.Combine(_dir, SkippedMarker)))
                    return End(WarmOutcome.Failed, $"hookshot.exe doesn't know --prepare-shaders and {SkippedMarker} makes it skip its precompile");
                return Launch(desktopName, env, user, again, driverFile, prepare: false)!;
            }
            finally
            {
                CloseDesktop(desktop);
                notes?.Dispose();
            }
        }

        /// <summary>One start of the port. Null: a --prepare-shaders start the build didn't know.</summary>
        WarmResult? Launch(string desktopName, SortedDictionary<string, string> env, string user, bool again, string driverFile, bool prepare)
        {
            _absent = Markers.Where(m => !File.Exists(Path.Combine(_dir, m))).ToArray();
            // before the launch: if SCSKiller dies, its job ends the port and the next start takes these markers out
            try { File.WriteAllLines(_pending, [_game.ExePath, _dir, .. _absent]); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return End(WarmOutcome.Failed, $"{_pending} can't be written: {e.Message}"); }
            var job = CreateJobObjectW(0, null);
            var initialized = false;
            nint list = 0, jobSlot = 0;
            try
            {
                var limits = new ExtendedLimitInformation { LimitFlags = 0x2000 /* JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE */ };
                if (job == 0 || !SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf<ExtendedLimitInformation>())) throw new Win32Exception();
                // created inside the job (PROC_THREAD_ATTRIBUTE_JOB_LIST): no moment where a crash of ours leaves it outside
                nint size = 0;
                InitializeProcThreadAttributeList(0, 1, 0, ref size);
                list = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(list, 1, 0, ref size)) throw new Win32Exception();
                initialized = true;
                jobSlot = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(jobSlot, job);
                if (!UpdateProcThreadAttribute(list, 0, 0x2000D /* JOB_LIST */, jobSlot, IntPtr.Size, 0, 0)) throw new Win32Exception();
                using var output = new FileStream(_logPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                if (!SetHandleInformation(output.SafeFileHandle, 1, 1)) throw new Win32Exception();   // HANDLE_FLAG_INHERIT: the port's stderr
                var si = new StartupInfoEx
                {
                    Info = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Desktop = desktopName, Flags = 0x101 /* USESHOWWINDOW | USESTDHANDLES */, ShowWindow = 0,
                        StdOutput = output.SafeFileHandle.DangerousGetHandle(), StdError = output.SafeFileHandle.DangerousGetHandle() },
                    AttributeList = list,
                };
                var block = new StringBuilder();
                foreach (var (k, v) in env) block.Append(k).Append('=').Append(v).Append('\0');
                block.Append('\0');
                var cmd = new StringBuilder($"\"{_game.ExePath}\"{(prepare ? " --prepare-shaders" : "")} --user \"{user}\"");
                const uint flags = 0x4 /* SUSPENDED */ | 0x10 /* NEW_CONSOLE */ | 0x400 /* UNICODE_ENVIRONMENT */ | 0x80000 /* EXTENDED_STARTUPINFO_PRESENT */;
                if (!CreateProcessW(_game.ExePath, cmd, 0, 0, true, flags, block.ToString(), _dir, ref si, out var pi)) throw new Win32Exception();
                Running[pi.ProcessId] = true;
                try
                {
                    _log?.Report($"{_game.Name}: hookshot.exe runs hidden for its precompile (process {pi.ProcessId}{(again ? ", its shaders prepared again for this driver" : "")})");
                    lock (_gate)
                    {
                        _process = pi.Process;
                        ResumeThread(pi.Thread);
                        if (_paused) NtSuspendProcess(pi.Process);   // a pause that came while it started
                        else _phase.Start();
                    }
                    var result = Watch(pi.Process, job, prepare);
                    if (result?.Outcome == WarmOutcome.Completed) File.WriteAllText(driverFile, _driver);
                    return result;
                }
                finally
                {
                    lock (_gate) _process = 0;
                    TerminateJobObject(job, 1);
                    WaitForSingleObject(pi.Process, 10_000);
                    // ended by itself: a crash marker it left is the port's own sign, kept for its next start
                    try { File.Delete(_pending); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                    Running.TryRemove(pi.ProcessId, out _);
                    CloseHandle(pi.Thread);
                    CloseHandle(pi.Process);
                }
            }
            catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException)
            {
                return End(WarmOutcome.Failed, $"hookshot.exe couldn't be started for its precompile: {e.Message}");
            }
            finally
            {
                if (initialized) DeleteProcThreadAttributeList(list);
                if (list != 0) Marshal.FreeHGlobal(list);
                if (jobSlot != 0) Marshal.FreeHGlobal(jobSlot);
                if (job != 0) CloseHandle(job);
            }
        }

        WarmResult? Watch(nint process, nint job, bool prepare)
        {
            using var read = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var lines = new StreamReader(read);
            var cache = Path.Combine(_dir, PipelineCache);
            DateTime? savedBefore = null;
            var partial = "";
            bool at = false, done = false;
            string? error = null;
            var share = 0.0;
            while (true)
            {
                var exited = WaitForSingleObject(process, 500) == 0;
                for (string? chunk; (chunk = lines.ReadToEnd()).Length > 0;)
                {
                    var all = (partial + chunk).Split('\n');
                    partial = all[^1];
                    foreach (var line in all[..^1])
                    {
                        if (prepare && line.StartsWith("@@", StringComparison.Ordinal))
                        {
                            at = true;
                            ResetPhase();   // the time limit counts from the last "@@" line
                            if (line.StartsWith("@@progress ", StringComparison.Ordinal) && double.TryParse(line[11..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f))
                                share = Math.Clamp(f, 0, 1);
                            else if (line.StartsWith("@@done shaders", StringComparison.Ordinal)) done = true;
                            else if (line.StartsWith("@@error ", StringComparison.Ordinal)) error = line[8..].Trim();
                            continue;
                        }
                        // an earlier start's crash marker: the port skips the precompile (its marker is gone now, the held notes stay)
                        if (!at && savedBefore == null && line.Contains(NotesDroppedLine, StringComparison.Ordinal) || at && line.Contains("its notes are dropped", StringComparison.Ordinal))
                        {
                            Kill(process, job);
                            return End(WarmOutcome.Failed, "hookshot.exe skipped its precompile after an earlier start that didn't finish it: compile again");
                        }
                        if (savedBefore == null && line.Contains(PrecompileLine, StringComparison.Ordinal))
                        {
                            savedBefore = File.Exists(cache) ? File.GetLastWriteTimeUtc(cache) : DateTime.MinValue;
                            _log?.Report($"{_game.Name}: {line.Trim()}");
                            if (!at) ResetPhase();
                        }
                        else if (!at && savedBefore != null && Presents.Match(line) is { Success: true } m && long.Parse(m.Groups[1].Value) >= 600)
                            return End(WarmOutcome.Completed, null);
                        else if (line.Contains("vk: prepared ", StringComparison.Ordinal) || at && line.Contains("did not finish the driver's part", StringComparison.Ordinal)) _log?.Report($"{_game.Name}: {line.Trim()}");
                    }
                }
                if (!at && savedBefore is { } before && (File.Exists(cache) && File.GetLastWriteTimeUtc(cache) > before || _phase.Elapsed > SaveLimit))
                    return End(WarmOutcome.Completed, null);
                if (_stop)
                {
                    Kill(process, job);
                    return End(WarmOutcome.Stopped, null);
                }
                if (exited)
                {
                    var code = GetExitCodeProcess(process, out var c) ? c : 0;
                    if (at)
                        return done && code == 0 ? End(WarmOutcome.Completed, null)
                            : End(WarmOutcome.Failed, $"hookshot.exe --prepare-shaders failed (exit code {code}): {error ?? "no reason given"}, see {_logPath}");
                    if (prepare && savedBefore == null) return null;
                    return End(WarmOutcome.Failed, $"hookshot.exe ended (exit code {code}) "
                        + (savedBefore == null ? "before its precompile finished" : "before it saved its pipeline cache") + $", see {_logPath}");
                }
                if ((at || savedBefore == null) && _phase.Elapsed > PrecompileLimit)
                {
                    Kill(process, job);
                    return End(WarmOutcome.Failed, $"hookshot.exe's precompile didn't finish within {PrecompileLimit.TotalMinutes:0} minutes, see {_logPath}");
                }
                // Done counts seconds while the total is unknown: the queue shows the note, and a count that moves isn't a stall
                _progress?.Report(new WarmProgress((long)_clock.Elapsed.TotalSeconds, 0, 0, 0, Note: at ? TranslatingNote(_clock.Elapsed, share) : savedBefore == null ? TranslatingNote(_clock.Elapsed) : SavingNote(_phase.Elapsed)));
            }
        }

        /// <summary>Ends the port mid-step: its crash markers would stay, which the next start reads as "the last start died here"
        /// and answers by dropping the notes. Only one this run made goes: absent before it, and no other process of the exe
        /// running that could have made it (one that can't be read counts as running).</summary>
        void Kill(nint process, nint job)
        {
            TerminateJobObject(job, 1);
            WaitForSingleObject(process, 10_000);
            DropOurMarkers();
        }

        void ResetPhase()
        {
            lock (_gate)
            {
                _phase.Reset();
                if (!_paused) _phase.Start();
            }
        }

        void DropOurMarkers()
        {
            foreach (var marker in _absent)
            {
                var path = Path.Combine(_dir, marker);
                try
                {
                    if (!File.Exists(path) || OtherRuns(_game.ExePath, _dir)) continue;
                    File.Delete(path);
                    _log?.Report($"{_game.Name}: {marker} our stopped run left taken out");
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _log?.Report($"{_game.Name}: {marker} left: {e.Message}"); }
            }
        }

        static bool Linked(string path)
        {
            try { return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0; }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return false; }
        }

        /// <summary>The value hookshot_env.txt gives <paramref name="name"/> as the port reads it (its last line, atoi, names
        /// without case as Windows has them). Null: the file doesn't set it.</summary>
        static long? EnvValue(string dir, string name)
        {
            try
            {
                var path = Path.Combine(dir, "hookshot_env.txt");
                if (!File.Exists(path)) return null;
                var last = File.ReadLines(path).Select(l => l.Trim().TrimStart('﻿')).LastOrDefault(l => l.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase));
                if (last == null) return null;
                return Regex.Match(last[(name.Length + 1)..], @"^\s*[+-]?\d+") is { Success: true } n && long.TryParse(n.Value, out var v) ? v : 0;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfo
    {
        public int Size;
        public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
        public short ShowWindow, Reserved2Size;
        public nint Reserved2, StdInput, StdOutput, StdError;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct StartupInfoEx { public StartupInfo Info; public nint AttributeList; }
    struct ProcessInformation { public nint Process, Thread; public int ProcessId, ThreadId; }

    [StructLayout(LayoutKind.Sequential)]
    struct ExtendedLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, nint processAttributes, nint threadAttributes, bool inherit, uint flags, string env,
        string dir, ref StartupInfoEx si, out ProcessInformation pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(nint list);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint CreateDesktopW(string name, string? device, nint devmode, uint flags, uint access, nint security);
    [DllImport("user32.dll")] static extern bool CloseDesktop(nint desktop);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateJobObjectW(nint security, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetInformationJobObject(nint job, int infoClass, ref ExtendedLimitInformation info, int length);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool TerminateJobObject(nint job, uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll")] static extern uint ResumeThread(nint thread);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(nint handle, uint ms);
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(nint process, out uint code);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
    [DllImport("ntdll.dll")] static extern int NtSuspendProcess(nint process);
    [DllImport("ntdll.dll")] static extern int NtResumeProcess(nint process);
}
