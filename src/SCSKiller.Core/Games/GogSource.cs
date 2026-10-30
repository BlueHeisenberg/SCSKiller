using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed GOG games (GOG Galaxy or the standalone offline installer both write the same registry +
/// info file) from HKLM\SOFTWARE\WOW6432Node\GOG.com\Games\&lt;id&gt; ("path") plus the goggame-&lt;id&gt;.info the
/// installer drops in that folder: its playTasks has the real game exe (the registry's own "exe" value, and the
/// task marked "launcher", is the prelauncher, not the D3D12 process). An id with no "game"-category task (a DLC
/// or expansion registered under its own id, sharing the base game's folder) is skipped.</summary>
public sealed class GogSource : IGameSource
{
    public Store Store => Store.Other;

    public IReadOnlyList<Game> Discover() => OperatingSystem.IsWindows() ? Installed() : [];

    [SupportedOSPlatform("windows")]
    static List<Game> Installed()
    {
        var games = new List<Game>();
        using var root = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GOG.com\Games");
        if (root == null) return games;
        foreach (var id in root.GetSubKeyNames())
            try
            {
                using var key = root.OpenSubKey(id);
                if (key?.GetValue("path") is not string install || !Directory.Exists(install)) continue;
                var infoPath = Path.Combine(install, $"goggame-{id}.info");
                string name = (key.GetValue("gameName") as string) ?? GameFiles.FolderName(install);
                string? exe = null, version = null;
                if (File.Exists(infoPath))
                {
                    try { (name, exe, version) = ParseInfo(File.ReadAllText(infoPath), install, name); }
                    catch (JsonException) { }
                    if (exe == null) continue;   // info file exists but has no "game" playTask: DLC/component entry
                }
                else
                {
                    exe = key.GetValue("exe") is string regExe && regExe.Length > 0 ? Path.GetFullPath(Path.Combine(install, regExe)) : null;
                    exe ??= GameFiles.FindExe(install);
                }
                if (exe == null || !File.Exists(exe)) continue;
                games.Add(new Game($"gog:{id}", name, Store.Other, install, exe, version));
            }
            catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("GOG", $"gog:{id}", e); }
        return games;
    }

    internal static (string Name, string? Exe, string? Version) ParseInfo(string json, string install, string fallbackName)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var name = r.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : fallbackName;
        var version = r.TryGetProperty("buildId", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        string? exe = null;
        if (r.TryGetProperty("playTasks", out var tasks) && tasks.ValueKind == JsonValueKind.Array)
            foreach (var t in tasks.EnumerateArray())
                if (t.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String && cat.GetString() == "game"
                    && t.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    exe = Path.GetFullPath(GameFiles.Find(install, p.GetString()!));
                    break;
                }
        return (name, exe, version);
    }
}
