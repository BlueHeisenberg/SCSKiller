using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NSec.Cryptography;

namespace SCSKiller.Core.App;

/// <summary>An alpha build starts only once a Patreon sign-in brought an access token with "alpha" or "internal", a beta
/// build with "beta", "alpha" or "internal".
/// The token is kept in <see cref="FileName"/> and its PASETO v4.public signature is checked
/// offline against <see cref="TokenKeys"/> at every start. Its exp is never read: a lapsed backer keeps the installed alpha,
/// and a clock set back gains nothing.</summary>
public static class AlphaGate
{
    /// <summary>The origin's token public keys as keys.json lists them: kid -> base64url raw Ed25519 key. Only these count,
    /// never a key fetched or read from the data folder. A new key goes in here before the origin signs with it, and a
    /// retired one stays, so the passes it signed keep verifying. Empty: every alpha build refuses to start.</summary>
    public static readonly IReadOnlyDictionary<string, string> TokenKeys = new Dictionary<string, string>
    {
        ["MHmPVBK8GNuvtgnI"] = "nKrxY1G2e367G5OS8_IhwqmLkj146pr-4zVzwueUy80",
    };

    public const string FileName = "alpha-pass";
    static bool Beta => AppVersion.Current.Channel == UpdateChannels.Beta;
    public static string Title => Beta ? "Beta builds are for Patreon supporters." : "Alpha builds are for Patreon backers.";
    public static string Intro => Beta ? "Sign in with Patreon once. After that this beta runs offline." : "Sign in with Patreon once. After that this alpha runs offline.";
    public const string SignIn = "Sign in with Patreon";
    public const string Waiting = "Finish the sign-in in your browser.";
    public const string NotBacker = "This Patreon account isn't a backer. Alpha builds come with the backer tier.";
    public const string SupporterOnly = "Your tier includes beta builds, not alpha builds.";
    public const string NotSupporter = "This Patreon account isn't a supporter. Beta builds come with the supporter tier.";
    public static string TooOld => Beta ? "This beta is too old to sign in. Download the latest beta." : "This alpha is too old to sign in. Download the latest alpha.";
    public const string Stable = "Get the stable version";
    public const string Quit = "Quit";
    public static string Cli => Beta ? "This beta needs a Patreon supporter sign-in in the SCSKiller app first." : "This alpha needs a Patreon backer sign-in in the SCSKiller app first.";
    public static readonly Uri StableUri = new($"https://github.com/{UpdateFeeds.GhRepo}/releases/latest");

    /// <summary>From the build's version, never a setting: stable and internal builds have no gate.</summary>
    public static bool Needed(AppVersion v) => v.Channel is UpdateChannels.Alpha or UpdateChannels.Beta;

    /// <summary>The kept pass verifies: this alpha may start.</summary>
    public static bool Passed(string dataDir, IReadOnlyDictionary<string, string>? keys = null) => Check(Read(dataDir), keys ?? TokenKeys, Beta) == null;

