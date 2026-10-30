using System.Runtime.InteropServices;

namespace SCSKiller.Core.Planning;

public static unsafe partial class RootSig
{
    static readonly delegate* unmanaged<void*, nint*, nint*, int> SerializeFn = (delegate* unmanaged<void*, nint*, nint*, int>)NativeLibrary.GetExport(
        NativeLibrary.Load(Path.Combine(Environment.SystemDirectory, "d3d12.dll")), "D3D12SerializeVersionedRootSignature"); // System32: never a proxy d3d12.dll next to the app

    internal static partial byte[] Call(byte* desc)
    {
        nint blob = 0, err = 0;
        var hr = SerializeFn(desc, &blob, &err);
        if (err != 0) Release(err);
        if (hr < 0) throw new SerializeException($"D3D12SerializeVersionedRootSignature failed 0x{hr:x8}");
        var vt = *(nint**)blob; // ID3DBlob: GetBufferPointer = slot 3, GetBufferSize = slot 4
        var bytes = new ReadOnlySpan<byte>(((delegate* unmanaged<nint, void*>)vt[3])(blob), (int)((delegate* unmanaged<nint, nuint>)vt[4])(blob)).ToArray();
        Release(blob);
        return bytes;
        static void Release(nint unk) => ((delegate* unmanaged<nint, uint>)(*(nint**)unk)[2])(unk);
    }
}
