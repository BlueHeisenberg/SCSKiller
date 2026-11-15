using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Controls;

/// <summary>A window's ContentDialog over its content, one at a time as WinUI shows them, on a smoke.</summary>
public sealed class DialogLayer : Panel
{
    ContentDialog? open;
    readonly DialogWait wait = new(a => Dispatcher.UIThread.Post(a));

    public Border Smoke { get; } = new() { Background = new SolidColorBrush(Color.FromArgb(0x4D, 0, 0, 0)) };

    public DialogLayer()
    {
        IsVisible = false;
        Children.Add(Smoke);
    }

    /// <summary>None when another dialog is open.</summary>
    internal Task<ContentDialogResult> ShowAsync(ContentDialog d) => TryShow(d) ?? Dropped(d);

    internal static Task<ContentDialogResult> Dropped(ContentDialog d)
    {
        Host.Log?.Report($"dialog \"{d.Title}\" not shown: another dialog is open");
        return Task.FromResult(ContentDialogResult.None);
    }

    /// <summary>Shown once no other dialog is open: the start's questions, which a dialog the user opened first must not drop.</summary>
    internal Task<ContentDialogResult> ShowWhenFreeAsync(ContentDialog d) => wait.ShowAsync(() => TryShow(d), d.Title ?? "");

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
        if (open != d) return;
        open = null;
        wait.Closed();
    }
}
