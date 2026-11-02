using Avalonia.Controls;
using Avalonia.Media;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

/// <summary>The user's own AES key for an encrypted Unreal game, or a BlackSpace game's key: checked against the game's
/// files and stored on this PC only.</summary>
static class AesKeyDialog
{
    /// <summary>True once a key that opens the game's files is stored; the caller then re-reads the game.
    /// <paramref name="reason"/>: the Library's Why?, shown above the key.</summary>
    public static async Task<bool> ShowAsync(string id, string title, string? reason = null, bool aes = true)
    {
        var box = new TextBox { PlaceholderText = Loc.T("Detail_KeyPlaceholder"), Classes = { "clearButton" } };
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = Loc.T(aes ? "Detail_AesKeyIntro" : "Detail_GameKeyIntro") },
                new StackPanel { Spacing = 8, Children = { new TextBlock { Text = aes ? Loc.T("Detail_AesKeyHeader") : Loc.T("Detail_GameKeyHeader") }, box } },
            },
        };
        if (reason != null) panel.Children.Insert(0, new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = title, Content = panel, CloseButtonText = Loc.T("Common_Close"), PrimaryButtonText = Loc.T("Detail_Unlock"), DefaultButton = ContentDialogButton.Close };
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
        var key = (box.Text ?? "").Trim();
        string? problem = null;
        bool ok;
        try { ok = await Task.Run(() => Host.Core.SetEncryptionKey(id, key)); }   // opens the game's files
        catch (Exception ex) { (ok, problem) = (false, ex.Message); }
        if (!ok) await App.ShowAsync(new ContentDialog { Title = Loc.T("Detail_KeyDidntWork"), Content = problem ?? Loc.T("Detail_KeyDoesntOpen"), CloseButtonText = Loc.T("Common_Ok") });
        return ok;
    }
}
