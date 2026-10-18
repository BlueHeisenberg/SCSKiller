using System.Globalization;
using System.Text;

namespace SCSKiller.Core.App;

/// <summary>The anonymous daily check: at most one POST /v1/active per UTC day,
/// with no identifier, token or account. Its three flags come from the date of the last check the server accepted
/// (active-check.txt), so the server counts installs per day, week and month without telling them apart. Internal and
/// dev builds send nothing. Quiet: a failure waits an hour, and the flags stay as they were.</summary>
public sealed class ActiveCheck
{
    readonly string file;
    readonly Func<bool> enabled;
    readonly AppVersion version;
    readonly string gpu;
    readonly RouteFailover routes;
    readonly HttpClient http;
    readonly TimeProvider clock;
    readonly SemaphoreSlim sending = new(1, 1);   // scans overlap: one check, not two
    DateTimeOffset retryAt;

    /// <param name="enabled">Settings.ActiveCheck, read at each call: off means no request at all</param>
    public ActiveCheck(string dataDir, Func<bool> enabled, GpuVendor vendor, AppVersion? version = null, RouteFailover? routes = null, TimeProvider? clock = null)
    {
        file = Path.Combine(dataDir, "active-check.txt");
        this.enabled = enabled;
        this.version = version ?? AppVersion.Current;
        gpu = Gpu(vendor);
        this.routes = routes ?? RouteFailover.Default;
        this.clock = clock ?? TimeProvider.System;
        http = new HttpClient(this.routes, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(10) };
    }

    /// <summary>Settings that let it be sent: turned on by a choice the user made (<see cref="Settings.ActiveCheckAsked"/>).</summary>
    public static bool Allowed(Settings s) => s is { ActiveCheck: true, ActiveCheckAsked: true, WelcomeSeen: true };

    /// <summary>An install from before the choice existed: asked once, after the welcome, and off until it answers.</summary>
    public static bool Asks(Settings s) => s.WelcomeSeen && !s.ActiveCheckAsked;

    /// <summary>The choice, from the welcome's checkbox, the one-time question (Esc or closing = off) or Settings.</summary>
    public static Settings Answered(Settings s, bool on) => s with { ActiveCheck = on, ActiveCheckAsked = true };

    /// <summary>The body's "gpu": the server takes only these three, so Intel is "other".</summary>
    public static string Gpu(GpuVendor vendor) => vendor switch { GpuVendor.Nvidia => "nvidia", GpuVendor.Amd => "amd", _ => "other" };

    /// <summary>The whole request body. <paramref name="last"/>: the UTC day of the last accepted check, null = never.</summary>
    public static string Body(DateOnly? last, DateOnly today, AppVersion version, string gpu)
    {
        static (int, int) Week(DateOnly d) => (ISOWeek.GetYear(d.ToDateTime(default)), ISOWeek.GetWeekOfYear(d.ToDateTime(default)));
        static string B(bool b) => b ? "true" : "false";
        return $$"""{"first":{{B(last is null)}},"week":{{B(last is not { } w || Week(w) != Week(today))}},"month":{{B(last is not { } m || (m.Year, m.Month) != (today.Year, today.Month))}},"v":"{{version}}","gpu":"{{gpu}}"}""";
    }

    public async Task SendAsync(CancellationToken ct = default)
    {
        if (!enabled() || version.Channel == UpdateChannels.Internal || clock.GetUtcNow() < retryAt) return;
        await sending.WaitAsync(ct);
        try
        {
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var last = Last();
            if (!enabled() || last >= today || clock.GetUtcNow() < retryAt) return;   // turned off while another send held it
            retryAt = clock.GetUtcNow() + TimeSpan.FromHours(1);
            using var body = new StringContent(Body(last, today, version, gpu), Encoding.UTF8, "application/json");
            using var r = await http.PostAsync(new Uri(routes.Primary, "v1/active"), body, ct);
            if (!r.IsSuccessStatusCode) return;
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        finally { sending.Release(); }
    }

    // A file that exists but can't be read throws: no check then, rather than a second "first".
    DateOnly? Last() => File.Exists(file)
        && DateOnly.TryParseExact(File.ReadAllText(file).Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}
