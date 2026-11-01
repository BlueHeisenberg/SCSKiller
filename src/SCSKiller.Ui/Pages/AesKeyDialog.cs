using Avalonia.Controls;
using Avalonia.Media;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;
using SCSKiller.Core.Localization;

namespace SCSKiller.Ui.Pages;

/// <summary>The user's own AES key for an encrypted Unreal game, or a BlackSpace game's key: checked against the game's
/// files and stored on this PC only.</summary>
static class AesKeyDialog
{
    /// <summary>True once a key that opens the game's files is stored; the caller then re-reads the game.
    /// <paramref name="reason"/>: the Library's Why?, shown above the key.</summary>
    public static async Task<bool> ShowAsync(string id, string title, string? reason = null, bool aes = true)
    {
        var box = new TextBox { PlaceholderText = Loc.T("AesKey_Placeholder"), Classes = { "clearButton" } };
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = (aes ? Loc.T("AesKey_AesIntro") : Loc.T("AesKey_GameKeyIntro")) + " " + Loc.T("AesKey_PastePrompt") },
                new StackPanel { Spacing = 8, Children = { new TextBlock { Text = aes ? Loc.T("AesKey_AesKey") : Loc.T("AesKey_GameKey") }, box } },
            },
        };
        if (reason != null) panel.Children.Insert(0, new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog { Title = title, Content = panel, CloseButtonText = Loc.T("Common_Close"), PrimaryButtonText = Loc.T("AesKey_UnlockBtn"), DefaultButton = ContentDialogButton.Close };
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
        var key = (box.Text ?? "").Trim();
        string? problem = null;
        bool ok;
        try { ok = await Task.Run(() => Host.Core.SetEncryptionKey(id, key)); }   // opens the game's files
        catch (Exception ex) { (ok, problem) = (false, ex.Message); }
        if (!ok) await App.ShowAsync(new ContentDialog { Title = Loc.T("AesKey_FailedTitle"), Content = problem ?? Loc.T("AesKey_FailedContent"), CloseButtonText = Loc.T("Common_OK") });
        return ok;
    }

    const string AesIntro = "SCSKiller couldn't find this game's key in its exe (protected exes hide it).";
    const string GameKeyIntro = "With this game's key, SCSKiller compiles it without a recording.";
}
