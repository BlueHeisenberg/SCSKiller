using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SCSKiller.Core.App;

public sealed partial class TokenFile<T>
{
    // {"Token":{...}} in the file itself, or {"Keyring":"<item id>"}
    sealed record Envelope(T? Token = null, string? Keyring = null);

    const UnixFileMode GroupOrOther = (UnixFileMode)0b000_111_111;
    const int MaxBytes = 64 << 10;

    string Name => Path.GetFileName(path);

    static Dictionary<string, string> Attributes(string id) => new() { ["application"] = "SCSKiller", ["scskiller-token"] = id };

    /// <summary>Null when there is none or it is damaged (a Windows DPAPI file too), left as it is. A file another user
    /// could read or change, or that isn't this user's own regular file, is never read: <paramref name="refused"/> says
    /// why in plain words, and the log has the details.</summary>
    public Kept? Peek(out string? refused)
    {
        refused = null;
        byte[] bytes;
        try
        {
            using var h = Libc.OpenRegular(path);
            if (h == null) return Refuse("it isn't a regular file", "", out refused);
            if (Libc.Owner(h) is not { } owner) return Refuse("it can't be read", "no owner or mode", out refused);
            var (uid, mode) = owner;
            if (uid != Libc.EffectiveUserId) return Refuse("it belongs to another user", $"owner {uid}", out refused);
            if ((mode & GroupOrOther) != 0) return Refuse("other users can read or change it", $"mode {Convert.ToString((int)mode, 8)}", out refused);
            var length = RandomAccess.GetLength(h);
            if (length > MaxBytes) return null;
            bytes = new byte[length];
            for (int n = 0, got; n < bytes.Length; n += got)
                if ((got = RandomAccess.Read(h, bytes.AsSpan(n), n)) == 0) return null;   // shrank while read: a writer replaces it by rename, never in place
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Refuse("it can't be read", e.Message, out refused); }
        try
        {
            return JsonSerializer.Deserialize<Envelope>(bytes) switch
            {
                { Token: { } token } => new(token, null),
                { Keyring: { Length: > 0 } id } => new(null, id),
                _ => null,
            };
        }
        catch (JsonException) { return null; }
    }

    Kept? Refuse(string why, string detail, out string refused)
    {
        Log?.Report($"{Name} isn't used: {why}{(detail.Length > 0 ? $" ({detail})" : "")}");
        refused = why;
        return null;
    }

