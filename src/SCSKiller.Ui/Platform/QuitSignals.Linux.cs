using System.Runtime.InteropServices;

namespace SCSKiller.Ui.Platform;

/// <summary>SIGTERM (a logout or shutdown, kill), SIGINT (Ctrl+C) and SIGHUP (its terminal closed) end the app as Windows
/// ends its session: the first one runs <c>first</c>, a SIGTERM or SIGINT at least <see cref="Settle"/> later runs
/// <c>again</c>, on a thread of their own. None of them ends the process by itself.</summary>
sealed class QuitSignals : IDisposable
{
    /// <summary>systemd sends a session's processes SIGHUP right after SIGTERM, and a stop may repeat its SIGTERM.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(1);

    readonly PosixSignalRegistration[] _registrations;
    readonly Lock _gate = new();
    long? _firstAt;

    public QuitSignals(Action<PosixSignal> first, Action<PosixSignal> again) =>
        _registrations = [.. new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP }.Select(s => PosixSignalRegistration.Create(s, c =>
        {
            c.Cancel = true;
            var now = Environment.TickCount64;
            Action<PosixSignal>? run;
            lock (_gate)
                if (_firstAt is not { } at) (run, _firstAt) = (first, now);
                else run = c.Signal != PosixSignal.SIGHUP && now - at >= Settle.TotalMilliseconds ? again : null;
            run?.Invoke(c.Signal);
        }))];

    public void Dispose()
    {
        foreach (var r in _registrations) r.Dispose();
    }
}
