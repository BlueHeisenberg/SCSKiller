using System.Runtime.InteropServices;

namespace SCSKiller.Ui.Platform;

static class Sound
{
    /// <summary>The system's Asterisk sound, async: it plays over a game.</summary>
    public static void Information() => MessageBeep(0x40 /* MB_ICONASTERISK */);

    [DllImport("user32.dll")] static extern bool MessageBeep(uint type);
}
