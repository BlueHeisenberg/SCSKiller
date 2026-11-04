using System.Net;
using System.Net.Http.Json;
using System.Web;
using SCSKiller.Core.App;

namespace SCSKiller.App.Design;

/// <summary>--screenshots: an <see cref="Account"/> against an in-process fake server, whose "browser" approves at once
/// through the real loopback flow, so the Settings shots can sign in (with or without "db") and out. No network.</summary>
static class FakeAccount
{
    /// <summary>The entitlements the next token carries.</summary>
    public static string[] Ent = ["db", "beta"];
    /// <summary>/v1/me's reason on the patreon row.</summary>
    public static string Reason = "active";

    public static Account Create(string dir) => new(dir, new RouteFailover(new Server(), [new Uri("https://api.fake.invalid/")]), Approve);

    static void Approve(Uri start)
    {
        var q = HttpUtility.ParseQueryString(start.Query);
        _ = Task.Run(async () =>
        {
            using var http = new HttpClient();
            (await http.GetAsync($"http://127.0.0.1:{q["port"]}/cb?code=fake&state={q["state"]}")).Dispose();
        });
    }

    sealed class Server : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(
            r.RequestUri!.AbsolutePath == "/v1/me"
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { entitlements = new[] { new { source = "patreon", reason = Reason } } }) }
                : r.RequestUri!.AbsolutePath is "/v1/auth/exchange" or "/v1/token"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new { device_token = "sd1_fake", access_token = "v4.public.fake", exp = DateTimeOffset.UtcNow.AddDays(1), ent = Ent, until = (string?)null }),
                }
                : new HttpResponseMessage(HttpStatusCode.NoContent));   // healthz, logout
    }
}
