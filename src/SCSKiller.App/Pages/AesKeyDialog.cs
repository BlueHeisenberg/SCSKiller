using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace SCSKiller.App.Pages;

/// <summary>The user's own AES key for an encrypted Unreal game: checked against the game's files and stored on this PC only.</summary>
static class AesKeyDialog
{
    /// <summary>True once a key that opens the game's files is stored; the caller then re-reads the game.
    /// <paramref name="reason"/>: the Library's Why?, shown above the key.</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, string id, string title, string? reason = null)
    {
        var box = new TextBox { PlaceholderText = "0x followed by 64 hex digits", Header = "AES key" };
        var panel = new StackPanel { Spacing = 12, Children = { new TextBlock { TextWrapping = TextWrapping.Wrap, Text =
            "SCSKiller couldn't find this game's key in its exe (protected exes hide it). If you have the key, paste it here: " +
            "it's checked against the game's files and kept on this PC only." }, box } };
        if (reason != null) panel.Children.Insert(0, new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = panel, CloseButtonText = "Close", PrimaryButtonText = "Unlock", DefaultButton = ContentDialogButton.Close,
        };
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
        var key = box.Text.Trim();
        string? problem = null;
        bool ok;
        try { ok = await Task.Run(() => App.Core.SetEncryptionKey(id, key)); }   // opens the game's files
        catch (Exception ex) { (ok, problem) = (false, ex.Message); }
        if (!ok) await App.ShowAsync(new ContentDialog { XamlRoot = root, Title = "That key didn't work", Content = problem ?? "It doesn't open this game's files.", CloseButtonText = "OK" });
        return ok;
    }
}
