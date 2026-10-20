using System.Diagnostics;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice.MediaFoundation;

namespace Pantallazo.Recording;

/// <summary>
/// Lleva los cuadros de WGC al <see cref="Mp4Writer"/>.
///
/// Hardware (ritmo constante): el tiempo se divide en ranuras de 1/fps desde t0. Cada cuadro nuevo se convierte a NV12
/// en una sola pasada (<see cref="Nv12Pool"/>, directo desde la textura de WGC si hay un solo monitor) y va a su
/// ranura; las ranuras en las que no llegó nada nuevo (pantalla quieta, o la placa no dio abasto) se llenan repitiendo
/// el último NV12, que no cuesta conversión. Así el archivo queda exactamente a los fps pedidos. Con varios monitores
/// se compone primero en un lienzo BGRA.
///
/// Software: como antes, cuadros variables: el lienzo se convierte a NV12 y se baja a memoria
/// (<see cref="Nv12Converter"/>), con la rejilla de aceptación y un cuadro de mantenimiento por segundo.
/// </summary>
internal sealed class VideoPipeline : IDisposable
{
    private const long TicksPerSecond = 10_000_000;
    private const int HardwarePoolSize = 8;
    private const int SoftwareMaxPending = 6;   // cuadros en memoria esperando al encoder por software
    private const int MaxPendingForRepeats = 24; // con más cola que esto no se agregan repeticiones (el encoder va atrás)

    private readonly GpuDevice _gpu;
    private readonly VideoStreamConfig _cfg;
    private readonly Mp4Writer _writer;
    private long _t0;
    private readonly long _frameDuration;
    private readonly object _gate = new();
    private readonly ID3D11Texture2D _canvas;
    private readonly RectI _canvasRect;
    private readonly Nv12Converter? _nv12Software;
    private readonly Nv12Pool? _nv12Pool;
    private readonly bool _direct;   // hardware con un solo monitor que cubre todo el lienzo: sin pasar por el lienzo
    private readonly List<(MonitorCapture Capture, MonitorPlacement Placement)> _captures = [];
    private readonly FrameGrid _grid;
    private Thread? _pacer;
    private volatile bool _stopping;
    private bool _dirty;
    private long _lastEmitTicks = long.MinValue;   // software: QPC (100 ns) del último cuadro emitido
    private long _lastSampleTime = -1;
    private long _lastSlot = -1;                   // hardware: última ranura escrita
    private ID3D11Texture2D? _lastNv12;            // hardware: último cuadro (con una referencia propia)
    private long _pausedTicks;                     // tiempo total en pausa (100 ns): se descuenta de todos los tiempos
    private long _pauseAt;                         // QPC (100 ns) de la pausa en curso; 0 = grabando
    private long _arrived, _emitted, _dropped, _repeated;
    private int _lost;
    private bool _disposed;

    public long FramesArrived => Interlocked.Read(ref _arrived);
    /// <summary>Cuadros nuevos escritos (sin contar repeticiones).</summary>
    public long FramesEmitted => Interlocked.Read(ref _emitted);
    public long FramesDropped => Interlocked.Read(ref _dropped);
    /// <summary>Ranuras llenadas repitiendo el cuadro anterior (solo hardware).</summary>
    public long FramesRepeated => Interlocked.Read(ref _repeated);

    /// <summary>Un monitor se desconectó o cambió de resolución (una vez).</summary>
    public event Action<string>? Lost;

    /// <summary>Pausa: los cuadros que lleguen se ignoran y no se rellenan ranuras.</summary>
    public void Pause(long atTicks)
    {
        lock (_gate) { if (_pauseAt == 0) _pauseAt = Math.Max(1, atTicks); }
    }

    /// <summary>Reanuda: el tiempo en pausa se descuenta, así el video sigue sin hueco desde donde quedó.</summary>
    public void Resume(long atTicks)
    {
        lock (_gate)
        {
            if (_pauseAt == 0) return;
            _pausedTicks += Math.Max(0, atTicks - _pauseAt);
            _pauseAt = 0;
            _dirty = false;   // lo que quedó en el lienzo es de antes de la pausa
        }
    }

