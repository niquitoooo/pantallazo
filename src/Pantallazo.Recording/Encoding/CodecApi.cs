using System.Runtime.InteropServices;

namespace Pantallazo.Recording;

/// <summary>Acceso mínimo a ICodecAPI (Vortice no lo trae) por su tabla virtual.</summary>
internal static unsafe class CodecApi
{
    private const ushort VT_UI4 = 19;

    /// <summary>ICodecAPI::SetValue(Api, VT_UI4). Devuelve el HRESULT.</summary>
    public static int SetUInt32(nint codecApi, Guid api, uint value)
    {
        // VARIANT: vt (2) + 3×reservado (6) + unión (8/16). 24 bytes alcanza en x64.
        byte* variant = stackalloc byte[24];
        new Span<byte>(variant, 24).Clear();
        *(ushort*)variant = VT_UI4;
        *(uint*)(variant + 8) = value;
        var vtbl = *(nint**)codecApi;
        // ICodecAPI: [3] IsSupported [4] IsModifiable [5] GetParameterRange [6] GetParameterValues
        //            [7] GetDefaultValue [8] GetValue [9] SetValue
        var setValue = (delegate* unmanaged[Stdcall]<nint, Guid*, void*, int>)vtbl[9];
        return setValue(codecApi, &api, variant);
    }

    /// <summary>QueryInterface(ICodecAPI) sobre un objeto COM; 0 si no lo soporta.</summary>
    public static nint Query(nint unknown)
    {
        if (unknown == 0) return 0;
        Guid iid = MfGuids.IID_ICodecAPI;
        return Marshal.QueryInterface(unknown, in iid, out nint p) >= 0 ? p : 0;
    }
}
