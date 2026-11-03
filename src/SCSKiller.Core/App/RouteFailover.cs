using System.Net;

namespace SCSKiller.Core.App;

/// <summary>Client failover between the API's two routes: requests are written against
/// <see cref="Primary"/> (api.scskiller.com, through Cloudflare) and go to api.scskiller.io (the VPS) when the primary
/// can't be reached: DNS, connect, TLS or timeout, or a 5xx that isn't ours (no X-SCSK header: Cloudflare's 52x/530).
/// 4xx and our own 5xx are answers and never fail over. A POST fails over only when it can't have been sent.
/// After a failover the fallback stays first for <see cref="Sticky"/>, then a probe decides. Requests to other hosts pass through.
/// SCSKILLER_API overrides the routes (comma-separated, e.g. http://localhost:5080) for development.</summary>
public sealed class RouteFailover : DelegatingHandler
{
    public static readonly Uri[] DefaultRoutes = [new("https://api.scskiller.com/"), new("https://api.scskiller.io/")];
    public static readonly TimeSpan Sticky = TimeSpan.FromMinutes(30);
    /// <summary>Process-wide, so every client shares which route works (sign-in, community DB, beta updates).</summary>
    public static readonly RouteFailover Default = new();

    /// <summary>The User-Agent's product; the CLI sets SCSKiller-CLI before its first request.</summary>
    public static string Product { get; set; } = "SCSKiller";

    /// <summary>The User-Agent of every request through this handler, any host. Names the build, nothing that identifies the user or the PC.</summary>
    public static string UserAgent(string product, AppVersion v, int windowsBuild) => $"{product}/{v} ({v.Channel}; Windows {windowsBuild})";

    public IReadOnlyList<Uri> Routes { get; }
    public Uri Primary => Routes[0];
    readonly TimeProvider clock;
    readonly TimeSpan headersTimeout;
    readonly Lock gate = new();
    int current;               // index of the route that answered last
    DateTimeOffset stickyUntil;

