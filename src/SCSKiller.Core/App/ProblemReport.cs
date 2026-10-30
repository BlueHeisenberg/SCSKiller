using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

/// <summary>A problem report this PC sent: the server's id, the game's name and when. Kept in reports.json, nothing else
/// of the report.</summary>
public sealed record SentProblem(string Id, string Game, DateTimeOffset At)
{
    [JsonIgnore] public string Detail => $"{Game} · {At.ToLocalTime():g}";
}

/// <summary>"Report a problem": the text the user reads in full before pressing Send, and its POST /v1/problems. No token,
/// no device, no account: the body is the user's message and that text, nothing else.</summary>
public static class ProblemReport
{
    public const int MaxLogLines = 50, MaxLineChars = 300, MaxMessageChars = 4000, MaxHitches = 20, MaxTraversalChars = 700;

    /// <summary>Masks what could name the user or the PC: the profile folder (~), the Windows user name, the computer
    /// name, e-mail addresses and access tokens.</summary>
    public sealed class Masker(string? profile, string? user, string? machine)
    {
        readonly (Regex? Profile, Regex? User) pc = AppLog.Maskers([profile], user);
        readonly Regex? machine = machine is not { Length: > 0 } m ? null
            : m.Length >= 4 ? new(Regex.Escape(m), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)   // also inside a longer name (BOBBY-PC_backup)
            : new($@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(m)}(?![\p{{L}}\p{{N}}_])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        // the whole address-like span around an @, whatever the local part or domain holds
        static readonly Regex Email = new(@"(?:""[^""\r\n]*""|[^\s""'<>()\[\],;]+)@[^\s""'<>(),;]+", RegexOptions.CultureInvariant);
        static readonly Regex Token = new(@"\bv\d\.(?:local|public)\.[A-Za-z0-9_\-.]+|\bBearer\s+\S+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public static Masker ThisPc { get; } = new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.UserName, Environment.MachineName);

        public string Mask(string s)
        {
            s = AppLog.Mask(Token.Replace(Email.Replace(s, "<email>"), "<token>"), pc.Profile, null);
            if (machine != null) s = machine.Replace(s, "<pc>");   // before the user name, which a PC's name often holds
            return AppLog.Mask(s, null, pc.User);
        }
    }

    /// <summary>The report as shown and sent. <paramref name="system"/>: <see cref="CompatReport.SystemLine"/>.
    /// <paramref name="log"/> null leaves the log section out, <paramref name="traversal"/> null or empty the Traversal one.</summary>
    public static string Build(string appVersion, string game, string store, string? vendor, string? gpu, string? driver, string system,
        IEnumerable<(string Label, string Value)> page, IEnumerable<string>? log, Masker mask, IReadOnlyList<string>? traversal = null)
    {
        var b = new System.Text.StringBuilder();
        b.AppendLine($"SCSKiller {appVersion}");
        b.AppendLine($"Game: {game} ({store})");
        b.AppendLine($"GPU: {gpu ?? "unknown"} ({vendor ?? "unknown vendor"}), driver {driver ?? "unknown"}");
        b.AppendLine(system);
        b.AppendLine();
        b.AppendLine("On the game's page:");
        foreach (var (label, value) in page)
            if (!string.IsNullOrWhiteSpace(value)) b.AppendLine($"{label}: {value.ReplaceLineEndings(" ")}");
        if (log != null)
        {
            b.AppendLine();
            b.AppendLine("app.log, the latest lines about this game:");
            var any = false;
            foreach (var line in log)
            {
                var masked = mask.Mask(line);   // before the cut, which could split what the mask matches
                b.AppendLine(masked.Length > MaxLineChars ? masked[..MaxLineChars] + "…" : masked);
                any = true;
            }
            if (!any) b.AppendLine("(none)");
        }
        if (traversal is { Count: > 0 })
        {
            b.AppendLine();
            b.AppendLine("Traversal, the game's scskiller.log, its last session:");
            foreach (var line in traversal)
            {
                var masked = mask.Mask(line);
                b.AppendLine(masked.Length > MaxTraversalChars ? masked[..MaxTraversalChars] + "…" : masked);
            }
        }
        return mask.Mask(b.ToString().TrimEnd());
    }

