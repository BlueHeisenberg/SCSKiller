using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace SCSKiller.Ui.Controls;

/// <summary>A window's ContentDialog over its content, one at a time as WinUI shows them, on a smoke.</summary>
public sealed class DialogLayer : Panel
{
    ContentDialog? open;

    public Border Smoke { get; } = new() { Background = new SolidColorBrush(Color.FromArgb(0x4D, 0, 0, 0)) };

    public DialogLayer()
    {
        IsVisible = false;
        Children.Add(Smoke);
    }

    /// <summary>None when another dialog is open.</summary>
    internal Task<ContentDialogResult> ShowAsync(ContentDialog d) => TryShow(d) ?? Task.FromResult(ContentDialogResult.None);

    /// <summary>Null when another dialog is open, so the caller can tell "never shown" from Esc or Close.</summary>
    internal Task<ContentDialogResult>? TryShow(ContentDialog d)
    {
        if (open != null) return null;
        open = d;
        var result = d.Open(this);
        Children.Add(d);
        IsVisible = true;
        d.Focusable = true;
        Dispatcher.UIThread.Post(d.FocusFirst, DispatcherPriority.Loaded);
        return result;
    }

    /// <summary>The open dialog closed as cancelled: its window is closing.</summary>
    public void HideOpen() => open?.Hide();

    internal void Close(ContentDialog d)
    {
        Children.Remove(d);
        IsVisible = Children.Count > 1;
        if (open == d) open = null;
    }
}