    /// <summary>Null: <paramref name="token"/> is signed by one of <paramref name="keys"/> and carries alpha or internal (or
    /// beta, for a <paramref name="beta"/> build). Else the refusal's line.</summary>
    public static string? Check(string? token, IReadOnlyDictionary<string, string> keys, bool beta = false)
    {
        if (Payload(token, keys) is not { } payload) return TooOld;
        string[] ent;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            ent = doc.RootElement.GetProperty("ent").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException) { return TooOld; }
        if (ent.Contains(UpdateChannels.Alpha) || ent.Contains(UpdateChannels.Internal)) return null;
        if (beta) return ent.Contains(UpdateChannels.Beta) ? null : NotSupporter;
        return ent.Contains(UpdateChannels.Beta) ? SupporterOnly : NotBacker;
    }

    /// <summary>Opens the gate with the account's token: the browser sign-in (<paramref name="signIn"/>), or at start a refresh
    /// when already signed in. Null: passed, the pass kept. Else the line to show, empty when there's nothing to say (signed
    /// out, a cancelled sign-in).</summary>
    public static async Task<string?> OpenAsync(Account account, string dataDir, bool signIn, IReadOnlyDictionary<string, string>? keys = null)
    {
        keys ??= TokenKeys;
        if (Check(Read(dataDir), keys, Beta) == null) return null;
        if (keys.Count == 0) return TooOld;   // no token could open it: don't send anyone to Patreon
        if (signIn)
        {
            if (!await account.SignInAsync()) return account.Problem ?? "";
        }
        else if (!account.SignedIn) return "";
        else await account.RefreshAsync();
        if (account.AccessToken is not { } token) return account.Problem ?? "";
        var why = Check(token, keys, Beta);
        if (why == null)
        {
            // the token is a bearer credential for a day: DPAPI as auth.dat on Windows (a portable folder's ACL may be open), 0600 elsewhere
            var bytes = Encoding.UTF8.GetBytes(token);
            try { AppStore.WriteAtomic(Path.Combine(dataDir, FileName), OperatingSystem.IsWindows() ? Dpapi.Protect(bytes) : bytes, ownerOnly: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { }   // this start opens all the same, the next one signs in again
        }
        return why;
    }

    const int MaxPass = 16 << 10;

    static string? Read(string dataDir)
    {
        var file = Path.Combine(dataDir, FileName);
        try
        {
            // a FIFO or device never ends: only a regular file, and only its first bytes
            using var s = File.Exists(file) ? Games.GameFiles.OpenRegular(file) : null;
            if (s == null) return null;
            var buffer = new byte[MaxPass + 1];
            var n = s.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (n > MaxPass) return null;
            return Encoding.UTF8.GetString(OperatingSystem.IsWindows() ? Dpapi.Unprotect(buffer[..n]) : buffer[..n]).Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { return null; }
    }

    static readonly SignatureAlgorithm Ed = SignatureAlgorithm.Ed25519;
    static readonly byte[] Header = "v4.public."u8.ToArray();

    /// <summary>The payload of a v4.public token whose footer's kid is one of <paramref name="keys"/> and whose signature
    /// verifies with it (no implicit assertion, as the origin mints them); else null.</summary>
    internal static byte[]? Payload(string? token, IReadOnlyDictionary<string, string> keys)
    {
        if (token == null || !token.StartsWith("v4.public.", StringComparison.Ordinal)) return null;
        var parts = token["v4.public.".Length..].Split('.');
        if (parts.Length is not (1 or 2)) return null;
        try
        {
            var body = Base64Url.DecodeFromChars(parts[0]);
            var footer = parts.Length == 2 ? Base64Url.DecodeFromChars(parts[1]) : [];
            if (body.Length < 64) return null;
            using var doc = JsonDocument.Parse(footer);
            var kid = doc.RootElement.GetProperty("kid").GetString();
            if (kid == null || !keys.TryGetValue(kid, out var pub)
                || !PublicKey.TryImport(Ed, Base64Url.DecodeFromChars(pub), KeyBlobFormat.RawPublicKey, out var key)) return null;
            var payload = body[..^64];
            return Ed.Verify(key!, Pae(Header, payload, footer, []), body.AsSpan(^64)) ? payload : null;
        }
        catch (Exception e) when (e is FormatException or JsonException or KeyNotFoundException or InvalidOperationException) { return null; }
    }

    /// <summary>PASETO's pre-authentication encoding: LE64(count), then LE64(length) and the bytes of each piece.</summary>
    internal static byte[] Pae(params byte[][] pieces)
    {
        var pae = new byte[8 + pieces.Sum(p => 8 + p.Length)];
        BinaryPrimitives.WriteUInt64LittleEndian(pae, (ulong)pieces.Length);
        var at = 8;
        foreach (var p in pieces)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(pae.AsSpan(at), (ulong)p.Length);
            p.CopyTo(pae, at + 8);
            at += 8 + p.Length;
        }
        return pae;
    }
}
