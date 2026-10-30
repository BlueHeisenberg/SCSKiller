using SCSKiller.Core.App;
using SCSKiller.Ui.Platform;

namespace SCSKiller.Ui;

public sealed partial class MainWindow
{
    void TrackPlacement() { }

    (double Width, double Height)? RestoreBounds(WindowBounds saved) => WindowPlacement.Restore(hwnd, saved);

    WindowBounds? ReadBounds() => WindowPlacement.Read(hwnd);
}
