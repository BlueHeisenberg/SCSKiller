using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using SCSKiller.Core;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

/// <summary>A game added by hand: the exe the recorder goes next to, and the game folder the user confirms (or changes),
/// which SCSKiller checks whole for anti-cheat before it records.</summary>
static class GameFolderDialog
{
    /// <summary>The folder confirmed; null = cancelled.</summary>
    public static async Task<string?> ShowAsync(Game game, string title, string primary)
    {
        var folder = game.InstallDir;
        var folderText = new SelectableTextBlock { Text = folder, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var problem = new InfoBar { Severity = InfoBarSeverity.Error, IsClosable = false, IsVisible = false };
        var change = new Button { Content = Loc.T("Common_Change") + "…", VerticalAlignment = VerticalAlignment.Center };
        var row = new Grid { ColumnSpacing = 12, ColumnDefinitions = new("*,Auto"), Children = { folderText, change } };
        Grid.SetColumn(change, 1);
        static TextBlock Label(string text) => new() { Text = text, Classes = { "BodyStrong" }, Margin = new(0, 8, 0, 0) };
        var dialog = new ContentDialog
        {
            Title = title, PrimaryButtonText = primary, CloseButtonText = Loc.T("Common_Cancel"), DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel
            {
                Spacing = 4, MaxWidth = 520,
                Children =
                {
                    new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.T("Detail_FolderIntro") },
                    Label(Loc.T("Detail_LabelGame")),
                    new TextBlock { Text = game.Name, TextWrapping = TextWrapping.Wrap },
                    Label(Loc.T("Detail_LabelProgram")),
                    new SelectableTextBlock { Text = game.ExePath, TextWrapping = TextWrapping.Wrap },
                    Label(Loc.T("Detail_LabelGameFolder")),
                    row,
                    problem,
                },
            },
        };

        async Task Check()
        {
            folderText.Text = folder;
            var why = await Task.Run(() => Host.Core.ManualFolderProblem(game.ExePath, folder));   // lists the folder's subfolders
            problem.Message = why ?? "";
            problem.IsVisible = why != null;
            dialog.IsPrimaryButtonEnabled = why == null;
        }

        change.Click += async (_, _) =>
        {
            try
            {
                if ((await App.Main.StorageProvider.OpenFolderPickerAsync(new() { Title = Loc.T("Detail_UseThisFolder") })).FirstOrDefault()?.TryGetLocalPath() is { } picked) folder = picked;
            }
            catch (Exception e)
            {
                problem.Message = Loc.T("Common_CouldntOpenFolderPickerWith", e.Message);
                problem.IsVisible = true;
                return;
            }
            await Check();
        };
        await Check();
        return await App.ShowAsync(dialog) == ContentDialogResult.Primary ? folder : null;
    }
}
