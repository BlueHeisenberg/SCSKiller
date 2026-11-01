using Avalonia.Data;
using Avalonia.Markup.Xaml;
using SCSKiller.Core.Localization;

namespace SCSKiller.Ui;

/// <summary>Avalonia markup extension for dynamic localized strings: {loc:Loc KeyName}.</summary>
public sealed class LocExtension : MarkupExtension
{
    public string Key { get; set; }

    public LocExtension(string key) => Key = key;

    public override object ProvideValue(IServiceProvider serviceProvider) =>
        new Binding($"[{Key}]")
        {
            Source = Loc.Instance,
            Mode = BindingMode.OneWay
        };
}

