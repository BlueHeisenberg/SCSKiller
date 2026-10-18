using SCSKiller.Core.Planning;

namespace SCSKiller.Core.Carved;

/// <summary>Several engine readers as one, tried in order. Detect: the first result that can be indexed, or that is
/// encrypted with a fork a plan rule names (its key makes it compile, so it wins over the carver), else the first reader's
/// that recognized the game (its reason is the most specific, e.g. Unreal's "encrypted" over the carver's "packed").
/// Index and ReadShaders go to the reader of <see cref="EngineInfo.Family"/>, so an EngineInfo cached from an earlier scan
/// still finds its reader. A reader listed under several families (the carver) is tried once.</summary>
public sealed class EngineReaders(params (string Family, IEngineReader Reader)[] readers) : IEngineReader
{
    public T? Get<T>() where T : class, IEngineReader => readers.Select(r => r.Reader).OfType<T>().FirstOrDefault();

    public EngineInfo? Detect(Game game)
    {
        EngineInfo? first = null;
        Exception? error = null;
        foreach (var reader in Distinct)
        {
            EngineInfo? e;
            try { e = reader.Detect(game); }
            catch (Exception ex) { error ??= ex; continue; } // one reader choking on the game's files doesn't stop the next
            if (e == null) continue;
            if (e.Unsupported == null && !e.Encrypted || e.Encrypted && RulesFile.Current.RuleFor(e)?.Match.Fork != null) return e;
            first ??= e;
        }
        if (first == null && error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
        return first;
    }

    IEnumerable<IEngineReader> Distinct => readers.Select(r => r.Reader).Distinct();

    public string IndexStamp(Game game) => string.Concat(Distinct.Select(r => r.IndexStamp(game)));

    public string DetectStamp(Game game, EngineInfo? engine) => engine == null ? string.Concat(Distinct.Select(r => r.DetectStamp(game, null)))
        : readers.FirstOrDefault(r => r.Family == engine.Family).Reader?.DetectStamp(game, engine) ?? "";

    public int ReaderVersion(EngineInfo engine) => readers.FirstOrDefault(r => r.Family == engine.Family).Reader?.ReaderVersion(engine) ?? 1;

    IEngineReader Of(EngineInfo e) => readers.FirstOrDefault(r => r.Family == e.Family).Reader ?? throw new NotSupportedException($"no reader for {e.Family}");

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, CancellationToken ct) => Of(engine).Index(game, engine, log, ct);

    public ShaderIndex Index(Game game, EngineInfo engine, IProgress<string>? log, IProgress<(int Done, int Total)>? progress, CancellationToken ct) =>
        Of(engine).Index(game, engine, log, progress, ct);

    public void ReadShaders(Game game, EngineInfo engine, IReadOnlySet<string> sha1s, Action<string, byte[]> sink, CancellationToken ct) =>
        Of(engine).ReadShaders(game, engine, sha1s, sink, ct);
}
