using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SCSKiller.Core.Games;

/// <summary>Installed HoYoPlay games from the launcher's per-game keys: HKCU\Software\Cognosphere\HYP\1_0\&lt;biz&gt; (global)
/// and HKCU\Software\miHoYo\HYP\1_1\&lt;biz&gt; (China), value GameInstallPath, the folder of the game exe. biz is the
/// game code plus server (hk4e_global, hkrpg_cn, nap_global, bh3_global, ...). The launcher writes config.ini's
/// [General] game_version when the install finishes; without it the game isn't listed.</summary>
public sealed class HoYoPlaySource(IEnumerable<(string Biz, string InstallDir)>? entries = null) : IGameSource
{
    public Store Store => Store.Other;

    static readonly Dictionary<string, string> Names = new()
    {
        ["hk4e"] = "Genshin Impact", ["hkrpg"] = "Honkai: Star Rail", ["nap"] = "Zenless Zone Zero", ["bh3"] = "Honkai Impact 3rd",
    };

    static readonly Regex GameVersion = new(@"^\s*game_version\s*=\s*(\S+)", RegexOptions.Multiline);

    public IReadOnlyList<Game> Discover()
    {
        var games = new List<Game>();
        foreach (var (biz, install) in entries ?? (OperatingSystem.IsWindows() ? Entries() : []))
            try
            {
                if (!Directory.Exists(install)) continue;
                string text;
                try { text = File.ReadAllText(Path.Combine(install, "config.ini")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                if (GameVersion.Matches(text) is not { Count: > 0 } versions) continue;
                if (GameFiles.FindExe(install) is not { } exe) continue;
                var name = Names.GetValueOrDefault(biz.Split('_')[0]) ?? Path.GetFileNameWithoutExtension(exe);
                games.Add(new Game($"hoyoplay:{biz}", name, Store.Other, install, exe, versions[^1].Groups[1].Value));
            }
            catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("HoYoPlay", $"hoyoplay:{biz}", e); }
        return games;
    }

    [SupportedOSPlatform("windows")]
    static IEnumerable<(string, string)> Entries()
    {
        var entries = new List<(string, string)>();
        foreach (var path in new[] { @"Software\Cognosphere\HYP", @"Software\miHoYo\HYP" })
        {
            using var hyp = Registry.CurrentUser.OpenSubKey(path);
            if (hyp == null) continue;
            foreach (var version in hyp.GetSubKeyNames())
            {
                using var v = hyp.OpenSubKey(version);
                if (v == null) continue;
                foreach (var biz in v.GetSubKeyNames())
                    try
                    {
                        using var key = v.OpenSubKey(biz);
                        if (key?.GetValue("GameInstallPath") is string install && install.Length > 0) entries.Add((biz, install));
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { GameFiles.Skip("HoYoPlay", $"hoyoplay:{biz}", e); }
            }
        }
        return entries;
    }
}
