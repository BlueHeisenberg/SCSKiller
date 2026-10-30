using System.Buffers.Text;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SCSKiller.Core.App;

/// <summary>The supporter's entitlements from the last token: <c>ent</c> flags ("db", "beta",
/// "alpha", "prio", "internal"; the UI ignores others) and <c>until</c>, when they end, grace included (null: no end date).</summary>
public sealed record AccountStatus(IReadOnlyList<string> Ent, DateTimeOffset? Until)
{
    static readonly Dictionary<string, string> Names = new() { ["db"] = "community shader hash database", ["beta"] = "beta builds", ["alpha"] = "alpha builds", ["prio"] = "priority requests" };

    /// <summary>The Account card's line, e.g. "Supporter: beta builds and alpha builds."; null when no flag is shown.</summary>
    public string? Benefits => Ent.Where(Names.ContainsKey).Select(k => Names[k]).ToList() is { Count: > 0 } names
        ? "Supporter: " + (names.Count > 1 ? string.Join(", ", names[..^1]) + " and " + names[^1] : names[0])
            + (Until is { } until ? $", until {until.ToLocalTime():d MMM yyyy}." : ".")
        : null;
}

/// <summary>A failure worded for the user.</summary>
public sealed class AccountException(string message) : Exception(message)
{
    /// <summary>The system keyring kept the device token from the request (locked, unreadable or gone).</summary>
    public bool Keyring { get; init; }
}

/// <summary>Sign in with Patreon. The device token lives in auth.dat (<see cref="TokenFile{T}"/>: DPAPI on Windows, the
/// system keyring or a file only this user can read on Linux); the access token only in memory. Every method reports
/// failures through <see cref="Problem"/>, never by throwing, so the UI works with the server unreachable.</summary>
public sealed class Account
{
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    const string Unreachable = "Can't reach the SCSKiller server right now. Check your internet connection, or try again later.";
    const string NotRevoked = "Signed out on this PC. The server couldn't remove it from your account's devices. It drops off there after 90 days unused.";
    const string KeptInFile = "The system keyring is locked or not running, so your sign-in is kept in a file only your user can read.";
    const string KeyringLocked = "Your sign-in is in the system keyring, which is locked or not running. Unlock it, then click Refresh status.";
    const string KeyringUnreadable = "Your sign-in in the system keyring can't be read. Sign out, then sign in again.";

    readonly TokenFile<Stored> file;
    readonly RouteFailover routes;
    readonly HttpClient http;
    readonly Action<Uri> openBrowser;
    readonly TimeProvider clock;
    TokenFile<Stored>.Kept? kept;   // what auth.dat holds: the device token, or where the keyring keeps it
    string? accessToken;
    long accessAt;           // clock.GetTimestamp() when the access token arrived
    TimeSpan accessLife;
    CancellationTokenSource? signIn;

