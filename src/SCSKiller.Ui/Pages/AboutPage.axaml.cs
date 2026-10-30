using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Interactivity;
using SCSKiller.Core.App;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

public sealed partial class AboutPage : UserControl
{
    public AboutVm Vm { get; }

    public AboutPage()
    {
        InitializeComponent();
        using var notices = typeof(AboutPage).Assembly.GetManifestResourceStream("THIRD-PARTY-NOTICES.md")!;
        DataContext = Vm = new AboutVm(new StreamReader(notices).ReadToEnd());
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Vm.Watch(true);
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        Vm.Watch(false);
    }

    void OnCheckForUpdates(object? _, RoutedEventArgs __) => Vm.CheckForUpdates();

    async void OnSource(object? _, RoutedEventArgs __) => await Vm.GetSourceAsync();

    async void OnWhatsNew(object? _, RoutedEventArgs __)
    {
        if (WhatsNew.ForAbout(AppVersion.Current) is { } section) await WhatsNewDialog.Build(section).ShowAsync();
    }

    /// <summary>--screenshots: the trademarks open, or the first component's licence (scrolled to its top).</summary>
    public void ShowExpanded(bool licence)
    {
        TrademarksBox.IsExpanded = !licence;
        if (licence && (Components.ContainerFromIndex(0) as ContentPresenter)?.Child is Expander first)
        {
            first.IsExpanded = true;
            UpdateLayout();
            if (first.TranslatePoint(default, Scroller) is { } at) Scroller.Offset = new(0, Math.Max(0, Scroller.Offset.Y + at.Y));
        }
    }
}
