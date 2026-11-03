using Avalonia.Threading;
using Windows.UI.ViewManagement;

namespace SCSKiller.Ui.Platform;

static partial class TextScale
{
    static UISettings? settings;   // kept: a collected UISettings raises no more events

    /// <summary>Applies the setting now and whenever it changes.</summary>
    public static void Follow()
    {
        try
        {
            settings = new UISettings();
            Apply(settings.TextScaleFactor);
            settings.TextScaleFactorChanged += (s, _) => Dispatcher.UIThread.Post(() => Apply(s.TextScaleFactor));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or TypeLoadException) { }
    }
}