    /// <param name="keyring">Linux: the system keyring, tried first. Null: the file only (the CLI, which never signs in or
    /// reads the token).</param>
    public Account(string dataDir, RouteFailover? routes = null, Action<Uri>? openBrowser = null, TimeProvider? clock = null,
        IKeyring? keyring = null, IProgress<string>? log = null)
    {
        file = new(Path.Combine(dataDir, "auth.dat"), "SCSKiller sign-in", keyring, log);
        this.routes = routes ?? RouteFailover.Default;
        http = new HttpClient(this.routes, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        this.openBrowser = openBrowser ?? (u => Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true })?.Dispose());
        this.clock = clock ?? TimeProvider.System;
        kept = file.Peek(out var refused);   // another user's or a damaged file: signed out
        if (refused != null) Problem = $"Your saved sign-in wasn't used because {refused}. Sign in again.";
    }

    /// <summary>Raised after any change of the properties below, on whatever thread made it.</summary>
    public event Action? Changed;
    /// <summary>From auth.dat alone: also while the keyring that holds the token is locked.</summary>
    public bool SignedIn => kept != null;
    /// <summary>At start: deletes the keyring items an earlier sign-out couldn't reach (Linux). Never throws.</summary>
    public Task SweepAsync() => file.SweepAsync();
    public bool SigningIn => signIn != null;
    public bool Busy { get; private set; }
    /// <summary>Null until a token has been received in this session.</summary>
    public AccountStatus? Status { get; private set; }
    /// <summary>The access token received last, without a refresh; null when none came in this session.</summary>
    internal string? AccessToken => accessToken;
    /// <summary>The last token carries "db" (the community database); false when signed out or not read yet.</summary>
    public bool HasDb => Status?.Ent.Contains("db") == true;
    /// <summary>The last failure, or a sign-in's caveat (kept in a file, not the keyring), in plain words; null after a
    /// plain success.</summary>
    public string? Problem { get; private set; }
    /// <summary>Signed in by <see cref="SignInAsync"/> in this session (not read back from auth.dat at start).</summary>
    public bool JustSignedIn { get; private set; }

    /// <summary>Settings' Account card asks once, right after a sign-in, to share recordings: only while sharing is off
    /// and the user hasn't said "Not now".</summary>
    public bool OffersSharing(Settings s) => JustSignedIn && !s.ShareRecordings && !s.SharePromptDismissed;

    /// <summary>The one entry point for "Sign in with Patreon" (Settings, the welcome dialog): opens the browser and waits
    /// for it, up to <see cref="SignInTimeout"/>. Another call, or <see cref="CancelSignIn"/>, cancels a running one.
    /// False when it was cancelled or failed (<see cref="Problem"/> says why).</summary>
    public async Task<bool> SignInAsync(CancellationToken ct = default)
    {
        CancelSignIn();
        var mine = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var timeout = new CancellationTokenSource(SignInTimeout);
        using var any = CancellationTokenSource.CreateLinkedTokenSource(mine.Token, timeout.Token);
        (signIn, Busy, Problem) = (mine, true, null);
        Raise();
        try
        {
            using var loopback = new LoopbackCallback();
            var verifier = Pkce.Random(32);
            var state = Pkce.Random(16);
            using (var health = await http.GetAsync(new Uri(routes.Primary, "healthz"), any.Token))   // settles which route works
                if ((int)health.StatusCode >= 500) await AnswerAsync(health, any.Token);                 // 503: the backend is down, no browser
            openBrowser(new Uri(routes.Current, $"v1/auth/start?provider=patreon&port={loopback.Port}&challenge={Pkce.Challenge(verifier)}&state={state}"));
            var code = await loopback.WaitAsync(state, any.Token);
            using var r = await SendAsync(HttpMethod.Post, "v1/auth/exchange", null, any.Token, new { code, verifier, label = DeviceLabel() });
            if (r.StatusCode == HttpStatusCode.BadRequest)   // invalid_grant: the one-time code expired (60 s) or was used
                throw new AccountException("The sign-in expired or was already used. Please try again.");
            var json = await AnswerAsync(r, any.Token);
            // committed only while this sign-in is the running one: another cancels it first, however long the keyring takes
            await file.SaveAsync(new Stored(json.GetProperty("device_token").GetString()!), any.Token, (saved, inFile) =>
            {
                kept = saved;
                SetAccess(r, json);
                JustSignedIn = true;
                if (inFile) Problem = KeptInFile;
            });
            return true;
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !mine.IsCancellationRequested)
        {
            Problem = $"Sign-in timed out after {SignInTimeout.TotalMinutes:0} minutes. If the Patreon page didn't load, Patreon may be " +
                      "unreachable from your network right now. If a football match is on, try again afterwards.";
        }
        catch (OperationCanceledException) when (mine.IsCancellationRequested) { }
        catch (Exception e) { Problem = Plain(e); }
        finally
        {
            if (signIn == mine) (signIn, Busy) = (null, false);
            Raise();
        }
        return false;
    }

    public void CancelSignIn()
    {
        try { signIn?.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>"Refresh status": a new access token, which carries the current entitlements.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        if (kept == null) return;
        (Busy, Problem) = (true, null);
        Raise();
        try { await NewAccessTokenAsync(ct); }
        catch (Exception e) when (!ct.IsCancellationRequested) { Problem = Plain(e); }
        finally
        {
            Busy = false;
            Raise();
        }
    }

    /// <summary>The access token for the edge (community DB, beta feed), renewed once half its life is gone (12 h of a
    /// 24 h token, §2.2). A failed renewal keeps a token that hasn't expired. Null when signed out.</summary>
    public async Task<string?> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (kept == null) return null;
        if (!NeedsRefresh) return accessToken;
        try { await NewAccessTokenAsync(ct); }
        catch (Exception e) when (e is HttpRequestException or AccountException && accessToken != null && AccessAge < accessLife) { }
        return accessToken;
    }

    /// <summary>The access token when it carries "db" (the community database), else null: signed out, not a supporter, or
    /// no token to be had. <paramref name="fresh"/>: a new one even if the current one is young (the edge answered 401).
    /// Throws like <see cref="GetAccessTokenAsync"/>.</summary>
    public Task<string?> GetDbTokenAsync(bool fresh = false, CancellationToken ct = default) => GetTokenAsync("db", fresh, ct);

    /// <summary>The access token when it carries <paramref name="flag"/>, else null; as <see cref="GetDbTokenAsync"/>.</summary>
    public async Task<string?> GetTokenAsync(string flag, bool fresh = false, CancellationToken ct = default)
    {
        if (fresh && kept != null) await NewAccessTokenAsync(ct);
        var token = await GetAccessTokenAsync(ct);
        return Status?.Ent.Contains(flag) == true ? token : null;
    }

    // Timed on this PC's monotonic clock from when the token arrived, never by comparing the server's exp with the local
    // clock: a PC clock hours off neither refreshes early nor keeps an expired token.
    TimeSpan AccessAge => clock.GetElapsedTime(accessAt);
    public bool NeedsRefresh => accessToken == null || AccessAge >= accessLife / 2;

    /// <summary>Revokes this PC at the server (POST /v1/auth/logout) and forgets it here, even when the server can't be reached.</summary>
    public async Task SignOutAsync(CancellationToken ct = default)
    {
        if (kept is not { } signedIn) return;
        (Busy, Problem) = (true, null);
        Raise();
        try
        {
            string? token = null;
            try { token = (await file.ReadAsync(signedIn, ct))?.Member; }
            catch (Exception e) when (e is KeyringUnavailableException or KeyringItemUnreadableException or OperationCanceledException) { }
            await ForgetAsync(signedIn);
            if (token == null) { Problem = NotRevoked; return; }   // a keyring that can't give it: signed out here all the same
            using var r = await SendAsync(HttpMethod.Post, "v1/auth/logout", token, ct);
            if ((int)r.StatusCode >= 500) Problem = NotRevoked;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException) { Problem = NotRevoked; }
        catch (Exception e) { Problem = Plain(e); }
        finally
        {
            Busy = false;
            Raise();
        }
    }

    async Task NewAccessTokenAsync(CancellationToken ct)
    {
        if (await DeviceTokenAsync(ct) is not { } token) return;
        using var r = await SendAsync(HttpMethod.Post, "v1/token", token, ct);
        if (kept is not { } k || k.Value?.Member != token) return;   // signed out, or in as another device, meanwhile: the answer is about a device no longer used
        if (r.StatusCode == HttpStatusCode.Unauthorized)   // revoked: removed from the account, 6th device, or 90 days unused (§2.4, §2.5)
        {
            await ForgetAsync(k);
            throw new AccountException("This PC was signed out of your Patreon account (removed from its devices, or unused for 90 days). Sign in again.");
        }
        SetAccess(r, await AnswerAsync(r, ct));
    }

    // {access_token, exp, ent, until}: the answer of /v1/auth/exchange and /v1/token alike. The lifetime is exp minus the
    // server's own time (the response's Date header), so both ends of it come from the server's clock.
    void SetAccess(HttpResponseMessage r, JsonElement j)
    {
        accessToken = j.GetProperty("access_token").GetString();
        accessAt = clock.GetTimestamp();
        accessLife = j.GetProperty("exp").GetDateTimeOffset() - (r.Headers.Date ?? clock.GetUtcNow());
        Status = new(j.GetProperty("ent").EnumerateArray().Select(e => e.GetString()!).ToArray(),
            j.GetProperty("until") is { ValueKind: JsonValueKind.String } u ? u.GetDateTimeOffset() : null);
    }

    Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? bearer, CancellationToken ct, object? json = null)
    {
        var request = new HttpRequestMessage(method, new Uri(routes.Primary, path)) { Content = json == null ? null : JsonContent.Create(json) };
        if (bearer != null) request.Headers.Authorization = new("Bearer", bearer);
        return http.SendAsync(request, ct);
    }

    static async Task<JsonElement> AnswerAsync(HttpResponseMessage r, CancellationToken ct)
    {
        if (!r.IsSuccessStatusCode) throw new AccountException(r.StatusCode switch
        {
            HttpStatusCode.TooManyRequests => "Too many attempts from your network. Wait a while and try again.",
            >= HttpStatusCode.InternalServerError => $"The SCSKiller server had a problem (error {(int)r.StatusCode}). Try again in a few minutes.",
            _ => $"The SCSKiller server refused the request (error {(int)r.StatusCode}). If it keeps happening, sign out and in again.",
        });
        return await r.Content.ReadFromJsonAsync<JsonElement>(ct);
    }

    static string Plain(Exception e) => e switch
    {
        AccountException => e.Message,
        HttpRequestException => Unreachable,
        OperationCanceledException => "The SCSKiller server didn't answer in time. Try again later.",
        JsonException or KeyNotFoundException or InvalidOperationException or FormatException =>
            "The SCSKiller server gave an answer this version doesn't understand. An update of SCSKiller may fix it.",
        Win32Exception => "Couldn't open your web browser: " + e.Message,
        _ => e.Message,
    };

    internal static string DeviceLabel() => ThisDevice.Kind + " · " + RandomNumberGenerator.GetString("ABCDEFGHJKLMNPQRSTUVWXYZ23456789", 4);   // §2.5: no machine name

    void Raise() => Changed?.Invoke();

    // auth.dat: {"Member":"sd1_..."} (TokenFile); the app and the CLI share it.
    sealed record Stored(string Member);

    /// <summary>The device token, read from the keyring the first time it's needed. Throws an <see cref="AccountException"/>
    /// while the keyring can't give it, and signs out when the keyring no longer has it.</summary>
    async Task<string?> DeviceTokenAsync(CancellationToken ct)
    {
        if (kept is not { } k) return null;
        if (k.Value is { } v) return v.Member;
        Stored? read;
        try { read = await file.ReadAsync(k, ct); }
        catch (KeyringUnavailableException) { throw new AccountException(KeyringLocked) { Keyring = true }; }
        catch (KeyringItemUnreadableException) { throw new AccountException(KeyringUnreadable) { Keyring = true }; }   // kept: signing out removes it
        if (kept != k) return kept?.Value?.Member;   // signed out or in again meanwhile
        if (read == null)
        {
            await ForgetAsync(k);
            throw new AccountException("Your sign-in is no longer in the system keyring. Sign in again.") { Keyring = true };
        }
        kept = k with { Value = read };
        return read.Member;
    }

    // only the sign-in it was about: one that landed meanwhile, while the keyring answered, stays
    async Task ForgetAsync(TokenFile<Stored>.Kept which)
    {
        if (kept is { } now && TokenFile<Stored>.Same(now, which)) (kept, accessToken, Status, JustSignedIn) = (null, null, null, false);
        await file.DeleteAsync(which);
    }
}

