using Avalonia.Controls;
using Avalonia.Media.Imaging;

namespace SCSKiller.Ui.Platform;

static class NativeClipboard
{
    /// <summary>Avalonia's own clipboard does it here.</summary>
    public static bool TrySet(TopLevel owner, Bitmap? image, string? text) => false;
}
