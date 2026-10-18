using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace SCSKiller.Core.App;

/// <summary>The app's <see cref="ScsKiller.Log"/>: app.log in the data folder, written in order by one background task
/// through <see cref="ScsKiller.AppendLog"/> (timestamps, at most <see cref="MaxBytes"/> then app.log.1). Report only
/// masks and queues (up to 10,000 lines), so it never blocks or throws. No line carries the user profile folder (it
/// becomes ~) or the Windows user name (it becomes &lt;user&gt;), as in the recorder's log.</summary>
public sealed class AppLog : IProgress<string>
{
    public const string FileName = "app.log";
    public const long MaxBytes = 1 << 20;

    readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });   // a stuck disk drops lines rather than grow memory
    readonly Task _writer;
    readonly Regex? _profile, _user;

    public AppLog(string dataDir, string file = FileName, long maxBytes = MaxBytes, string? profile = null, string? user = null)
    {
        profile ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        (_profile, _user) = Maskers([profile, ShortPath(profile)], user ?? Environment.UserName);
        _writer = Task.Run(async () =>
        {
            await foreach (var line in _lines.Reader.ReadAllAsync())
                ScsKiller.AppendLog(dataDir, file, line, maxBytes);
        });
    }

    public void Report(string value) => _lines.Writer.TryWrite(Mask(value, _profile, _user));

    /// <summary>Writes what is queued, waiting at most <paramref name="wait"/>; lines reported afterwards are dropped.</summary>
    public Task FlushAsync(TimeSpan wait)
    {
        _lines.Writer.TryComplete();
        return _writer.WaitAsync(wait).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    const RegexOptions Options = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    const string Word = @"[\p{L}\p{N}_]";

    // each spelling of the profile in either separator, with or without \\?\, not when it starts a longer folder name
    // (C:\Users\bobby, C:\Users\bob.old); a period that ends a sentence still masks
    internal static (Regex? Profile, Regex? User) Maskers(IEnumerable<string?> profiles, string? user)
    {
        var spellings = profiles
            .Select(p => (p ?? "").Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => string.Join(@"[\\/]", parts.Select(Regex.Escape)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var p = spellings.Count == 0 ? null
            : new Regex(@"(?:[\\/]{2}\?[\\/])?(?:" + string.Join("|", spellings) + $@")(?![\p{{L}}\p{{N}}_\-]|\.{Word})", Options);
        var u = user is { Length: >= 3 } ? new Regex($"(?<!{Word}){Regex.Escape(user)}(?!{Word})", Options) : null;
        return (p, u);
    }

    internal static string Mask(string line, Regex? profile, Regex? user)
    {
        if (profile != null) line = profile.Replace(line, "~");
        return user?.Replace(line, "<user>") ?? line;
    }

    // the 8.3 alias (C:\Users\BOBSMI~1) a short %TEMP% hands out; null when the volume has none
    static string? ShortPath(string path)
    {
        var b = new StringBuilder(1024);
        try { return GetShortPathNameW(path, b, b.Capacity) is > 0 and < 1024 ? b.ToString() : null; }
        catch (Exception) { return null; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetShortPathNameW(string longPath, StringBuilder shortPath, int size);
}
