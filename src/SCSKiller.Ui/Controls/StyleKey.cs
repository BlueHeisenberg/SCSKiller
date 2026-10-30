using Avalonia;

namespace SCSKiller.Ui.Controls;

/// <summary>A style class chosen by the view model (the WinUI app's style keys, such as StatusWarn): replaces the
/// element's previous one.</summary>
public sealed class StyleKey : AvaloniaObject
{
    public static readonly AttachedProperty<string?> ClassProperty = AvaloniaProperty.RegisterAttached<StyleKey, StyledElement, string?>("Class");

    static StyleKey() => ClassProperty.Changed.AddClassHandler<StyledElement>((e, a) =>
    {
        if (a.OldValue is string old) e.Classes.Remove(old);
        if (a.NewValue is string now) e.Classes.Add(now);
    });

    public static string? GetClass(StyledElement e) => e.GetValue(ClassProperty);
    public static void SetClass(StyledElement e, string? value) => e.SetValue(ClassProperty, value);
}
