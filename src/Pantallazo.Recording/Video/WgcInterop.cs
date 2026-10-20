using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;

namespace Pantallazo.Recording;

/// <summary>Puentes COM entre Windows Graphics Capture (WinRT) y D3D11 (Vortice).</summary>
internal static unsafe class WgcInterop
{
    private static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    private static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    private static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int RoGetActivationFactory(nint activatableClassId, in Guid iid, out nint factory);

    [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out nint hstring);

    [DllImport("combase.dll", PreserveSig = true)]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("d3d11.dll", PreserveSig = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, out nint graphicsDevice);

    public static bool IsSupported
    {
        get { try { return GraphicsCaptureSession.IsSupported(); } catch { return false; } }
    }

    public static GraphicsCaptureItem CreateItemForMonitor(nint hmonitor)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out nint hs));
        nint factory = 0;
        try
        {
            Marshal.ThrowExceptionForHR(RoGetActivationFactory(hs, IID_IGraphicsCaptureItemInterop, out factory));
            // IGraphicsCaptureItemInterop: [3] CreateForWindow, [4] CreateForMonitor
            var vtbl = *(nint**)factory;
            var createForMonitor = (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtbl[4];
            nint itemPtr;
            Guid iid = IID_IGraphicsCaptureItem;
            int hr = createForMonitor(factory, hmonitor, &iid, &itemPtr);
            if (hr < 0) throw new InvalidOperationException($"No se pudo preparar la captura del monitor (0x{hr:X8}).");
            try { return GraphicsCaptureItem.FromAbi(itemPtr); }
            finally { Marshal.Release(itemPtr); }
        }
        finally
        {
            if (factory != 0) Marshal.Release(factory);
            WindowsDeleteString(hs);
        }
    }

    public static IDirect3DDevice CreateWinRtDevice(ID3D11Device device)
    {
        using var dxgi = device.QueryInterface<IDXGIDevice>();
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi.NativePointer, out nint inspectable));
        try { return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable); }
        finally { Marshal.Release(inspectable); }
    }

    private static readonly Guid IID_IGraphicsCaptureSession5 = new("67c0ea62-1f85-5061-925a-239be0ac09cb");

    /// <summary>
    /// GraphicsCaptureSession.MinUpdateInterval (Windows 11 24H2+). No está en la proyección del SDK 22621, así que
    /// se llama por la tabla virtual de IGraphicsCaptureSession5. Sin esto WGC entrega como máximo ~60 cuadros/s.
    /// </summary>
    public static bool TrySetMinUpdateInterval(GraphicsCaptureSession session, TimeSpan interval)
    {
        nint unk = ((WinRT.IWinRTObject)session).NativeObject.ThisPtr;
        Guid iid = IID_IGraphicsCaptureSession5;
        if (Marshal.QueryInterface(unk, in iid, out nint s5) < 0) return false;
        try
        {
            // IInspectable (6) → [6] get_MinUpdateInterval, [7] put_MinUpdateInterval(TimeSpan = int64 en 100 ns)
            var vtbl = *(nint**)s5;
            var put = (delegate* unmanaged[Stdcall]<nint, long, int>)vtbl[7];
            return put(s5, interval.Ticks) >= 0;
        }
        finally { Marshal.Release(s5); }
    }

    /// <summary>Textura D3D11 detrás de una superficie de WGC. Hay que liberarla (Dispose) al terminar.</summary>
    public static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        nint unk = ((WinRT.IWinRTObject)surface).NativeObject.ThisPtr;
        Guid accessIid = IID_IDirect3DDxgiInterfaceAccess;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in accessIid, out nint access));
        try
        {
            // IDirect3DDxgiInterfaceAccess: [3] GetInterface(REFIID, void**)
            var vtbl = *(nint**)access;
            var getInterface = (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtbl[3];
            Guid texIid = IID_ID3D11Texture2D;
            nint tex;
            Marshal.ThrowExceptionForHR(getInterface(access, &texIid, &tex));
            return new ID3D11Texture2D(tex);
        }
        finally { Marshal.Release(access); }
    }
}
