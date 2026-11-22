using System.IO.Compression;
using System.Text.Json;

namespace SCSKiller.Core.App;

/// <summary>SCSKiller's companion plugin for Decky Loader: where Decky keeps its plugins, which published plugin release
/// is the newest, and how that release is unpacked into Decky's folder. Decky loads what sits in that folder when Steam
/// starts, so an install only has to put the files there.</summary>
public static class Decky
{
    /// <summary>The repository that publishes the plugin: one GitHub release per plugin version, tagged
    /// <c>SCSKiller-v&lt;version&gt;</c>, with the installable zip as its asset.</summary>
    public const string Repository = "JamesDAdams/my-decky-plugins";

    /// <summary>The plugin's loader key: the name in its plugin.json, which is both Decky's folder for it and its URL
    /// under 127.0.0.1:1337.</summary>
    public const string PluginName = "SCSKiller";

    const string Releases = "https://api.github.com/repos/" + Repository + "/releases?per_page=20";
    const string TagPrefix = PluginName + "-v";

    /// <summary>Decky's home: <c>%USERPROFILE%\homebrew</c> on Windows, <c>$HOME/homebrew</c> elsewhere (the Deck's own
    /// <c>/home/deck/homebrew</c>). DECKY_HOME overrides it, for a fake Decky folder in a test.</summary>
    public static string Home => Environment.GetEnvironmentVariable("DECKY_HOME") is { Length: > 0 } folder
        ? folder
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "homebrew");

    /// <summary>Where Decky keeps one folder per installed plugin.</summary>
    public static string PluginsFolder(string? home = null) => Path.Combine(home ?? Home, "plugins");

    /// <summary>The folder Decky installs this plugin into.</summary>
    public static string PluginFolder(string? home = null) => Path.Combine(PluginsFolder(home), PluginName);

    /// <summary>Whether Decky Loader is installed here: its installer always makes the plugins folder.</summary>
    public static bool IsInstalled(string? home = null) => Directory.Exists(PluginsFolder(home));

    /// <summary>The plugin version found in Decky's folder (its package.json), or null when it isn't installed.</summary>
    public static string? InstalledVersion(string? home = null)
    {
        try
        {
            var manifest = Path.Combine(PluginFolder(home), "package.json");
            if (!File.Exists(manifest)) return null;
            using var json = JsonDocument.Parse(File.ReadAllText(manifest));
            return json.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    /// <summary>A published plugin release: its version, and the zip of the installable plugin.</summary>
    public readonly record struct Release(string Version, Uri Zip);

    /// <summary>The newest published plugin release, from GitHub's API: drafts are skipped, newest first.</summary>
    public static async Task<Release> LatestAsync(HttpClient http, CancellationToken cancel = default)
    {
        using var request = Identify(HttpMethod.Get, new Uri(Releases));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
        foreach (var release in json.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            if (!release.TryGetProperty("tag_name", out var tag) || tag.GetString() is not { } name
                || !name.StartsWith(TagPrefix, StringComparison.Ordinal)) continue;
            if (!release.TryGetProperty("assets", out var assets)) continue;
            foreach (var asset in assets.EnumerateArray())
            {
                if (!asset.TryGetProperty("name", out var file) || file.GetString() is not { } fileName
                    || !fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) continue;
                if (asset.TryGetProperty("browser_download_url", out var url) && url.GetString() is { } link)
                    return new Release(name[TagPrefix.Length..], new Uri(link));
            }
        }
        throw new InvalidOperationException($"No {PluginName} plugin release was found in {Repository}.");
    }

    /// <summary>GitHub answers 403 to a request with no User-Agent, so each request names the app even when the caller's
    /// client sends none (a client of its own, outside the app's RouteFailover handler).</summary>
    static HttpRequestMessage Identify(HttpMethod method, Uri uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.UserAgent.TryParseAdd("SCSKiller");
        return request;
    }

    /// <summary>Downloads <paramref name="release"/>'s zip and unpacks the plugin over what Decky has, returning the
    /// folder written. Throws when the folder can't be replaced, with what to do about it.</summary>
    public static async Task<string> InstallAsync(HttpClient http, Release release, string? home = null, CancellationToken cancel = default)
    {
        var zip = DownloadPath(home);
        var partial = zip + ".partial";
        try
        {
            using (var response = await http.SendAsync(Identify(HttpMethod.Get, release.Zip), HttpCompletionOption.ResponseHeadersRead, cancel))
            {
                response.EnsureSuccessStatusCode();
                await using (var download = File.Create(partial))
                    await response.Content.CopyToAsync(download, cancel);
            }
            File.Move(partial, zip, overwrite: true);
            return Unpack(zip, PluginFolder(home));
        }
        finally
        {
            foreach (var path in new[] { zip, partial })
                try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>Where the zip goes while it installs: the temp folder when it takes a file, else Decky's own plugins
    /// folder, which the install writes into anyway. A temp folder can be read-only or be a full disk; the message lists
    /// what each folder answered when neither takes it.</summary>
    static string DownloadPath(string? home)
    {
        string[] candidates = [Path.GetTempPath(), Path.Combine(home ?? Home, "plugins")];
        var refused = new List<string>();
        foreach (var folder in candidates)
        {
            try
            {
                Directory.CreateDirectory(folder);
                var probe = Path.Combine(folder, $"scskiller-probe-{Guid.NewGuid():N}.tmp");
                using (File.Create(probe)) { }
                File.Delete(probe);
                return Path.Combine(folder, $"scskiller-decky-{Guid.NewGuid():N}.zip");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                refused.Add($"{folder}: {e.GetType().Name}: {e.Message}");
            }
        }
        throw new IOException("The plugin's download has nowhere to go - " + string.Join(" | ", refused));
    }

    /// <summary>Unpacks the zip's one top-level folder into <paramref name="target"/>, after checking that it is the
    /// SCSKiller plugin and that no path leaves the folder. It unpacks beside the target first: an unpack that fails
    /// leaves the working copy alone.</summary>
    static string Unpack(string zip, string target)
    {
        using var archive = ZipFile.OpenRead(zip);
        var files = new List<(ZipArchiveEntry Entry, string Relative)>();
        string? root = null;
        foreach (var entry in archive.Entries)
        {
            // "SCSKiller/plugin.json": the plugin's own folder, then the path inside it
            var parts = entry.FullName.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;   // the folder's line, and anything beside it
            root ??= parts[0];
            if (root != parts[0]) throw new InvalidDataException($"The plugin zip holds more than one folder: {root}, {parts[0]}.");
            var relative = string.Join('/', parts[1..]);
            if (relative.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                throw new InvalidDataException($"The plugin zip writes outside its folder: {entry.FullName}");
            files.Add((entry, relative));
        }
        if (root == null) throw new InvalidDataException("The plugin zip is empty.");
        var manifest = files.FirstOrDefault(f => f.Relative == "plugin.json").Entry
            ?? throw new InvalidDataException($"The plugin zip has no {root}/plugin.json.");
        string name;
        using (var json = JsonDocument.Parse(manifest.Open()))
            name = json.RootElement.TryGetProperty("name", out var named) ? named.GetString() ?? "" : "";
        if (!string.Equals(name, PluginName, StringComparison.Ordinal))
            throw new InvalidDataException($"The plugin zip carries '{name}', not {PluginName}.");

        // The working copy is set aside, never deleted first: an unpack that can't be put in place is put back, so a
        // failed install never leaves Decky without the plugin it had.
        var staging = target + ".new";
        var previous = target + ".old";
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            foreach (var (entry, relative) in files)
            {
                var path = Path.Combine(staging, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: true);
            }
            if (Directory.Exists(previous)) Directory.Delete(previous, recursive: true);
            if (Directory.Exists(target)) Directory.Move(target, previous);
            try { Directory.Move(staging, target); }
            catch
            {
                if (!Directory.Exists(target) && Directory.Exists(previous)) Directory.Move(previous, target);
                throw;
            }
            try { Directory.Delete(previous, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Decky's folder for the plugin can't be replaced ({target}): close Steam and Decky Loader, then install again. {e.Message}", e);
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return target;
    }
}