    /// <summary>From the last session of <paramref name="scskillerLog"/> with "traversal:" lines: the startup line, the last
    /// VRAM reading, the <see cref="MaxHitches"/> worst frames over the probe's ms in their order, and the last pool line. A line naming a
    /// path is left out.</summary>
    public static IReadOnlyList<string> TraversalLines(string scskillerLog)
    {
        List<string> session = [], last = [];
        foreach (var line in Read(scskillerLog))
        {
            if (line.Contains("] loaded into ", StringComparison.Ordinal))
            {
                if (session.Count > 0) last = session;
                session = [];
            }
            else if (line.Contains("] traversal: ", StringComparison.Ordinal) && !PathLike.IsMatch(line)) session.Add(Stamp.Replace(line, "["));
        }
        if (session.Count > 0) last = session;
        var hitches = last.Select((l, i) => (l, i, m: Hitch.Match(l))).Where(h => h.m.Success)
            .OrderByDescending(h => double.Parse(h.m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Take(MaxHitches).OrderBy(h => h.i).Select(h => h.l);
        return [.. new[] { last.FirstOrDefault(l => l.Contains("] traversal: probe ", StringComparison.Ordinal)), last.LastOrDefault(l => Vram.IsMatch(l)) }.OfType<string>(),
            .. hitches,
            .. new[] { last.LastOrDefault(l => l.Contains("] traversal: pool: ", StringComparison.Ordinal)) }.OfType<string>()];
    }

    static readonly Regex Hitch = new(@"\] traversal: frame at [\d.]+ s: (\d+(?:\.\d+)?) ms in ", RegexOptions.CultureInvariant);
    static readonly Regex Stamp = new(@"^\[\s+", RegexOptions.CultureInvariant);
    static readonly Regex Vram = new(@"(?<!non-)\blocal usage \d+ of \d+ MB", RegexOptions.CultureInvariant);
    static readonly Regex PathLike = new(@"[A-Za-z]:/|\\|<(?:game|system32)>", RegexOptions.CultureInvariant);

    /// <summary>The last <see cref="MaxLogLines"/> lines of app.log (app.log.1 before it) that name any of
    /// <paramref name="about"/>; none when there is no log yet.</summary>
    public static IReadOnlyList<string> LogLines(string dataDir, IEnumerable<string> about)
    {
        string[] terms = [.. about.Where(t => t is { Length: >= 3 }).Distinct(StringComparer.OrdinalIgnoreCase)];
        var kept = new Queue<string>();
        foreach (var file in new[] { AppLog.FileName + ".1", AppLog.FileName })
            foreach (var line in Read(Path.Combine(dataDir, file)))
                if (terms.Any(t => line.Contains(t, StringComparison.OrdinalIgnoreCase)) && !Auth.IsMatch(line))
                {
                    kept.Enqueue(line);
                    if (kept.Count > MaxLogLines) kept.Dequeue();
                }
        return [.. kept];
    }

    static IEnumerable<string> Read(string path)
    {
        string text;
        try
        {
            // the writer appends and rotates while this reads
            using var f = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(f);
            text = r.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    // app.log has no sign-in lines today: should one name a game, its tokens and ids stay out
    static readonly Regex Auth = new(@"token|patreon|account|device[ _]?id|refresh|secret|password|sign(?:ed)?[ -]?in", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly Regex Id = new("^[0-9a-f]{8}$", RegexOptions.CultureInvariant);
    static readonly Lazy<HttpClient> Http = new(() => new(RouteFailover.Default, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) });

    public static byte[] Body(string message, string report) => JsonSerializer.SerializeToUtf8Bytes(new { message, report }, Json);

    /// <summary>POST /v1/problems, only on the user's Send. The report's id when the server took it, else what to tell the
    /// user. Never throws but cancellation.</summary>
    public static async Task<(string? Id, string? Problem)> SendAsync(string message, string report, CancellationToken ct = default)
    {
        try
        {
            using var content = new ByteArrayContent(Body(message, report));
            content.Headers.ContentType = new("application/json");
            using var r = await Http.Value.PostAsync(new Uri(RouteFailover.Default.Primary, "v1/problems"), content, ct);
            if (r.StatusCode == HttpStatusCode.OK
                && JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct)).RootElement.TryGetProperty("id", out var id)
                && id.GetString() is { } s && Id.IsMatch(s))
                return (s, null);
            return (null, r.StatusCode switch
            {
                HttpStatusCode.NotFound or HttpStatusCode.NotImplemented => "Problem reports aren't open yet.",
                HttpStatusCode.RequestEntityTooLarge => "The report is too long. Leave the log lines out and try again.",
                HttpStatusCode.TooManyRequests => "Too many reports today. Try again tomorrow.",
                HttpStatusCode.BadRequest => "The server didn't take this report.",
                _ => "Not sent. Try again later.",
            });
        }
        catch (Exception) when (!ct.IsCancellationRequested)   // offline, or an answer this version can't read
        {
            return (null, "Not sent. Try again later.");
        }
    }
}
