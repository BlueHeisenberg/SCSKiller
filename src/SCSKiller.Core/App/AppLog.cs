using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace SCSKiller.Core.App;

/// <summary>The app's <see cref="ScsKiller.Log"/>: app.log in the data folder, written in order by one background task
/// through <see cref="ScsKiller.AppendLog"/> (timestamps, at most <see cref="MaxBytes"/> then app.log.1). Report only
/// masks and queues (up to 10,000 lines), so it never blocks or throws. No line carries the user profile folder (it
/// becomes ~) or the Windows user name (it becomes &lt;user&gt;), as in the recorder's log.</summary>
public sealed partial class AppLog : IProgress<string>
{
    public const string FileName = "app.log";
    public const long MaxBytes = 1 << 20;

    readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });   // a stuck disk drops lines rather than grow memory
    readonly Task _writer;
    readonly Regex? _profile, _user;
    readonly string _dataDir, _file;
    int _started;

    /// <summary>Why the process ends, for the exit line: set by the path that ends it, "exit" when nothing did.</summary>
    public string ExitReason { get; set; } = "exit";

    public AppLog(string dataDir, string file = FileName, long maxBytes = MaxBytes, string? profile = null, string? user = null)
    {
        (_dataDir, _file) = (dataDir, file);
        profile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (_profile, _user) = Maskers([profile], user ?? Environment.UserName);
        _writer = Task.Run(async () =>
        {
            await foreach (var line in _lines.Reader.ReadAllAsync())
                ScsKiller.AppendLog(dataDir, file, line, maxBytes);
        });
    }

    public void Report(string value) => _lines.Writer.TryWrite(Mask(value, _profile, _user));

    const string StartMark = " started (pid ", ExitMark = " exiting (pid ";

    /// <summary>The start line, and when the last lifecycle line left in app.log is a start (that run never logged an exit)
    /// a line saying so with its start time. The exit line comes with <see cref="FlushAsync"/>, which every exit path
    /// ends with, and with the process exit for the paths that don't.</summary>
    public void Start(string where)
    {
        var last = LastLifecycle();
        Mark($"SCSKiller {AppVersion.Current}{StartMark}{Environment.ProcessId}) on {Mask(where, _profile, _user)}");
        if (last != null && last.Contains(StartMark)) Mark($"previous run ended unexpectedly, it started at {last[..last.IndexOf(' ', last.IndexOf(' ') + 1)]}");
        _started = 1;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushAsync(TimeSpan.FromSeconds(1)).Wait();
    }

    // the lifecycle lines are not masked: a user name equal to one of their words would break the markers
    void Mark(string line) => _lines.Writer.TryWrite(line);

    // a rotation to app.log.1 right after the start line leaves the marker there
    string? LastLifecycle()
    {
        foreach (var name in new[] { _file, _file + ".1" })
            try
            {
                var path = Path.Combine(_dataDir, name);
                if (!File.Exists(path)) continue;
                using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var r = new StreamReader(f);
                string? mark = null;
                for (string? line; (line = r.ReadLine()) != null;)
                    if (line.Contains(StartMark) || line.Contains(ExitMark)) mark = line;
                if (mark != null) return mark;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    /// <summary>Writes what is queued, waiting at most <paramref name="wait"/>; lines reported afterwards are dropped.</summary>
    public Task FlushAsync(TimeSpan wait)
    {
        lock (_lines)   // the exit line goes in before anything completes the channel
        {
            if (Interlocked.Exchange(ref _started, 0) == 1) Mark($"SCSKiller{ExitMark}{Environment.ProcessId}): {ExitReason}");
            _lines.Writer.TryComplete();
        }
        return _writer.WaitAsync(wait).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string Word = @"[\p{L}\p{N}_]";

    // each spelling of the profile in either separator, with or without \\?\, not when it starts a longer folder name
    // (C:\Users\bobby, C:\Users\bob.old); a period that ends a sentence still masks
    internal static (Regex? Profile, Regex? User) Maskers(IEnumerable<string?> profiles, string? user)
    {
        var spellings = profiles
            .OfType<string>()
            .SelectMany(p => new[] { p, ShortPath(p), p.StartsWith("/var/home/") ? p[4..] : null })   // Fedora Atomic: /home links to /var/home
            .Select(p => (Rooted: p?.StartsWith('/') == true, Parts: (p ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries)))
            .Where(p => p.Parts.Length >= (p.Rooted ? 1 : 2))   // never a bare Windows drive
            .Select(p => (p.Rooted ? @"[\\/]" : "") + string.Join(@"[\\/]+", p.Parts.Select(Spelled)))   // + : doubled or JSON-escaped separators
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var p = spellings.Count == 0 ? null
            : new Regex(@"(?:[\\/]{2}\?[\\/])?(?:" + string.Join("|", spellings) + $@")(?![\p{{L}}\p{{N}}_\-]|\.{Word})", Options);
        // a word character just before still counts as a boundary when it ends an escape (\n, \u0020, %20)
        var u = user is { Length: >= 3 } ? new Regex($@"(?:(?<!{Word})|(?<=\\[nrtbf]|\\u[0-9a-f]{{4}}|%[0-9a-f]{{2}})){Spelled(user)}(?!{Word})", Options) : null;
        return (p, u);
    }

    // as written, or each character as a JSON writer escapes it (\u00eb) or a URI percent-encodes it (%C3%AB), in any
    // mix: Options covers the hex case, the upper and lower case forms cover an encoded ZOË
    static string Spelled(string s) => string.Concat(s.EnumerateRunes().Select(r => r.IsAscii && Rune.IsLetterOrDigit(r)
        ? r.ToString()
        : $"(?:{string.Join("|", new[] { r, Rune.ToUpperInvariant(r), Rune.ToLowerInvariant(r) }.Distinct().SelectMany(Encodings))})"));

    static IEnumerable<string> Encodings(Rune r) =>
        [Regex.Escape(r.ToString()), string.Concat(r.ToString().Select(c => $@"\\u{(int)c:x4}")), string.Concat(Encoding.UTF8.GetBytes(r.ToString()).Select(b => $"%{b:X2}"))];

    internal static string Mask(string line, Regex? profile, Regex? user)
    {
        if (profile != null) line = profile.Replace(line, "~");
        return user?.Replace(line, "<user>") ?? line;
    }

    // the 8.3 alias (C:\Users\BOBSMI~1) a short %TEMP% hands out; null when the volume has none
    private static partial string? ShortPath(string path);
}
