using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

/// <summary>"Report a problem": the text the user reads in full before pressing Send, and its POST /v1/problems. No token,
/// no device, no account: the body is the user's message and that text, nothing else.</summary>
public static class ProblemReport
{
    public const int MaxLogLines = 50, MaxLineChars = 300, MaxMessageChars = 4000;

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

    /// <summary>The report as shown and sent. <paramref name="log"/> null leaves the log section out.</summary>
    public static string Build(string appVersion, string game, string store, string? vendor, string? gpu, string? driver, string windows,
        IEnumerable<(string Label, string Value)> page, IEnumerable<string>? log, Masker mask)
    {
        var b = new System.Text.StringBuilder();
        b.AppendLine($"SCSKiller {appVersion}");
        b.AppendLine($"Game: {game} ({store})");
        b.AppendLine($"GPU: {gpu ?? "unknown"} ({vendor ?? "unknown vendor"}), driver {driver ?? "unknown"}");
        b.AppendLine($"Windows: {windows}");
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
        return mask.Mask(b.ToString().TrimEnd());
    }

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
