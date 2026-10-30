namespace SCSKiller.Core.App;

/// <summary>crash.log in the data folder: the exceptions nothing handled, with the app version and OS build.</summary>
public static class CrashLog
{
    public const string FileName = "crash.log";

    static readonly Lock _gate = new();
    static Exception? _last;
    static bool _lastWritten;

    /// <summary>Appends <paramref name="e"/>, once: a start that failed is logged, then rethrown into the unhandled handlers.
    /// A log over 1 MB moves to crash.old.log first. Handlers on several threads take turns, so one rotation serves them all.
    /// False: the log couldn't be written.</summary>
    public static bool Write(string dataDir, Exception e)
    {
        lock (_gate)
        {
            if (_last == e) return _lastWritten;
            (_last, _lastWritten) = (e, false);
            try
            {
                Directory.CreateDirectory(dataDir);   // a first start that fails may have made nothing yet
                var path = Path.Combine(dataDir, FileName);
                try { if (new FileInfo(path) is { Exists: true, Length: > 1 << 20 } full) full.MoveTo(Path.Combine(dataDir, "crash.old.log"), true); }
                catch (IOException) { }   // another process holds the old log: this entry goes on the long one
                _lastWritten = ScsKiller.AppendLog(dataDir, FileName, $"SCSKiller {AppVersion.Current} on {(OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux")} {Environment.OSVersion.Version}: {e}");
            }
            catch (Exception) { }   // a log that can't be written must not turn the crash into another one
            return _lastWritten;
        }
    }
}
