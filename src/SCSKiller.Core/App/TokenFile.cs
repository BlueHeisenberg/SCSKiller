namespace SCSKiller.Core.App;

/// <summary>The system keyring as <see cref="TokenFile{T}"/> uses it: on Linux the Secret Service (org.freedesktop.secrets),
/// which a Flatpak would reach through the Secret portal instead. Items are found by their attributes. It never prompts:
/// a locked keyring counts as unavailable.</summary>
public interface IKeyring
{
    /// <summary>False when it can't take the secret now: not running, locked, no default collection, or it wants a prompt.</summary>
    Task<bool> StoreAsync(string label, IReadOnlyDictionary<string, string> attributes, byte[] secret, CancellationToken ct);

    /// <summary>The secret of the item with these attributes. <see cref="KeyringState.Missing"/> only when the keyring
    /// answered, unlocked, that it has none.</summary>
    Task<KeyringRead> ReadAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct);

    /// <summary>Deletes the items with these attributes. True only when none is left: those found deleted, with every
    /// collection open. False when it couldn't tell or couldn't delete: unavailable, or an item or a collection locked.</summary>
    Task<bool> DeleteAsync(IReadOnlyDictionary<string, string> attributes, CancellationToken ct);

    /// <summary>False: new tokens go to the file, and the keyring only reads and deletes the items an earlier build saved
    /// there (SteamOS).</summary>
    bool TakesNew => true;
}

public enum KeyringState { Found, Missing, Unavailable }

public readonly record struct KeyringRead(KeyringState State, byte[]? Secret = null);

/// <summary>The keyring holds the token but can't give it now (locked or not running): neither signed out nor a reason
/// to register again.</summary>
public sealed class KeyringUnavailableException() : Exception("The system keyring is locked or not running.");

/// <summary>The keyring has the item but its secret isn't a token: kept as it is, neither signed out nor a reason to
/// register again.</summary>
public sealed class KeyringItemUnreadableException() : Exception("The system keyring's item can't be read.");

/// <summary>A device token kept in the data folder: auth.dat (the Patreon sign-in) or upload.dat (the anonymous upload
/// device), the JSON of <typeparamref name="T"/>. On Windows the file itself, under DPAPI (CurrentUser). On Linux a file
/// only this user can read (0600) that holds the token, or, when the system keyring took it, its keyring item's id, so
/// the file alone tells whether there is one. A token stays where it was saved until it is deleted: a keyring that
/// comes or goes later never moves it, signs out or registers again.</summary>
public sealed partial class TokenFile<T> where T : class
{
    readonly string path;
    readonly SemaphoreSlim gate = new(1, 1);   // one save or delete at a time: a commit sees the file the last one left

    /// <param name="label">The keyring item's label, which keyring managers show.</param>
    /// <param name="keyring">Null: the file only (the CLI, tests, the design data).</param>
    public TokenFile(string path, string label, IKeyring? keyring = null, IProgress<string>? log = null) =>
        (this.path, Label, Keyring, Log) = (path, label, keyring, log);

    public string Label { get; }
    public IKeyring? Keyring { get; }
    public IProgress<string>? Log { get; }

    /// <summary>What the file holds: the token, or the id of the keyring item that holds it (<see cref="Value"/> null
    /// until <see cref="ReadAsync"/>).</summary>
    public sealed record Kept(T? Value, string? KeyringId);

    /// <summary>The same token: the same keyring item, or the same value in the file.</summary>
    internal static bool Same(Kept a, Kept b) => (a.KeyringId, b.KeyringId) is (null, null) ? Equals(a.Value, b.Value) : a.KeyringId == b.KeyringId;
}
