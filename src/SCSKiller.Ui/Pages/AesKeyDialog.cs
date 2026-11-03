using Avalonia.Controls;
using Avalonia.Media;
using SCSKiller.Core.App;
using SCSKiller.Ui.Controls;
using SCSKiller.Ui.Models;

namespace SCSKiller.Ui.Pages;

/// <summary>The user's own AES keys for an encrypted Unreal game, or a BlackSpace game's key: checked against the game's
/// files and stored on this PC only.</summary>
static class AesKeyDialog
{
    /// <summary>True once a key that opens the game's files is stored; the caller then re-reads the game.
    /// <paramref name="reason"/>: the Library's Why?, shown above the key.</summary>
    public static async Task<bool> ShowAsync(string id, string title, string? reason = null, bool aes = true)
    {
        var (dialog, box) = Dialog(title, reason, aes);
        if (await App.ShowAsync(dialog) != ContentDialogResult.Primary) return false;
        var key = (box.Text ?? "").Trim();
        (bool Stored, IReadOnlyList<string> Refused) result;
        try { result = await Task.Run(() => Host.Core.SetEncryptionKey(id, key)); }   // opens the game's files
        catch (Exception ex) { result = (false, [ex.Message]); }
        if (result.Refused.Count > 0) await App.ShowAsync(Refused(result.Stored, result.Refused));
        return result.Stored;
    }

    public static (ContentDialog Dialog, TextBox Box) Dialog(string title, string? reason, bool aes)
    {
        var box = new TextBox { PlaceholderText = aes ? MultiKeyHint : "0x followed by 64 hex digits", Classes = { "clearButton" }, AcceptsReturn = aes, MaxHeight = 160 };
        var panel = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { TextWrapping = TextWrapping.Wrap, Text = (aes ? AesIntro : GameKeyIntro) +
                    " If you have the key, paste it here: it's checked against the game's files and kept on this PC only." },
                new StackPanel { Spacing = 8, Children = { new TextBlock { Text = aes ? "AES key" : "Game key" }, box } },
            },
        };
        if (reason != null) panel.Children.Insert(0, new TextBlock { Text = reason, TextWrapping = TextWrapping.Wrap });
        return (new ContentDialog { Title = title, Content = panel, CloseButtonText = "Close", PrimaryButtonText = "Unlock", DefaultButton = ContentDialogButton.Close }, box);
    }

    /// <summary>The lines that didn't work, each with why. <paramref name="stored"/>: the others were kept.</summary>
    public static ContentDialog Refused(bool stored, IReadOnlyList<string> refused)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.AddRange(refused.Select(r => new TextBlock { Text = Format.Sentence(r.TrimEnd('.')) + ".", TextWrapping = TextWrapping.Wrap }));
        return new ContentDialog { Title = stored ? "Some keys didn't work" : refused.Count > 1 ? "Those keys didn't work" : "That key didn't work", Content = panel, CloseButtonText = "OK" };
    }

    const string MultiKeyHint = "0x and 64 hex digits, one key per line";
    const string AesIntro = "SCSKiller couldn't find this game's key in its exe (protected exes hide it).";
    const string GameKeyIntro = "With this game's key, SCSKiller compiles it without a recording.";
}