    /// <summary>The token, read from the keyring when the file only points there. Null when the keyring answered that
    /// it has no such item. Throws <see cref="KeyringUnavailableException"/> when the keyring can't answer now, and
    /// <see cref="KeyringItemUnreadableException"/> when its item holds no token.</summary>
    public async Task<T?> ReadAsync(Kept kept, CancellationToken ct)
    {
        if (kept.Value is { } value) return value;
        KeyringRead read;
        try { read = Keyring == null ? new(KeyringState.Unavailable) : await Keyring.ReadAsync(Attributes(kept.KeyringId!), ct); }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Log?.Report($"{Name}: the system keyring failed ({e.Message})");
            read = new(KeyringState.Unavailable);
        }
        if (read.State == KeyringState.Unavailable) throw new KeyringUnavailableException();
        if (read.State == KeyringState.Missing)
        {
            Log?.Report($"{Name}: its keyring item is gone");
            return null;
        }
        try
        {
            if (read.Secret is { } secret && JsonSerializer.Deserialize<T>(secret, Strict) is { } token) return token;
        }
        catch (JsonException) { }
        Log?.Report($"{Name}: its keyring item holds no token and is left as it is");
        throw new KeyringItemUnreadableException();
    }

    /// <summary>Into the keyring when there is one that takes new tokens and it takes this one, else into the file
    /// (<c>FellBack</c> when such a keyring was there but locked or not running). Nothing is written once <paramref name="ct"/> is cancelled, however long
    /// the keyring took. <paramref name="commit"/> runs right after the write, before another save or delete. The
    /// keyring item it replaces is deleted. Throws when the file can't be written.</summary>
    public async Task<(Kept Kept, bool FellBack)> SaveAsync(T value, CancellationToken ct = default, Action<Kept, bool>? commit = null)
    {
        var store = Keyring is { TakesNew: true };
        var id = store ? await StoreAsync(JsonSerializer.SerializeToUtf8Bytes(value), ct) : null;
        var (kept, fellBack) = (new Kept(value, id), store && id == null);
        string? old = null;
        var written = false;
        try
        {
            await gate.WaitAsync(ct);   // throws once cancelled, however long the keyring or another save took
            try
            {
                old = Peek(out _)?.KeyringId;
                Write(id != null ? new(Keyring: id) : new(Token: value));
                written = true;
                commit?.Invoke(kept, fellBack);
            }
            finally { gate.Release(); }
        }
        finally
        {
            if (!written && id != null) await ForgetAsync(id);   // nothing points at it
            if (written && old != null && old != id) await ForgetAsync(old);
        }
        return (kept, fellBack);
    }

    // {}, or a field missing, null or empty, throws: the item counts as unreadable
    static readonly JsonSerializerOptions Strict = new() { RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, Converters = { new NonEmpty() } };

    sealed class NonEmpty : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader r, Type type, JsonSerializerOptions o) =>
            r.TokenType == JsonTokenType.String && r.GetString() is { Length: > 0 } s ? s : throw new JsonException("an empty string");
        public override void Write(Utf8JsonWriter w, string value, JsonSerializerOptions o) => w.WriteStringValue(value);
    }

    void Write(Envelope e) => AppStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(e), ownerOnly: true);

    async Task<string?> StoreAsync(byte[] secret, CancellationToken ct)
    {
        var id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        try
        {
            if (await Keyring!.StoreAsync(Label, Attributes(id), secret, ct)) return id;
            Log?.Report($"{Name}: the system keyring is locked or not running, so the token is kept in the file (mode 600)");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await ForgetAsync(id);   // it may have landed
            throw;
        }
        catch (Exception e) { Log?.Report($"{Name}: the system keyring failed ({e.Message}), so the token is kept in the file (mode 600)"); }
        return null;
    }

    /// <summary>The keyring item first, then the file: a file left behind points at nothing, which reads as none. The
    /// file stays when it holds another token than <paramref name="kept"/> (saved since). Null: whatever it holds.</summary>
    public async Task DeleteAsync(Kept? kept)
    {
        await gate.WaitAsync();
        try
        {
            var now = Peek(out _);
            if ((kept ?? now)?.KeyringId is { } id) await ForgetAsync(id);
            if (kept == null || now == null || Same(now, kept)) File.Delete(path);
        }
        finally { gate.Release(); }
    }

    async Task ForgetAsync(string id)
    {
        if (await DeleteItemAsync(id)) return;
        Log?.Report($"{Name}: its keyring item couldn't be deleted (the keyring is locked or not running), so the next start tries again");
        ChangeLeft(left => left.Append(id));
    }

    async Task<bool> DeleteItemAsync(string id)
    {
        try { return Keyring != null && await Keyring.DeleteAsync(Attributes(id), CancellationToken.None); }
        catch (Exception) { return false; }
    }

    // <file>.left: the ids of keyring items a delete couldn't reach, which nothing points at any more
    string LeftPath => path + ".left";
    readonly Lock leftLock = new();

    /// <summary>Deletes the keyring items a delete couldn't reach in an earlier run. Never throws.</summary>
    public async Task SweepAsync()
    {
        if (Keyring == null) return;
        List<string> left;
        lock (leftLock) left = Left();
        List<string> gone = [];
        foreach (var id in left)
            if (await DeleteItemAsync(id)) gone.Add(id);
        if (gone.Count == 0) return;
        ChangeLeft(now => now.Except(gone));
        Log?.Report($"{Name}: deleted {gone.Count} keyring item(s) an earlier run couldn't");
    }

    List<string> Left()
    {
        try { return JsonSerializer.Deserialize<List<string>>(File.ReadAllBytes(LeftPath)) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    void ChangeLeft(Func<List<string>, IEnumerable<string>> change)
    {
        lock (leftLock)
            try
            {
                List<string> ids = [.. change(Left()).Distinct()];
                if (ids.Count > 0) AppStore.WriteAtomic(LeftPath, JsonSerializer.SerializeToUtf8Bytes(ids), ownerOnly: true);
                else File.Delete(LeftPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log?.Report($"{Name}: couldn't note the keyring items left behind ({e.Message})"); }
    }
}
