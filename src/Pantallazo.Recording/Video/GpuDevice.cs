using System.Runtime.CompilerServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace Pantallazo.Recording;

/// <summary>
/// Dispositivo D3D11 compartido por la captura y el encoder (BGRA + video, multithread protected).
/// Se crea sobre el adaptador que maneja el monitor elegido.
/// </summary>
internal sealed class GpuDevice : IDisposable
{
    public ID3D11Device Device { get; }
    public ID3D11DeviceContext Context { get; }
    public IDXGIAdapter1 Adapter { get; }
    public long Luid { get; }
    public string AdapterName { get; }
    public string DriverVersion { get; }
    public uint VendorId { get; }
    public uint DeviceId { get; }
    public uint SubsystemId { get; }
    private IDirect3DDevice? _winrt;

    private GpuDevice(IDXGIAdapter1 adapter)
    {
        Adapter = adapter;
        var desc = adapter.Description1;
        AdapterName = desc.Description.Trim();
        VendorId = desc.VendorId;
        DeviceId = desc.DeviceId;
        SubsystemId = desc.SubsystemId;
        Luid = LuidToLong(desc.Luid);
        DriverVersion = ReadDriverVersion(adapter);

        FeatureLevel[] levels = [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];
        var flags = DeviceCreationFlags.BgraSupport | DeviceCreationFlags.VideoSupport;
        var hr = D3D11.D3D11CreateDevice(adapter.NativePointer, DriverType.Unknown, flags, levels, out var device, out var context);
        if (hr.Failure || device == null || context == null)
            throw new InvalidOperationException($"No se pudo crear el dispositivo gráfico en «{AdapterName}» (0x{hr.Code:X8}).");
        Device = device;
        Context = context;
        using (var mt = Device.QueryInterface<ID3D11Multithread>())
            mt.SetMultithreadProtected(true);
    }

    /// <summary>Dispositivo WinRT para Windows Graphics Capture.</summary>
    public IDirect3DDevice WinRtDevice => _winrt ??= WgcInterop.CreateWinRtDevice(Device);

    public string CacheKey => $"{VendorId:X4}:{DeviceId:X4}:{SubsystemId:X8}|{DriverVersion}|{AdapterName}";

    public static string LuidText(long luid) => luid.ToString("X16");

    /// <summary>Adaptador cuyo salida es el monitor indicado; si no se encuentra, el primero (con <paramref name="found"/> = false).</summary>
    public static GpuDevice CreateForMonitor(nint hmonitor, out bool found)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? first = null;
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if (IsSoftwareAdapter(adapter)) { adapter.Dispose(); continue; }
            bool match = false;
            for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
            {
                using (output)
                    if (output.Description.Monitor == hmonitor) match = true;
            }
            if (match)
            {
                first?.Dispose();
                found = true;
                return CreateOwning(adapter);
            }
            if (first == null) first = adapter; else adapter.Dispose();
        }
        found = false;
        if (first == null) throw new InvalidOperationException("No se encontró ninguna placa de video.");
        return CreateOwning(first);
    }

    public static GpuDevice CreateForLuid(long luid)
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if (LuidToLong(adapter.Description1.Luid) == luid) return CreateOwning(adapter);
            adapter.Dispose();
        }
        throw new InvalidOperationException($"No se encontró la placa de video {LuidText(luid)}.");
    }

    /// <summary>Adaptador al que está conectado cada monitor (para avisar si hay más de uno).</summary>
    public static Dictionary<nint, long> MonitorAdapters()
    {
        var map = new Dictionary<nint, long>();
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            using (adapter)
            {
                long luid = LuidToLong(adapter.Description1.Luid);
                for (uint j = 0; adapter.EnumOutputs(j, out var output).Success; j++)
                    using (output) map.TryAdd(output.Description.Monitor, luid);
            }
        }
        return map;
    }

    private static GpuDevice CreateOwning(IDXGIAdapter1 adapter)
    {
        try { return new GpuDevice(adapter); }
        catch { adapter.Dispose(); throw; }
    }

    private static bool IsSoftwareAdapter(IDXGIAdapter1 a)
        => (a.Description1.Flags & AdapterFlags.Software) != 0;

    private static long LuidToLong<T>(T luid) where T : unmanaged
    {
        // Luid = { uint LowPart; int HighPart } (8 bytes)
        return Unsafe.As<T, long>(ref luid);
    }

    private static string ReadDriverVersion(IDXGIAdapter1 adapter)
    {
        try
        {
            if (adapter.CheckInterfaceSupport(typeof(IDXGIDevice), out long v))
                return $"{(v >> 48) & 0xFFFF}.{(v >> 32) & 0xFFFF}.{(v >> 16) & 0xFFFF}.{v & 0xFFFF}";
        }
        catch { }
        return "?";
    }

    public void Dispose()
    {
        if (_winrt is IDisposable d) { try { d.Dispose(); } catch { } }
        _winrt = null;
        try { Context.ClearState(); Context.Flush(); } catch { }
        Context.Dispose();
        Device.Dispose();
        Adapter.Dispose();
    }
}
