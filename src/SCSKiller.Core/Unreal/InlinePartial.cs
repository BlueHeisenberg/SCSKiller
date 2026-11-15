using System.Text;
using System.Text.Json;

namespace SCSKiller.Core.Unreal;

/// <summary>games\&lt;id&gt;\inline.partial: the packages an inline read (<see cref="UnrealReader.IndexInline"/>) has finished, so a
/// read that is stopped partway goes on after them next time instead of starting over. On disk only, never held between
/// reads: a key line (the engine and every package's place in the paks), then one JSON line per finished package with what
/// it added. A different key starts the file over, and the read that finishes deletes it. Never an index by itself.</summary>
sealed class InlinePartial : IDisposable
{
    public const string FileName = "inline.partial";
    static readonly JsonSerializerOptions Json = new() { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };   // one line per package

    public sealed record Map(string Hash, string Library, List<string> Shaders);
    public sealed record At(string Sha1, char Format, int Offset);
    public sealed record Wide(string Sha1, ResourceCounts Counts, int Vote);

    /// <summary>One finished package: <see cref="Id"/> its place in the paks (a patch pak's copy of a path is another one).
    /// <see cref="Stamp"/> null: nothing carved (no shader map owner, or unreadable).</summary>
    public sealed record Package(string Id, string Path, string? Stamp = null, long Read = 0, bool Unparsed = false, bool Unreadable = false, int Undecoded = 0,
        int Bad = 0, List<Map>? Maps = null, List<At>? Where = null, List<ShaderInfo>? Shaders = null, List<Wide>? Wide = null);

    readonly string path;
    FileStream? file;

    InlinePartial(string path, FileStream? file) => (this.path, this.file) = (path, file);

    /// <summary>The packages a read with <paramref name="key"/> finished, each given to <paramref name="each"/>, and the file
    /// opened to add more. A file of another key is started over; one that can't be read or written: none, and nothing kept.</summary>
    public static InlinePartial Open(string path, string key, Action<Package> each)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var f = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            try
            {
                var kept = Load(f, key, each);
                f.SetLength(kept);
                f.Position = kept;
                if (kept == 0) Line(f, Encoding.UTF8.GetBytes(key));
                return new(path, f);
            }
            catch
            {
                f.Dispose();
                throw;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(path, null); }
    }

    /// <returns>the length of the file up to its last whole line of <paramref name="key"/>; 0 for another key</returns>
    static long Load(FileStream f, string key, Action<Package> each)
    {
        using var r = new StreamReader(f, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        if (r.ReadLine() != key) return 0;
        long kept = Encoding.UTF8.GetByteCount(key) + 1;
        for (var line = r.ReadLine(); line != null; line = r.ReadLine())
        {
            Package? p;
            try { p = JsonSerializer.Deserialize<Package>(line, Json); }
            catch (JsonException) { break; }   // the last line of a read that ended while writing it
            if (p == null) break;
            var end = kept + Encoding.UTF8.GetByteCount(line) + 1;
            if (end > f.Length) break;   // no newline after it: not known to be whole
            each(p);
            kept = end;
        }
        return kept;
    }

    static void Line(FileStream f, byte[] bytes)
    {
        f.Write(bytes);
        f.WriteByte((byte)'\n');
        f.Flush();
    }

    public void Add(Package p)
    {
        if (file == null) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(p, Json);
        lock (this)
            try { if (file != null) Line(file, bytes); }
            catch (IOException) { Close(); }   // a full disk: the read goes on without its checkpoint
    }

    /// <summary>The read finished: its index replaces the checkpoint.</summary>
    public void Done()
    {
        Close();
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }   // a stale key starts it over next time
    }

    void Close()
    {
        lock (this)
        {
            file?.Dispose();
            file = null;
        }
    }

    public void Dispose() => Close();
}
