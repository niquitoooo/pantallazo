using Vortice.Direct3D11;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Pantallazo.Recording;

/// <summary>
/// Una sesión de Windows Graphics Capture sobre un monitor. Entrega cada cuadro como textura D3D11 (válida solo
/// durante el callback) con su marca de tiempo QPC en unidades de 100 ns.
/// </summary>
internal sealed class MonitorCapture : IDisposable
{
    private readonly GraphicsCaptureItem _item;
    private readonly Direct3D11CaptureFramePool _pool;
    private readonly GraphicsCaptureSession _session;
    private readonly int _width, _height;
    private int _lost;
    private volatile bool _disposed;

    public DisplayInfo Display { get; }
    public int Width => _width;
    public int Height => _height;

    /// <summary>(captura, textura, tiempo QPC en 100 ns). Se llama en un hilo del pool.</summary>
    public event Action<MonitorCapture, ID3D11Texture2D, long>? FrameArrived;
    /// <summary>El monitor se desconectó o cambió de resolución. Se dispara una vez.</summary>
    public event Action<MonitorCapture, string>? Lost;

    public MonitorCapture(GpuDevice gpu, DisplayInfo display, bool captureCursor)
    {
        Display = display;
        _item = WgcInterop.CreateItemForMonitor(display.Handle);
        var size = _item.Size;
        _width = size.Width;
        _height = size.Height;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(gpu.WinRtDevice,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, size);
        _session = _pool.CreateCaptureSession(_item);
        try { _session.IsCursorCaptureEnabled = captureCursor; } catch { }
        try { _session.IsBorderRequired = false; } catch { RecordingLog.Write("No se pudo quitar el borde amarillo de captura."); }
        try
        {
            if (!WgcInterop.TrySetMinUpdateInterval(_session, TimeSpan.FromMilliseconds(1)))
                RecordingLog.Write("MinUpdateInterval no disponible: WGC puede limitar a ~60 cuadros/s.");
        }
        catch (Exception ex) { RecordingLog.Write($"MinUpdateInterval: {ex.Message}"); }
        _pool.FrameArrived += OnFrameArrived;
        _item.Closed += OnItemClosed;
    }

    public void Start() => _session.StartCapture();

    private void OnItemClosed(GraphicsCaptureItem sender, object args) => RaiseLost("El monitor se desconectó.");

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        if (_disposed) return;
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = sender.TryGetNextFrame();
            if (frame == null) return;
            var content = frame.ContentSize;
            if (content.Width != _width || content.Height != _height)
            {
                RaiseLost($"Cambió la resolución del monitor ({_width}×{_height} → {content.Width}×{content.Height}).");
                return;
            }
            long t = frame.SystemRelativeTime.Ticks;
            using var tex = WgcInterop.GetTexture(frame.Surface);
            FrameArrived?.Invoke(this, tex, t);
        }
        catch (Exception ex)
        {
            if (!_disposed) RecordingLog.Write($"Captura de {Display.Id}: {ex.Message}");
        }
        finally
        {
            frame?.Dispose();
        }
    }

    private void RaiseLost(string reason)
    {
        if (_disposed || Interlocked.Exchange(ref _lost, 1) != 0) return;
        RecordingLog.Write($"Captura de {Display.Id}: {reason}");
        Lost?.Invoke(this, reason);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _pool.FrameArrived -= OnFrameArrived; } catch { }
        try { _item.Closed -= OnItemClosed; } catch { }
        try { _session.Dispose(); } catch { }
        try { _pool.Dispose(); } catch { }
    }
}
