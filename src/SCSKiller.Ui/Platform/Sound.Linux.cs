using System.ComponentModel;
using System.Diagnostics;

namespace SCSKiller.Ui.Platform;

/// <summary>The sound theme's dialog-information, Windows' Asterisk. libcanberra's player follows the desktop's sound theme
/// and its event sounds switch. Without it, the freedesktop theme's file plays through PipeWire or PulseAudio. With
/// neither (a Flatpak's runtime has none), no sound.</summary>
static class Sound
{
    const string Name = "dialog-information";

    public static void Information() => Task.Run(() =>
    {
        if (Start("canberra-gtk-play", "--id", Name, "--description", "SCSKiller")) return;
        if (Find(Name, DataDirs()) is not { } file) return;
        if (!Start("pw-play", file)) Start("paplay", file);
    });

    /// <summary>False: no such program.</summary>
    static bool Start(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program) { UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var _ = Process.Start(psi);
            return true;
        }
        catch (Win32Exception) { return false; }
    }

    /// <summary>XDG_DATA_HOME, then XDG_DATA_DIRS (or its default), as the sound theme spec searches them.</summary>
    internal static IEnumerable<string> DataDirs()
    {
        var home = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } h ? h
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        var dirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS") is { Length: > 0 } d ? d : "/usr/local/share:/usr/share";
        return [home, .. dirs.Split(':', StringSplitOptions.RemoveEmptyEntries)];
    }

    /// <summary>The freedesktop theme's stereo file for <paramref name="name"/> in the first data folder that has it.</summary>
    internal static string? Find(string name, IEnumerable<string> dataDirs) =>
        dataDirs.SelectMany(d => new[] { "oga", "ogg", "wav" }.Select(ext => Path.Combine(d, "sounds", "freedesktop", "stereo", $"{name}.{ext}")))
            .FirstOrDefault(File.Exists);
}
