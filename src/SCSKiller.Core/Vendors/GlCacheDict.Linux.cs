using System.Buffers.Binary;
using System.Collections.Concurrent;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace SCSKiller.Core.Vendors;

/// <summary>Re-encodes entries of NVIDIA's cache (<see cref="GlCacheFile"/>) stored as plain zstd under one dictionary at one
/// level, framed as the driver frames its own: content size, no checksum, the dictionary's id. The dictionary's tables are
/// built once and shared by every worker's context.</summary>
sealed unsafe class GlCacheDict : IDisposable
{
    readonly ZSTD_CDict_s* _cdict;
    readonly ZSTD_DDict_s* _ddict;
    readonly ConcurrentBag<Worker> _idle = [];
    readonly List<Worker> _all = [];

    public uint Id { get; }
    public int Level { get; }

    public GlCacheDict(ReadOnlySpan<byte> dictionary, int level)
    {
        Id = GlCacheFile.DictionaryId(dictionary) ?? throw new InvalidDataException("the dictionary isn't a zstd dictionary");
        Level = level;
        fixed (byte* d = dictionary)
        {
            _cdict = Methods.ZSTD_createCDict(d, (nuint)dictionary.Length, level);
            _ddict = Methods.ZSTD_createDDict(d, (nuint)dictionary.Length);
        }
        if (_cdict == null || _ddict == null || Methods.ZSTD_getDictID_fromCDict(_cdict) != Id)
        {
            Dispose();
            throw new InvalidDataException("the dictionary doesn't load");
        }
    }

    /// <summary>A worker's contexts, kept from one batch to the next.</summary>
    public Worker Take() => _idle.TryTake(out var w) ? w : Add(new Worker(this));

    public void Give(Worker w) => _idle.Add(w);

    Worker Add(Worker w)
    {
        lock (_all) _all.Add(w);
        return w;
    }

    public void Dispose()
    {
        foreach (var w in _all) w.Dispose();
        if (_cdict != null) Methods.ZSTD_freeCDict(_cdict);
        if (_ddict != null) Methods.ZSTD_freeDDict(_ddict);
    }

    /// <summary>A zstd dictionary of <paramref name="capacity"/> bytes at most, trained from <paramref name="samples"/> for
    /// <paramref name="level"/> on <paramref name="threads"/> threads, which start with the calling thread's priority.</summary>
    public static byte[] Train(IEnumerable<byte[]> samples, int capacity, int level, int threads)
    {
        var p = new ZDICT_fastCover_params_t { d = 8, steps = 4, nbThreads = (uint)threads, zParams = new ZDICT_params_t { compressionLevel = level } };
        try { return DictBuilder.TrainFromBufferFastCover(samples, p, capacity).ToArray(); }
        catch (ZstdException e) { throw new InvalidDataException($"a dictionary couldn't be trained from the warm's entries: {e.Message}"); }
    }

    /// <summary>One thread's compression and decompression contexts and buffers.</summary>
    public sealed class Worker : IDisposable
    {
        readonly GlCacheDict _owner;
        readonly ZSTD_CCtx_s* _cctx;
        readonly ZSTD_DCtx_s* _dctx;
        readonly Decompressor _plain = new();
        byte[] _raw = [], _payload = [], _check = [];

        internal Worker(GlCacheDict owner)
        {
            _owner = owner;
            _cctx = Methods.ZSTD_createCCtx();
            _dctx = Methods.ZSTD_createDCtx();
            if (_cctx == null || _dctx == null)
            {
                Dispose();
                throw new OutOfMemoryException("zstd contexts");
            }
            Set(ZSTD_cParameter.ZSTD_c_contentSizeFlag, 1);
            Set(ZSTD_cParameter.ZSTD_c_checksumFlag, 0);
            Set(ZSTD_cParameter.ZSTD_c_dictIDFlag, 1);
            Set(ZSTD_cParameter.ZSTD_c_experimentalParam4, 1);   // ZSTD_c_forceAttachDict: each frame reads the shared tables rather than a copy (measured faster, same size)
            Ok(Methods.ZSTD_CCtx_refCDict(_cctx, owner._cdict));
        }

        void Set(ZSTD_cParameter p, int value) => Ok(Methods.ZSTD_CCtx_setParameter(_cctx, p, value));

        static nuint Ok(nuint code) => Methods.ZSTD_isError(code) ? throw new InvalidDataException($"zstd: {Methods.ZSTD_getErrorName(code)}") : code;

        /// <summary>A stored payload (u32 size, one plain zstd frame) decoded to exactly the bytes its u32 says; false when it
        /// doesn't decode so.</summary>
        public bool Raw(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> raw)
        {
            raw = default;
            if (payload.Length < 4 || BinaryPrimitives.ReadUInt32LittleEndian(payload) is var size && size > int.MaxValue / 2) return false;
            if (_raw.Length < size) _raw = new byte[Math.Max(size, 2L * _raw.Length)];
            var into = _raw.AsSpan(0, (int)size);
            try
            {
                if (_plain.Unwrap(payload[4..], into) != size) return false;
            }
            catch (ZstdException) { return false; }
            raw = into;
            return true;
        }

        /// <summary>The entry of <paramref name="key"/> holding <paramref name="raw"/> encoded under the dictionary: the u32
        /// size, then the frame, decoded back before it is returned.</summary>
        public byte[] Entry(ReadOnlySpan<byte> key, ReadOnlySpan<byte> raw)
        {
            var bound = 4 + (long)Compressor.GetCompressBound(raw.Length);
            if (_payload.Length < bound) _payload = new byte[Math.Max(bound, 2L * _payload.Length)];
            BinaryPrimitives.WriteUInt32LittleEndian(_payload, (uint)raw.Length);
            nuint n;
            fixed (byte* src = raw)
            fixed (byte* dst = _payload)
                n = Ok(Methods.ZSTD_compress2(_cctx, dst + 4, (nuint)(_payload.Length - 4), src, (nuint)raw.Length));
            var payload = _payload.AsSpan(0, 4 + (int)n);
            if (GlCacheFile.FrameOf(payload) is not { Checksum: false } f || f.DictId != _owner.Id || f.ContentSize != raw.Length || f.Length != (long)n)
                throw new InvalidDataException("an encoded entry isn't one frame of the dictionary");
            if (_check.Length < raw.Length) _check = new byte[Math.Max(raw.Length, 2L * _check.Length)];
            nuint back;
            fixed (byte* src = payload)
            fixed (byte* dst = _check)
                back = Methods.ZSTD_decompress_usingDDict(_dctx, dst, (nuint)_check.Length, src + 4, n, _owner._ddict);
            if (Methods.ZSTD_isError(back) || back != (nuint)raw.Length || !_check.AsSpan(0, raw.Length).SequenceEqual(raw))
                throw new InvalidDataException("an encoded entry doesn't decode back to its bytes");
            return GlCacheFile.Entry(key, payload);
        }

        public void Dispose()
        {
            if (_cctx != null) Methods.ZSTD_freeCCtx(_cctx);
            if (_dctx != null) Methods.ZSTD_freeDCtx(_dctx);
            _plain.Dispose();
        }
    }
}
