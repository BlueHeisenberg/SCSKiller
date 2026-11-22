using Avalonia.Controls;
using Avalonia.Interactivity;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

public sealed partial class BigPicturePage : UserControl
{
    public BigPictureVm Vm { get; }

    public BigPicturePage()
    {
        InitializeComponent();
        DataContext = Vm = new BigPictureVm();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        _ = Vm.LoadAsync();
    }

    async void OnInstall(object? _, RoutedEventArgs __) => await Vm.InstallAsync();

    // the links open in the user's browser through the shell, which says so on the page when it refuses
    void OnDeckyDownloads(object? _, RoutedEventArgs __) => Vm.Open(BigPictureVm.DeckyDownloads);
    void OnDeckySite(object? _, RoutedEventArgs __) => Vm.Open(BigPictureVm.DeckySite);
    void OnPluginReleases(object? _, RoutedEventArgs __) => Vm.Open(BigPictureVm.PluginReleases);
}
