using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCSKiller.Core.App;

// The offline half of the signed update feed and of signed content files (plan-rules.json, news.json, game-verdicts.json).
// Same code as the client's checks (FeedTrust, ContentTrust).
//   keygen <key-file> [--for rules]                                           new key; prints the public key to pin
//   sign <releases.<c>.json> --key <key-file> --kid rel-a [--channel <c>]     writes <feed>.sig
//   verify <releases.<c>.json> [--pub <base64>] [--kid rel-a] [--channel <c>] checks <feed>.sig (no --pub: the pinned keys)
//   sign-content <body.json> --name <name> --key <key-file> --kid rules-a     writes <name>.json beside the body
//   verify-content <name>.json [--pub <base64>] [--kid rules-a]               checks it (no --pub: the pinned rules keys)
// The key file must be outside every git work tree. Release keys sign feeds, rules keys sign content: never swap them.

try
{
    return args switch
    {
        ["keygen", var file, .. var o] when Known(o, "--for") && Opt(o, "--for") is null or "release" or "rules" => Keygen(file, Opt(o, "--for") == "rules"),
        ["sign", var feed, .. var o] when Known(o, "--key", "--kid", "--channel") =>
            Sign(feed, Opt(o, "--key") ?? throw Usage(), Opt(o, "--kid") ?? throw Usage(), Opt(o, "--channel")),
        ["verify", var feed, .. var o] when Known(o, "--pub", "--kid", "--channel") =>
            Verify(feed, Opt(o, "--pub"), Opt(o, "--kid") ?? "rel-a", Opt(o, "--channel")),
        ["sign-content", var body, .. var o] when Known(o, "--name", "--key", "--kid") =>
            SignContent(body, Opt(o, "--name") ?? throw Usage(), Opt(o, "--key") ?? throw Usage(), Opt(o, "--kid") ?? throw Usage()),
        ["verify-content", var file, .. var o] when Known(o, "--pub", "--kid") => VerifyContent(file, Opt(o, "--pub"), Opt(o, "--kid") ?? "rules-a"),
        _ => throw Usage(),
    };
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or FormatException or FeedRejectedException or Win32Exception or System.Text.Json.JsonException)
{
    Console.Error.WriteLine($"release-sign: {e.Message}");
    return 1;
}

static ArgumentException Usage() => new("usage: keygen <key-file> [--for release|rules] | sign <releases.<channel>.json> --key <key-file> --kid <kid> [--channel <c>]"
    + " | verify <releases.<channel>.json> [--pub <base64>] [--kid <kid>] [--channel <c>]"
    + " | sign-content <body.json> --name <name> --key <key-file> --kid <kid> | verify-content <name>.json [--pub <base64>] [--kid <kid>]");

static bool Known(string[] o, params string[] names) => o.Length % 2 == 0 && o.Where((_, i) => i % 2 == 0).All(names.Contains);

static string? Opt(string[] o, string name) => Array.IndexOf(o, name) is var i and >= 0 ? o[i + 1] : null;

static int Keygen(string file, bool rules)
{
    var (seed, pub) = FeedTrust.NewKey();
    using (var f = new FileStream(OutsideGit(file), FileMode.CreateNew, FileAccess.Write)) f.Write(Encoding.ASCII.GetBytes(seed + "\n"));   // never overwrites
    Console.WriteLine($"wrote {Path.GetFullPath(file)}: the private key, keep it offline");
    Console.WriteLine($"public key: {pub}");
    Console.WriteLine(rules
        ? "pin it in ContentTrust.RulesKeys (src/SCSKiller.Core/App/ContentTrust.cs) as rules-a (primary) or rules-b (backup)"
        : "pin it in FeedTrust.ReleaseKeys (src/SCSKiller.Core/App/Updates.cs) as rel-a (primary) or rel-b (backup)");
    return 0;
}

static int Sign(string feed, string keyFile, string kid, string? channel)
{
    channel = ChannelOf(feed, channel);
    var seed = Convert.FromBase64String(File.ReadAllText(OutsideGit(keyFile)).Trim());
    try
    {
        if (seed.Length != 32) throw new FormatException($"{keyFile}: not a 32-byte Ed25519 seed");
        File.WriteAllText(feed + ".sig", FeedTrust.Sign(seed, kid, channel, File.ReadAllBytes(feed), DateTimeOffset.UtcNow));
    }
    finally { CryptographicOperations.ZeroMemory(seed); }
    Console.WriteLine($"wrote {feed}.sig ({channel}, {kid})");
    return 0;
}

