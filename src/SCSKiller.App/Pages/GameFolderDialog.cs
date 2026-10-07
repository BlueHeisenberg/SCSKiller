using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SCSKiller.Core;
using SCSKiller.Core.App;

namespace SCSKiller.App.Pages;

/// <summary>A game added by hand: the exe the recorder goes next to, and the game folder the user confirms (or changes),
/// which SCSKiller checks whole for anti-cheat before it records.</summary>
static class GameFolderDialog
{
    /// <summary>The folder confirmed; null = cancelled.</summary>
    public static async Task<string?> ShowAsync(XamlRoot root, Game game, string title, string primary)
    {
        var folder = game.InstallDir;
        var folderText = new TextBlock { Text = folder, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, VerticalAlignment = VerticalAlignment.Center };
        var problem = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false };
        var change = new Button { Content = Loc.Text("Change…"), VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnSpacing = 12, ColumnDefinitions = { new() { Width = new GridLength(1, GridUnitType.Star) }, new() { Width = GridLength.Auto } } };
        row.Children.Add(folderText);
        Grid.SetColumn(change, 1);
        row.Children.Add(change);
        TextBlock Label(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) };
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, PrimaryButtonText = primary, CloseButtonText = Loc.Text("Cancel"), DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 4, MaxWidth = 520,
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.Text("Before it records, SCSKiller checks the whole game folder for anti-cheat. ")
                        + Loc.Text("Pick the game's own folder, not one that holds other games.") },
                    Label(Loc.Text("Game")),
                    new TextBlock { Text = game.Name, TextWrapping = TextWrapping.Wrap },
                    Label(Loc.Text("Program (the recorder goes next to it)")),
                    new TextBlock { Text = game.ExePath, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                    Label(Loc.Text("Game folder")),
                    row,
                    problem,
                },
            },
        };

        async Task Check()
        {
            folderText.Text = folder;
            var why = await Task.Run(() => App.Core.ManualFolderProblem(game.ExePath, folder));   // lists the folder's subfolders
            problem.Message = why ?? "";
            problem.IsOpen = why != null;
            dialog.IsPrimaryButtonEnabled = why == null;
        }

        change.Click += async (_, _) =>
        {
            try
            {
                var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(App.Main.AppWindow.Id) { CommitButtonText = Loc.Text("Use this folder") };
                if ((await picker.PickSingleFolderAsync())?.Path is { } picked) folder = picked;
            }
            catch (Exception e)
            {
                problem.Message = Loc.Text("Couldn't open the folder picker: ") + e.Message;
                problem.IsOpen = true;
                return;
            }
            await Check();
        };
        await Check();
        return await App.ShowAsync(dialog) == ContentDialogResult.Primary ? folder : null;
    }
}
