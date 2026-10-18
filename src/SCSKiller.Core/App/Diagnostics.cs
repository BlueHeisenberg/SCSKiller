using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SCSKiller.Core.App;

public sealed record DiagnosticRejection(string Kind, string Hr, long Count);
public sealed record DiagnosticKey(string Key, string Kind, string Hr);

/// <summary>Why a compile failed, as fixed values only: never a message. <see cref="Stage"/>: index, plan, materialize or
/// warm (where it failed); <see cref="Reason"/>: exception (an exception in the app, <see cref="Exception"/> its type
/// name), exit_code (the warm process exited with <see cref="Hr"/>), no_done_event, stuck (it had to be ended), reported
/// (the warm process reported an error, whose text isn't sent).</summary>
public sealed record DiagnosticFailure(string Stage, string Reason, string? Hr = null, string? Exception = null)
{
    static readonly Regex ExitCode = new(@"^scskiller_warm exited with code (-?\d+)", RegexOptions.CultureInvariant);
    static readonly Regex TypeName = new(@"^[A-Za-z_][A-Za-z0-9_.]{0,127}$", RegexOptions.CultureInvariant);

    /// <summary>A failed warm's: its error matched against the warmer's own messages; any other is "reported".</summary>
    public static DiagnosticFailure FromWarm(string? error) =>
        error == Warming.Warmer.StuckError ? new("warm", "stuck")
        : error == Warming.Warmer.NoDoneError ? new("warm", "no_done_event")
        : error != null && ExitCode.Match(error) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var code)
            ? new("warm", "exit_code", ((uint)code).ToString("x8"))
            : new("warm", "reported");

    /// <summary>An exception the compile ended with at <paramref name="stage"/>: its HRESULT and its type's full name
    /// (none when the name isn't a plain one, e.g. a generic type's).</summary>
    public static DiagnosticFailure FromException(QueueStage stage, Exception e) =>
        new(stage switch { QueueStage.Indexing => "index", QueueStage.Planning => "plan", QueueStage.Materializing => "materialize", _ => "warm" },
            "exception", ((uint)e.HResult).ToString("x8"), e.GetType().FullName is { } n && TypeName.IsMatch(n) ? n : null);
}