/// <summary>PKCE S256 (RFC 7636) and the random strings sign-in needs, base64url without padding.</summary>
public static class Pkce
{
    public static string Random(int bytes) => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(bytes));
    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}

/// <summary>The app's end of the sign-in redirect (RFC 8252): http://127.0.0.1:&lt;random port&gt;/cb?code&amp;state.</summary>
public sealed class LoopbackCallback : IDisposable
{
    readonly HttpListener listener;
    public int Port { get; }

    public LoopbackCallback()
    {
        for (var tries = 0; ; tries++)
        {
            Port = Random.Shared.Next(49152, 65536);
            listener = new HttpListener { Prefixes = { $"http://127.0.0.1:{Port}/" } };
            try { listener.Start(); return; }
            catch (HttpListenerException) when (tries < 20) { listener.Close(); }   // port taken
        }
    }

    /// <summary>The one-time code from the /cb request. A /cb with another state, an error or no code ends the sign-in
    /// (the browser gets a page saying so); other paths get a 404.</summary>
    public async Task<string> WaitAsync(string state, CancellationToken ct)
    {
        while (true)
        {
            var c = await listener.GetContextAsync().WaitAsync(ct);
            if (c.Request.Url?.AbsolutePath != "/cb") { Reply(c, 404, "Not found."); continue; }
            var q = c.Request.QueryString;
            if (q["state"] != state)
            {
                Reply(c, 400, "This page doesn't belong to the sign-in SCSKiller is waiting for. Start again from SCSKiller.");
                throw new AccountException("The browser's answer didn't match this sign-in, so it was refused. Please try again.");
            }
            if (q["error"] != null || q["code"] is not { Length: > 0 } code)
            {
                Reply(c, 400, "Sign-in didn't complete. You can close this tab and try again from SCSKiller.");
                throw new AccountException(q["error"] == "access_denied" ? "Sign-in was cancelled on Patreon." : $"Sign-in didn't complete ({q["error"] ?? "no code"}). Please try again.");
            }
            Reply(c, 200, "You're signed in. You can close this tab and go back to SCSKiller.");
            return code;
        }
    }

