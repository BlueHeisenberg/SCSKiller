using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SCSKiller.Core.App;

namespace SCSKiller.App.Pages;

/// <summary>The user's own AES keys for an encrypted Unreal game, or a BlackSpace game's key: checked against the game's
/// files and stored on this PC only.</summary>
static class AesKeyDialog
{
    /// <summary>True once a key that opens the game's files is stored; the caller then re-reads the game.
    /// <paramref name="reason"/>: the Library's Why?, shown above the key.</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, string id, string title, string? reason = null, bool aes = true)
    {
        var box = new TextBox { PlaceholderText = aes ? MultiKeyHint : "0x followed by 64 hex digits", Header = aes ? "AES key" : "Game key", AcceptsReturn = aes, MaxHeight = 160 };
        var panel = new StackPanel { Spacing = 12, Children = { new TextBlock { TextWrapping = TextWrapping.Wrap, Text = (aes ? AesIntro : GameKeyIntro) +
            " If you have the key, paste it here: it's checked against the game's files and kept on this PC only." }, box } };
        if (reason != null) panel.Children.Insert(0, new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = root, Title = title, Content = panel, CloseButtonText = "Close", PrimaryButtonText = "Unlock", DefaultButton = ContentDialogButton.Close,
        };
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
        var key = box.Text.Trim();
        (bool Stored, IReadOnlyList<string> Refused) result;
        try { result = await Task.Run(() => App.Core.SetEncryptionKey(id, key)); }   // opens the game's files
        catch (Exception ex) { result = (false, [ex.Message]); }
        if (result.Refused.Count > 0)
        {
            var lines = new StackPanel { Spacing = 4 };
            foreach (var r in result.Refused) lines.Children.Add(new TextBlock { Text = Format.Sentence(r.TrimEnd('.')) + ".", TextWrapping = TextWrapping.Wrap });
            var heading = result.Stored ? "Some keys didn't work" : result.Refused.Count > 1 ? "Those keys didn't work" : "That key didn't work";
            await App.ShowAsync(new ContentDialog { XamlRoot = root, Title = heading, Content = lines, CloseButtonText = "OK" });
        }
        return result.Stored;
    }

    const string MultiKeyHint = "0x and 64 hex digits, one key per line";
    const string AesIntro = "SCSKiller couldn't find this game's key in its exe (protected exes hide it).";
    const string GameKeyIntro = "With this game's key, SCSKiller compiles it without a recording.";
}