static int Verify(string feed, string? pub, string kid, string? channel)
{
    channel = ChannelOf(feed, channel);
    var keys = pub != null ? new Dictionary<string, string> { [kid] = pub } : FeedTrust.ReleaseKeys;
    var tmp = Directory.CreateTempSubdirectory("release-sign-");   // a fresh replay store: nothing seen yet
    try { new FeedTrust(new AppStore(tmp.FullName), keys).Accept(channel, File.ReadAllBytes(feed), File.ReadAllBytes(feed + ".sig")); }
    finally { tmp.Delete(true); }
    Console.WriteLine($"{feed}.sig verifies ({channel}, {(pub != null ? kid : "pinned keys")})");
    return 0;
}

static int SignContent(string bodyFile, string name, string keyFile, string kid)
{
    if (!Regex.IsMatch(name, "^[a-z0-9-]+$")) throw new ArgumentException($"{name}: a content name is [a-z0-9-]+");
    var body = File.ReadAllText(bodyFile);
    if (System.Text.Json.JsonDocument.Parse(body).RootElement.ValueKind != System.Text.Json.JsonValueKind.Object)
        throw new ArgumentException($"{bodyFile}: the body must be a JSON object");
    if (name == News.Name && (News.TryParse(body) is not { } news || news.Items.Count != System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("items").GetArrayLength()))
        throw new ArgumentException($"{bodyFile}: the app would drop it or one of its items (fields, lengths, dates, link)");
    if (name == SCSKiller.Core.Games.GameVerdicts.Name && (SCSKiller.Core.Games.GameVerdicts.TryParse(body) is not { } verdicts
            || verdicts.Count != System.Text.Json.JsonDocument.Parse(body).RootElement.GetProperty("games").GetArrayLength()))
        throw new ArgumentException($"{bodyFile}: the app would skip it or one of its games (fields, verdict, reason code, ids, dates)");
    var outFile = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(bodyFile))!, name + ".json");
    if (string.Equals(outFile, Path.GetFullPath(bodyFile), StringComparison.OrdinalIgnoreCase))
        throw new ArgumentException($"{bodyFile}: name the body something other than {name}.json, the signed file goes there");
    var seed = Convert.FromBase64String(File.ReadAllText(OutsideGit(keyFile)).Trim());
    string envelope;
    try
    {
        if (seed.Length != 32) throw new FormatException($"{keyFile}: not a 32-byte Ed25519 seed");
        envelope = ContentTrust.Sign(seed, kid, name, body, DateTimeOffset.UtcNow);
    }
    finally { CryptographicOperations.ZeroMemory(seed); }
    if (Encoding.UTF8.GetByteCount(envelope) > ContentFile.MaxBytes)
        throw new ArgumentException($"the signed file is over {ContentFile.MaxBytes / 1024} KB, which the app refuses");
    File.WriteAllText(outFile, envelope);
    Console.WriteLine($"wrote {outFile} ({name}, {kid})");
    return 0;
}

static int VerifyContent(string file, string? pub, string kid)
{
    var name = Path.GetFileNameWithoutExtension(file);
    var keys = pub != null ? new Dictionary<string, string> { [kid] = pub } : ContentTrust.RulesKeys;
    var tmp = Directory.CreateTempSubdirectory("release-sign-");   // a fresh replay store: nothing seen yet
    try
    {
        var trust = new ContentTrust(new AppStore(tmp.FullName), keys);
        if (trust.Accept(name, File.ReadAllText(file), b => b) == null) throw new ArgumentException($"{file}: refused, {trust.Rejected}");
    }
    finally { tmp.Delete(true); }
    Console.WriteLine($"{file} verifies ({name}, {(pub != null ? kid : "pinned rules keys")})");
    return 0;
}

static string ChannelOf(string feed, string? channel)
{
    channel ??= Regex.Match(Path.GetFileName(feed), @"^releases\.([a-z]+)\.json$").Groups[1].Value;
    return UpdateChannels.All.Contains(channel) ? channel
        : throw new ArgumentException($"no channel: name the feed releases.<channel>.json or pass --channel {string.Join('|', UpdateChannels.All)}");
}

// Fails closed: only git's own "not a git repository" counts as outside.
static string OutsideGit(string path)
{
    var full = Path.GetFullPath(path);
    var dir = Path.GetDirectoryName(full)!;
    if (!Directory.Exists(dir)) throw new ArgumentException($"no directory {dir}");
    using var git = Process.Start(new ProcessStartInfo("git", ["-C", dir, "rev-parse", "--show-toplevel"])
        { RedirectStandardOutput = true, RedirectStandardError = true })!;
    var err = git.StandardError.ReadToEndAsync();
    var top = git.StandardOutput.ReadToEnd().Trim();
    git.WaitForExit();
    if (git.ExitCode == 0) throw new ArgumentException($"{full} is inside the git work tree {top}: keep the release key outside any repo");
    if (!err.Result.Contains("not a git repository")) throw new ArgumentException($"can't tell whether {dir} is in a git work tree: {err.Result.Trim()}");
    return full;
}