    static void Reply(HttpListenerContext c, int status, string text)
    {
        var html = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>SCSKiller</title>" +
            $"<body style=\"font:16px 'Segoe UI',sans-serif;margin:3em\"><h2>SCSKiller</h2><p>{WebUtility.HtmlEncode(text)}</p>");
        c.Response.StatusCode = status;
        c.Response.ContentType = "text/html; charset=utf-8";
        c.Response.KeepAlive = false;   // sent whole (Content-Length) and closed gracefully: Dispose right after can't cut the page off
        c.Response.Close(html, willBlock: true);
    }

    public void Dispose() => listener.Close();
}

/// <summary>DPAPI, CurrentUser scope (no CRYPTPROTECT_LOCAL_MACHINE), with SCSKiller's entropy. P/Invoke rather than the
/// ProtectedData package, which isn't part of the plain .NET runtime.</summary>
public static class Dpapi
{
    static readonly byte[] Entropy = "SCSKiller.auth.v1"u8.ToArray();
    const int UiForbidden = 1;

    public static byte[] Protect(byte[] data) => Run(data, protect: true);
    public static byte[] Unprotect(byte[] data) => Run(data, protect: false);

    /// <summary>A protected JSON file; null when it is missing, another user's or damaged.</summary>
    public static T? Load<T>(string file) where T : class
    {
        try { return File.Exists(file) ? JsonSerializer.Deserialize<T>(Unprotect(File.ReadAllBytes(file))) : null; }
        catch (Exception e) when (e is CryptographicException or JsonException or IOException) { return null; }
    }

    public static void Save<T>(string file, T value) => AppStore.WriteAtomic(file, Protect(JsonSerializer.SerializeToUtf8Bytes(value)));

    static unsafe byte[] Run(byte[] data, bool protect)
    {
        fixed (byte* d = data, e = Entropy)
        {
            Blob input = new(data.Length, (nint)d), entropy = new(Entropy.Length, (nint)e), output;
            var ok = protect ? CryptProtectData(in input, null, in entropy, 0, 0, UiForbidden, out output)
                             : CryptUnprotectData(in input, 0, in entropy, 0, 0, UiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            try { return new ReadOnlySpan<byte>((void*)output.Data, output.Size).ToArray(); }
            finally { LocalFree(output.Data); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    readonly struct Blob(int size, nint data) { public readonly int Size = size; public readonly nint Data = data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(in Blob data, string? description, in Blob entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(in Blob data, nint description, in Blob entropy, nint reserved, nint prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")]
    static extern nint LocalFree(nint mem);
}
