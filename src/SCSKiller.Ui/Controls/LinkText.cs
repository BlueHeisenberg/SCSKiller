using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;

namespace SCSKiller.Ui.Controls;

/// <summary>A TextBlock whose runs of class "Link" act as WinUI's Hyperlink inline: they wrap with the text around them,
/// show the hand cursor and raise <see cref="LinkClick"/>.</summary>
public sealed class LinkText : TextBlock
{
    public event EventHandler<Run>? LinkClick;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    Run? LinkAt(Point p)
    {
        var hit = TextLayout.HitTestPoint(new Point(p.X - Padding.Left, p.Y - Padding.Top));
        if (!hit.IsInside || Inlines is not { } inlines) return null;
        int at = 0;
        foreach (var inline in inlines)
        {
            int length = inline is Run r ? r.Text?.Length ?? 0 : 1;
            if (hit.TextPosition >= at && hit.TextPosition < at + length) return inline is Run link && link.Classes.Contains("Link") ? link : null;
            at += length;
        }
        return null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Cursor = LinkAt(e.GetPosition(this)) != null ? new Cursor(StandardCursorType.Hand) : null;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left && LinkAt(e.GetPosition(this)) is { } link)
        {
            e.Handled = true;
            LinkClick?.Invoke(this, link);
        }
    }
}