    public RouteFailover(HttpMessageHandler? inner = null, IReadOnlyList<Uri>? routes = null, TimeProvider? clock = null, TimeSpan? headersTimeout = null)
        : base(inner ?? new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3), PooledConnectionLifetime = TimeSpan.FromMinutes(5) })   // lifetime: DNS changes reach a tray process
    {
        Routes = routes ?? FromEnvironment() ?? DefaultRoutes;
        this.clock = clock ?? TimeProvider.System;
        this.headersTimeout = headersTimeout ?? TimeSpan.FromSeconds(15);
    }

    static Uri[]? FromEnvironment() => Environment.GetEnvironmentVariable("SCSKILLER_API") is { Length: > 0 } v
        ? v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(h => new Uri(h.TrimEnd('/') + "/")).ToArray()
        : null;

    /// <summary>The route requests try first right now (the browser's sign-in page opens on it too).</summary>
    public Uri Current => Routes[First()];

    /// <summary>Caps each route's attempt of one request (default: 15 s to the headers), for a caller with a short total
    /// budget: a blackholed primary then still leaves the fallback time.</summary>
    public static readonly HttpRequestOptionsKey<TimeSpan> AttemptTimeout = new("SCSKiller.AttemptTimeout");

    /// <summary>Asked before each route's attempt: false withdraws the request (<see cref="RequestWithdrawnException"/>).</summary>
    public static readonly HttpRequestOptionsKey<Func<bool>> StillWanted = new("SCSKiller.StillWanted");

    /// <summary>The running return probe, if any (tests await it).</summary>
    public Task Probe { get; private set; } = Task.CompletedTask;

    // Once the stickiness expires, the first request after it starts a background GET <primary>/healthz (3 s, must carry
    // X-SCSK) and still goes to the fallback: the probe's success makes the primary first again, its failure keeps the
    // fallback for another Sticky. So a blocked primary never costs a request its connect timeout.
    int First()
    {
        lock (gate)
        {
            if (current != 0 && clock.GetUtcNow() >= stickyUntil && Probe.IsCompleted)
            {
                stickyUntil = DateTimeOffset.MaxValue;   // one probe at a time
                Probe = Task.Run(ProbeAsync);
            }
            return current;
        }
    }

    async Task ProbeAsync()
    {
        bool ok;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Primary, "healthz"));
            using var response = await SendWithAgent(request, timeout.Token).ConfigureAwait(false);
            ok = response.Headers.Contains("X-SCSK");
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { ok = false; }
        lock (gate) (current, stickyUntil) = ok ? (0, default) : (current, clock.GetUtcNow() + Sticky);
    }

    /// <summary>GET of a small file (a content file; with <paramref name="bearer"/>, a channel's file behind the access token)
    /// within <paramref name="budget"/> in total; each route gets half, so a blackholed Cloudflare still leaves .io time. The
    /// body of a success, else null (an error status is an answer: no fallback); never throws.</summary>
    public async Task<string?> GetContentAsync(string path, TimeSpan budget, int maxBytes, string? bearer = null) =>
        (await GetContentStatusAsync(path, budget, maxBytes, bearer)).Body;

    /// <summary><see cref="GetContentAsync"/> with the status of the answer; null when none came.</summary>
    public async Task<(string? Body, HttpStatusCode? Status)> GetContentStatusAsync(string path, TimeSpan budget, int maxBytes, string? bearer = null)
    {
        using var http = new HttpClient(this, disposeHandler: false) { MaxResponseContentBufferSize = maxBytes };
        using var cts = new CancellationTokenSource(budget, clock);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(Primary, path));
        if (bearer != null) request.Headers.Authorization = new("Bearer", bearer);
        request.Options.Set(AttemptTimeout, budget / 2);
        HttpStatusCode? status = null;
        try
        {
            using var r = await http.SendAsync(request, cts.Token);
            status = r.StatusCode;
            return (r.IsSuccessStatusCode ? await r.Content.ReadAsStringAsync(cts.Token) : null, status);
        }
        catch (Exception) { return (null, status); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (Uri.Compare(uri, Primary, UriComponents.SchemeAndServer, UriFormat.Unescaped, StringComparison.OrdinalIgnoreCase) != 0)
            return await SendWithAgent(request, ct).ConfigureAwait(false);
        var path = uri.PathAndQuery.TrimStart('/');
        var idempotent = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        var first = First();
        Exception? last = null;
        for (var i = 0; i < Routes.Count; i++)
        {
            if (request.Options.TryGetValue(StillWanted, out var wanted) && !wanted()) throw new RequestWithdrawnException();
            var at = (first + i) % Routes.Count;
            request.RequestUri = new Uri(Routes[at], path);
            bool sent;   // whether the failure can have happened after the request went out
            using var timeout = new CancellationTokenSource(request.Options.TryGetValue(AttemptTimeout, out var cap) ? cap : headersTimeout, clock);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            try
            {
                var response = await SendWithAgent(request, attempt.Token).ConfigureAwait(false);
                if ((int)response.StatusCode >= 500 && !response.Headers.Contains("X-SCSK"))   // the route failed, not our server
                {
                    if (!idempotent) return response;
                    last = new HttpRequestException($"{Routes[at].Host}: {(int)response.StatusCode} from the route", null, response.StatusCode);
                    response.Dispose();
                    continue;
                }
                // only a request that failed over moves the routes, and only from where it started: one that began on the
                // fallback before a probe put the primary back must not undo the probe's verdict
                lock (gate) if (at != first && current == first) (current, stickyUntil) = (at, clock.GetUtcNow() + Sticky);
                return response;
            }
            catch (HttpRequestException e) when (!ct.IsCancellationRequested)
            {
                last = e;
                sent = e.HttpRequestError is not (HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError
                    or HttpRequestError.SecureConnectionError or HttpRequestError.ProxyTunnelError);
            }
            catch (OperationCanceledException e) when (!ct.IsCancellationRequested)
            {
                last = e;
                sent = attempt.IsCancellationRequested;   // our headers timeout; otherwise the handler's ConnectTimeout
            }
            if (sent && !idempotent) break;
        }
        throw new ApiUnreachableException(last);
    }

    Task<HttpResponseMessage> SendWithAgent(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Headers.UserAgent.Count == 0) request.Headers.UserAgent.ParseAdd(UserAgent(Product, AppVersion.Current, Environment.OSVersion.Version.Build));
        return base.SendAsync(request, ct);
    }
}

public sealed class ApiUnreachableException(Exception? inner) : HttpRequestException("Can't reach the SCSKiller server.", inner);

public sealed class RequestWithdrawnException() : Exception("The request was withdrawn before it was sent.");   // not an HttpRequestException: HttpClient wraps those once its timeout fired
