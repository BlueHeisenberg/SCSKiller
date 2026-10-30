using System.Diagnostics;

namespace SCSKiller.Core.Warming;

static partial class WarmHost
{
    public static partial IWarmHost For(Game game, string workDir, GpuInfo gpu, IProgress<string>? log, string? steamRoot, bool vulkan, long? cacheCap, bool mesa) => new WindowsWarmHost(workDir);

    public static partial string? Refusal(Game game, string? steamRoot, bool vulkan, bool mesa) => null;
}

/// <summary>scskiller_warm as Core's child, its stdout and stderr on pipes, in <see cref="WarmJob.Current"/>. Stop is a named
/// event every process of the run polls.</summary>
sealed class WindowsWarmHost : IWarmHost
{
    readonly string _workDir;
    readonly EventWaitHandle _stop;
    bool _disposed;

    public WindowsWarmHost(string workDir)
    {
        _workDir = workDir;
        var stopName = $"Local\\SCSKiller.Stop.{Guid.NewGuid():N}";
        _stop = new EventWaitHandle(false, EventResetMode.ManualReset, stopName);
        StopArgs = ["--stop-event", stopName];
    }

    public string WarmPath(string host) => host;

    public string HostPath(string warm) => warm;

    public IReadOnlyList<string> StopArgs { get; }

    public IReadOnlyList<string> AdapterArgs(GpuInfo gpu) => ["--adapter-luid", gpu.AdapterLuid.ToString("X16")];

    public void Stop() => _stop.Set();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Dispose();
    }

    public IWarmProcess Start(string exe, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> env, Action<string> errorLine)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8, WorkingDirectory = _workDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        try { WarmJob.Current.Add(p.Id); }   // dies with this SCSKiller process, whatever ends it (WarmJob)
        catch (Exception) { p.Kill(entireProcessTree: true); throw; }
        var errorEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        p.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null) errorEnd.TrySetResult();
            else if (e.Data.Length > 0) errorLine(e.Data);
        };
        p.BeginErrorReadLine();
        return new Child(p, errorEnd.Task);
    }

    /// <summary>How long after the process exits its stderr may stay open: a process it started that inherited the handle
    /// (or one the driver keeps from tearing down) holds it for as long as it lives.</summary>
    static readonly TimeSpan ErrorGrace = TimeSpan.FromSeconds(5);

    sealed class Child(Process p, Task errorEnd) : IWarmProcess
    {
        public TextReader Output => p.StandardOutput;

        public Task<int?> Exited { get; } = Exit(p, errorEnd);

        public bool HasExited => p.HasExited;

        public void Suspend(bool suspend) => ProcessTree.Suspend(p.Id, suspend);

        public void Kill()
        {
            try { p.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }   // exited meanwhile
        }

        // not WaitForExitAsync: it also waits for the redirected streams' end, which a held handle never brings
        static async Task<int?> Exit(Process p, Task errorEnd)
        {
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            p.Exited += (_, _) => exited.TrySetResult();
            p.EnableRaisingEvents = true;
            if (p.HasExited) exited.TrySetResult();
            await exited.Task;
            await Task.WhenAny(errorEnd, Task.Delay(ErrorGrace));
            return p.ExitCode;
        }
    }
}
