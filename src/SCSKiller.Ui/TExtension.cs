using Avalonia.Markup.Xaml;
using SCSKiller.Core.App;

namespace SCSKiller.Ui;

/// <summary>The text of a Loc key, as {loc:T Key} in XAML: one read at load, as the language is fixed for the run (Loc.Load at start).</summary>
public sealed class TExtension(string key) : MarkupExtension
{
    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(key);
}
