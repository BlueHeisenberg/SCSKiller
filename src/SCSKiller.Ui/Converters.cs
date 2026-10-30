using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using SCSKiller.Core;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui;

public static class Converters
{
    /// <summary>A view model's ARGB colour as a brush.</summary>
    public static readonly IValueConverter ArgbBrush = new FuncValueConverter<uint, IBrush>(argb => new SolidColorBrush(argb));
    /// <summary>A view model's image (typed object there: it holds no UI types) as an image source.</summary>
    public static readonly IValueConverter Image = new FuncValueConverter<object?, IImage?>(o => o as IImage);
    /// <summary>The game page's status icon as its glyph.</summary>
    public static readonly IValueConverter StatusGlyph = new FuncValueConverter<StatusIcon, string>(i => i switch
    {
        StatusIcon.Warning => Glyphs.Warning, StatusIcon.Warmed => Glyphs.Checkmark, StatusIcon.Ready => Glyphs.Bolt, StatusIcon.Record => Glyphs.Record, _ => Glyphs.Blocked,
    });
    /// <summary>Path data from a view model as a geometry; null draws nothing.</summary>
    public static readonly IValueConverter PathData = new FuncValueConverter<string?, Geometry?>(d => d is null ? null : Geometry.Parse(d));
    /// <summary>A finished queue row's stage as its glyph.</summary>
    public static readonly IValueConverter StageGlyph = new FuncValueConverter<QueueStage, string>(s => s switch
    {
        QueueStage.Done => Glyphs.Checkmark, QueueStage.Failed => Glyphs.Warning, _ => Glyphs.Stop,
    });
    /// <summary>A radio button of a group bound to one index: checked when the index is the parameter, and checking it sets it.</summary>
    public static readonly IValueConverter Index = new IndexConverter();

    sealed class IndexConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && i == Of(parameter);
        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is true ? Of(parameter) : BindingOperations.DoNothing;
        static int Of(object? parameter) => System.Convert.ToInt32(parameter, CultureInfo.InvariantCulture);
    }
}
