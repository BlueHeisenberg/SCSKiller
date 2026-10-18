using System.IO.Compression;
using System.Runtime.Intrinsics.X86;
using System.Text;
using CUE4Parse.Compression;
using SCSKiller.Core.App;
using SCSKiller.Core.Unreal;

namespace SCSKiller.Tests.Platform;

/// <summary>The pinned Oodle DLL runs AVX2 and BMI2 code unchecked: it loads only on a CPU that has both, and Oodle data
/// decodes either way. CI runs this class a second time with DOTNET_EnableAVX2=0, which turns both off for .NET.</summary>
public class CodecCpuTests
{
    internal static readonly byte[] Text = Encoding.ASCII.GetBytes(string.Join(" ", Enumerable.Range(0, 300).Select(i => $"shader{i * 7 % 23}")));

    internal const string Kraken = "jAYAAFmIAFdzaGFkZXIwIAAAIjcxNDIxNTEyOTMxMDc4MTI2MTI0MTE4MjkxIHNoYWRlcjAAABgQFdYWVVoVVVoZVBXZGVVZGVWaFVUVgfwAAAMBDQ0AAAH/CwQ1gEY=";

    /// <summary>Shader code no codec reads names the CPU only for an Oodle block where native Oodle is off; any other
    /// failure keeps its own error.</summary>
    [Fact]
    public void Undecodable_shader_code_names_the_cpu_only_for_oodle_data_without_native_oodle()
    {
        Codecs.Load();
        var oodle = Assert.Throws<InvalidDataException>(() => UnrealReader.Decompress(Convert.FromBase64String(Kraken)[..40], Text.Length));   // cut short
        Assert.StartsWith("cannot decompress", (oodle.InnerException ?? oodle).Message);
        Assert.Equal(!Codecs.NativeOodle ? Codecs.NoNativeOodle : null, Codecs.Reason(oodle));
        var other = Assert.Throws<InvalidDataException>(() => UnrealReader.Decompress([0x78, 0x9C, 1, 2, 3, 4, 5, 6], 64));
        Assert.Null(Codecs.Reason(other));
    }

    // Text compressed by the pinned Oodle DLL (Optimal2)
    [Theory]
    [InlineData(Kraken)]
    [InlineData("jAoAAGWIAGNzaGFkZXIwIAAAJzcxNDIxNTIxOTMwNzgyNjMwNDExODk2c2hhZGVyMTYgc2hhZGVyMAAAGLC5OrpBubrBwblAwUDBQcG5wbm6QMGJAQcACQAaADwARQAaADwAxQAAAAD89wE=")]   // Mermaid
    [InlineData("jAoAAGaIAGRzaGFkZXIwIAAAKDcxNDIxNTIxOTMwNzgyNjMwNDExODk2IHNoYWRlcjE2IHNoYWRlcjAAABiwuTq6Qbm6wcG5QMFAwUHBucG5ukDBggEHAAkAGgA8AEUAGgA8AMUAAAAA/PcB")]   // Selkie
    [InlineData("jAwAAFiIAFZzaGFkZXIwIICAAwgZJAAAAf8AACI3MTQyMTUxMjkzMTA3ODEyNjEyNDExODI5MSBzaGFkZXIwAAAYBA31FS02DS02DiUN7g4tLg4tVg0tDUjn6CHMVA==")]   // Leviathan
    public void Native_oodle_loads_only_with_avx2_and_bmi2_and_oodle_and_zlib_data_decode_either_way(string oodle)
    {
        Codecs.Load();
        Assert.Equal(Avx2.IsSupported && Bmi2.X64.IsSupported, OodleHelper.Instance != null);
        Assert.Equal(Text, Compression.Decompress(Convert.FromBase64String(oodle), Text.Length, CompressionMethod.Oodle));
        var zlib = new MemoryStream();
        using (var z = new ZLibStream(zlib, CompressionLevel.Optimal, leaveOpen: true)) z.Write(Text);
        Assert.Equal(Text, Compression.Decompress(zlib.ToArray(), Text.Length, CompressionMethod.Zlib));
    }
}