/// <summary>A compile's diagnostics, POST /v1/diagnostics's body as snake_case JSON:
/// driver rejections per pipeline kind and code (<see cref="Rejections"/>) and up to 500 of the rejected record keys,
/// the record keys that crash the driver (<see cref="Crashed"/>), the exception codes the warm caught, and why it
/// failed (<see cref="Failure"/>). Only codes, hashes and fixed values: no message text, no identifier, no times.</summary>
public sealed record DiagnosticsReport(string ContentHash, string? Engine, string Vendor, string? GpuModel, string? DriverVersion, string AppVersion,
    string Outcome, DiagnosticRejection[] Rejections, DiagnosticKey[] Keys, string[] Crashed, string[] Exceptions, DiagnosticFailure? Failure)
{
    public const int MaxKeys = 500, MaxCodes = 64, MaxCrashed = 100, MaxExceptions = 16;

    // the proxy's lines (proxy.cpp note_rejected, note_fail's tally, replay_guarded)
    static readonly Regex Rejected = new(@"warm: rejected '([GCSRA])' hr=0x([0-9a-f]{8}) key ([0-9a-f]{40})", RegexOptions.CultureInvariant);
    static readonly Regex Tally = new(@"warm: failed x(\d+): (?:replay of|plan item on) '([GCSRA])' \([^)]*\) hr=0x([0-9a-f]{8})", RegexOptions.CultureInvariant);
    static readonly Regex Exception = new(@"warm: exception 0x([0-9a-f]{8})", RegexOptions.CultureInvariant);

    /// <summary>The report of a compile from its warm processes' logs (<paramref name="logLines"/>, only the codes and keys
    /// of the lines above are read), its result's crashed keys and, for a failed one, <paramref name="failure"/>; null when
    /// there is nothing to report (a clean compile).</summary>
    public static DiagnosticsReport? Build(IEnumerable<string> logLines, DiagnosticFailure? failure, IEnumerable<string>? crashed,
        string contentHash, string? engine, string vendor, string? gpuModel, string? driverVersion, string appVersion)
    {
        var codes = new Dictionary<(string Kind, string Hr), long>();
        var keys = new Dictionary<string, DiagnosticKey>();
        var exceptions = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var line in logLines)
        {
            if (Tally.Match(line) is { Success: true } t && long.TryParse(t.Groups[1].Value, out var n))
                codes[(t.Groups[2].Value, t.Groups[3].Value)] = codes.GetValueOrDefault((t.Groups[2].Value, t.Groups[3].Value)) + n;
            else if (Rejected.Match(line) is { Success: true } r && keys.Count < MaxKeys)
                keys.TryAdd(r.Groups[3].Value, new(r.Groups[3].Value, r.Groups[1].Value, r.Groups[2].Value));
            else if (Exception.Match(line) is { Success: true } e && exceptions.Count < MaxExceptions)
                exceptions.Add(e.Groups[1].Value);
        }
        if (codes.Count == 0)   // a process that ended before its tally: what its rejection lines say
            foreach (var g in keys.Values.GroupBy(k => (k.Kind, k.Hr))) codes[g.Key] = g.Count();
        string[] removed = [.. (crashed ?? []).Select(k => k.ToLowerInvariant()).Where(k => k.Length == 40 && k.All(char.IsAsciiHexDigit)).Distinct().Order(StringComparer.Ordinal).Take(MaxCrashed)];
        if (failure == null && codes.Count == 0 && keys.Count == 0 && removed.Length == 0 && exceptions.Count == 0) return null;
        return new(contentHash, engine, vendor, gpuModel, driverVersion, appVersion, failure != null ? "failed" : "completed",
            [.. codes.OrderByDescending(c => c.Value).ThenBy(c => c.Key.Kind, StringComparer.Ordinal).ThenBy(c => c.Key.Hr, StringComparer.Ordinal).Take(MaxCodes)
                .Select(c => new DiagnosticRejection(c.Key.Kind, c.Key.Hr, Math.Min(c.Value, 10_000_000)))],
            [.. keys.Values], removed, [.. exceptions], failure);
    }
}

/// <summary>Sends <see cref="DiagnosticsReport"/>s while Settings.ShareDiagnostics is on: no token, no device, no account,
/// and nothing is kept on the PC about what was sent. Quiet: a server without the route (404, 501) or any failure is "not
/// sent", and nothing retries it.</summary>
public sealed class Diagnostics
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    readonly Func<bool> enabled;
    readonly RouteFailover routes;
    readonly HttpClient http;

    /// <param name="enabled">Settings.ShareDiagnostics, read at each call: off means no request at all</param>
    public Diagnostics(Func<bool> enabled, RouteFailover? routes = null)
    {
        this.enabled = enabled;
        this.routes = routes ?? RouteFailover.Default;
        http = new HttpClient(this.routes, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    }

    public static byte[] Body(DiagnosticsReport report) => JsonSerializer.SerializeToUtf8Bytes(report, Json);

    /// <summary>The one-time question at launch, for a user who closed the welcome before it offered this choice; a first
    /// launch answers it in the welcome instead.</summary>
    public static bool Asks(Settings s) => s.WelcomeSeen && !s.DiagnosticsAsked && !s.ShareDiagnostics;

    /// <summary>The welcome's or the question's answer: either answer is final, and Settings can still change it.</summary>
    public static Settings Answered(Settings s, bool share) => s with { DiagnosticsAsked = true, ShareDiagnostics = share };

    /// <summary>True when the server took it.</summary>
    public async Task<bool> SendAsync(DiagnosticsReport report, CancellationToken ct = default)
    {
        if (!enabled()) return false;
        try
        {
            using var content = new ByteArrayContent(Body(report));
            content.Headers.ContentType = new("application/json");
            using var r = await http.PostAsync(new Uri(routes.Primary, "v1/diagnostics"), content, ct);
            return r.StatusCode == HttpStatusCode.NoContent || r.IsSuccessStatusCode;
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return false; }
    }
}