    /// <summary>Tiempo de grabación (sin las pausas) de un instante QPC en 100 ns.</summary>
    private long Active(long t) => t - _pausedTicks;

    public VideoPipeline(GpuDevice gpu, RectI canvas, IReadOnlyList<MonitorPlacement> placements,
        VideoStreamConfig cfg, bool captureCursor, Mp4Writer writer)
    {
        _gpu = gpu;
        _cfg = cfg;
        _writer = writer;
        _canvasRect = canvas;
        _frameDuration = TicksPerSecond / cfg.Fps;
        _grid = new FrameGrid(cfg.Fps);
        try
        {
            _canvas = CreateBgra(canvas.Width, canvas.Height);
            using (var rtv = gpu.Device.CreateRenderTargetView(_canvas))
                gpu.Context.ClearRenderTargetView(rtv, new Color4(0, 0, 0, 1));

            if (cfg.Software)
            {
                _nv12Software = new Nv12Converter(gpu, canvas.Width, canvas.Height, cfg.OutputWidth, cfg.OutputHeight, cfg.Fps);
            }
            else
            {
                _direct = placements.Count == 1 && placements[0].DestX == 0 && placements[0].DestY == 0 &&
                          placements[0].Source.Width == canvas.Width && placements[0].Source.Height == canvas.Height;
                int maxW = Math.Max(canvas.Width, placements.Max(p => p.Display.Width));
                int maxH = Math.Max(canvas.Height, placements.Max(p => p.Display.Height));
                _nv12Pool = new Nv12Pool(gpu, maxW, maxH, cfg.OutputWidth, cfg.OutputHeight, cfg.Fps, HardwarePoolSize);
                RecordingLog.Write($"Video por hardware: NV12 en una pasada, {(_direct ? "directo desde la captura" : "por el lienzo")}, ritmo constante {cfg.Fps} fps");
            }

            foreach (var p in placements)
            {
                var cap = new MonitorCapture(gpu, p.Display, captureCursor);
                cap.FrameArrived += OnFrame;
                cap.Lost += OnLost;
                _captures.Add((cap, p));
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private ID3D11Texture2D CreateBgra(int w, int h) => _gpu.Device.CreateTexture2D(new Texture2DDescription(
        Format.B8G8R8A8_UNorm, (uint)w, (uint)h, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource,
        ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));

    /// <summary>Arranca las capturas; <paramref name="t0Ticks"/> (QPC en 100 ns) es el cero del archivo.</summary>
    public void Start(long t0Ticks)
    {
        _t0 = t0Ticks;
        foreach (var (cap, _) in _captures) cap.Start();
        _pacer = new Thread(PacerLoop) { IsBackground = true, Name = "Pantallazo pacer", Priority = ThreadPriority.AboveNormal };
        _pacer.Start();
    }

    public static long NowTicks()
    {
        long qpc = Stopwatch.GetTimestamp();
        return (long)(qpc * ((double)TicksPerSecond / Stopwatch.Frequency));
    }

    // ------------------------------------------------------------------ entrada de cuadros

    private void OnFrame(MonitorCapture cap, ID3D11Texture2D tex, long t)
    {
        if (_stopping) return;
        Interlocked.Increment(ref _arrived);
        var placement = _captures.First(c => ReferenceEquals(c.Capture, cap)).Placement;
        var src = placement.Source;
        int w = Math.Min(src.Width, cap.Width - src.X);
        int h = Math.Min(src.Height, cap.Height - src.Y);
        if (w <= 0 || h <= 0) return;
        lock (_gate)
        {
            if (_stopping || _disposed || _pauseAt != 0) return;
            if (_nv12Pool != null)
            {
                long slot = SlotOf(t);
                // Un monitor solo y ranura libre: se convierte directo desde la textura de WGC (sin copias).
                if (_direct && slot > _lastSlot &&
                    EmitHardwareLocked(tex, new Vortice.RawRect(src.X, src.Y, src.X + w, src.Y + h), slot))
                    return;
                CopyToCanvas(tex, placement, src, w, h);
                if (slot > _lastSlot) EmitHardwareLocked(_canvas, CanvasRect, slot);
                return;
            }
            CopyToCanvas(tex, placement, src, w, h);
            if (_grid.WouldAccept(Active(t))) EmitSoftwareLocked(Active(t), force: false);
        }
    }

    private void CopyToCanvas(ID3D11Texture2D tex, MonitorPlacement placement, RectI src, int w, int h)
    {
        _gpu.Context.CopySubresourceRegion(_canvas, 0, (uint)placement.DestX, (uint)placement.DestY, 0, tex, 0,
            new Box(src.X, src.Y, 0, src.X + w, src.Y + h, 1));
        _dirty = true;
    }

    private Vortice.RawRect CanvasRect => new(0, 0, _canvasRect.Width, _canvasRect.Height);

    private void OnLost(MonitorCapture cap, string reason)
    {
        if (Interlocked.Exchange(ref _lost, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ => Lost?.Invoke(reason));
    }

    // ------------------------------------------------------------------ marcapasos

    private void PacerLoop()
    {
        int sleepMs = Math.Clamp(1000 / _cfg.Fps / 2, 1, 15);
        while (!_stopping)
        {
            Thread.Sleep(sleepMs);
            try
            {
                lock (_gate)
                {
                    if (_stopping || _disposed) break;
                    if (_pauseAt != 0) continue;
                    long now = NowTicks();
                    if (_nv12Pool != null) PaceHardwareLocked(now);
                    else PaceSoftwareLocked(Active(now));
                }
            }
            catch (Exception ex)
            {
                RecordingLog.Write($"Marcapasos de video: {ex.Message}");
            }
        }
    }

    private void PaceHardwareLocked(long now)
    {
        long cur = SlotOf(now);
        if (_dirty && cur > _lastSlot)
        {
            // Llegó un cambio cuya ranura ya estaba usada (o el pool estaba lleno): va en la ranura actual.
            EmitHardwareLocked(_canvas, CanvasRect, cur);
            return;
        }
        // Nada nuevo: se completan las ranuras ya vencidas con el último cuadro (la actual queda para uno nuevo).
        if (!_dirty) FillUpToLocked(cur - 1);
    }

    private void PaceSoftwareLocked(long now)
    {
        if (_dirty)
        {
            if (_grid.WouldAccept(now)) EmitSoftwareLocked(now, force: false);
        }
        else if (_lastEmitTicks != long.MinValue && now - _lastEmitTicks >= TicksPerSecond)
        {
            _dirty = true;   // cuadro de mantenimiento con la pantalla quieta
            EmitSoftwareLocked(now, force: false);
        }
    }

    // ------------------------------------------------------------------ hardware (ritmo constante)

    /// <summary>Ranura de un tiempo QPC: floor((t − t0)·fps + 0,5).</summary>
    private long SlotOf(long t)
    {
        long dt = Math.Max(0, Active(t) - _t0);
        return (2 * dt * _cfg.Fps + TicksPerSecond) / (2 * TicksPerSecond);
    }

    private long SlotTime(long slot) => slot * TicksPerSecond / _cfg.Fps;

    /// <summary>Convierte <paramref name="input"/> y lo escribe en <paramref name="slot"/>, llenando antes los huecos. Requiere _gate.</summary>
    private bool EmitHardwareLocked(ID3D11Texture2D input, Vortice.RawRect source, long slot)
    {
        var tex = _nv12Pool!.Convert(input, source);
        if (tex == null)
        {
            Interlocked.Increment(ref _dropped);
            return false;   // el encoder no da abasto; la ranura se llena con una repetición
        }
        FillUpToLocked(slot - 1);
        WriteNv12Locked(tex, slot);
        tex.AddRef();
        _lastNv12?.Release();
        _lastNv12 = tex;
        _lastSlot = slot;
        _dirty = false;
        Interlocked.Increment(ref _emitted);
        return true;
    }

    /// <summary>Repite el último cuadro en las ranuras (último+1 … <paramref name="slot"/>). Requiere _gate.</summary>
    private void FillUpToLocked(long slot)
    {
        if (_lastNv12 == null || slot <= _lastSlot) return;
        if (_writer.PendingVideo > MaxPendingForRepeats) return;   // se reintenta en el próximo tic
        for (long s = _lastSlot + 1; s <= slot; s++)
        {
            WriteNv12Locked(_lastNv12, s);
            Interlocked.Increment(ref _repeated);
        }
        _lastSlot = slot;
    }

    private void WriteNv12Locked(ID3D11Texture2D tex, long slot)
    {
        var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, tex, 0, false);
        IMFSample sample;
        try
        {
            using (var b2 = buffer.QueryInterface<IMF2DBuffer>())
                buffer.CurrentLength = b2.ContiguousLength;   // nace en 0: sin esto WriteSample da E_INVALIDARG
            sample = MediaFactory.MFCreateSample();
            sample.AddBuffer(buffer);
        }
        finally { buffer.Dispose(); }
        sample.SampleTime = SlotTime(slot);
        sample.SampleDuration = SlotTime(slot + 1) - SlotTime(slot);
        _writer.WriteVideo(sample);
    }

    // ------------------------------------------------------------------ software (cuadros variables)

    /// <summary>Emite el lienzo con tiempo QPC <paramref name="t"/>. Requiere _gate.</summary>
    private bool EmitSoftwareLocked(long t, bool force)
    {
        if (!force && !_grid.WouldAccept(t)) return false;
        long sampleTime = Math.Max(0, t - _t0);
        if (sampleTime <= _lastSampleTime) sampleTime = _lastSampleTime + 1;
        if (!force && _writer.PendingVideo >= SoftwareMaxPending)
        {
            Interlocked.Increment(ref _dropped);
            return false;   // el encoder no da abasto: se descarta (queda _dirty)
        }
        var sample = _nv12Software!.Convert(_canvas);
        if (!force) _grid.TryAccept(t);
        sample.SampleTime = sampleTime;
        sample.SampleDuration = _frameDuration;
        _writer.WriteVideo(sample);
        _lastSampleTime = sampleTime;
        _lastEmitTicks = t;
        _dirty = false;
        Interlocked.Increment(ref _emitted);
        return true;
    }

    // ------------------------------------------------------------------ cierre

    /// <summary>Corta las capturas y completa el video hasta <paramref name="stopTicks"/> (si ya hubo algún cuadro).</summary>
    public void Stop(long stopTicks)
    {
        _stopping = true;
        foreach (var (cap, _) in _captures)
        {
            cap.FrameArrived -= OnFrame;
            cap.Dispose();
        }
        _pacer?.Join(1000);
        lock (_gate)
        {
            if (_disposed || _emitted == 0) return;
            if (_pauseAt != 0) stopTicks = _pauseAt;   // detenido en pausa: el video termina donde se pausó
            if (_nv12Pool != null)
            {
                // Ritmo constante hasta el final: la duración del video cubre toda la grabación.
                long last = SlotOf(stopTicks);
                for (int attempt = 0; attempt < 100 && _lastSlot < last; attempt++)
                {
                    FillUpToLocked(last);
                    if (_lastSlot < last) Monitor.Wait(_gate, 10);
                }
                return;
            }
            stopTicks = Active(stopTicks);
            if (stopTicks - _lastEmitTicks < _frameDuration / 2) return;
            for (int attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    if (EmitSoftwareLocked(stopTicks, force: true)) return;
                }
                catch (Exception ex)
                {
                    RecordingLog.Write($"Cuadro final: {ex.Message}");
                    return;
                }
                Monitor.Wait(_gate, 10);
            }
        }
    }

    public void Dispose()
    {
        _stopping = true;
        foreach (var (cap, _) in _captures)
        {
            try { cap.Dispose(); } catch { }
        }
        _pacer?.Join(1000);
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _lastNv12?.Release();
            _lastNv12 = null;
            _nv12Software?.Dispose();
            _nv12Pool?.Dispose();
            _canvas?.Dispose();
        }
    }
}
