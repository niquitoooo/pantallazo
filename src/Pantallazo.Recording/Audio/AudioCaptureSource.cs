using System.Diagnostics;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace Pantallazo.Recording;

/// <summary>
/// Una fuente WASAPI compartida (loopback de una salida o captura de un micrófono) convertida a 48 kHz estéreo
/// float y volcada a su <see cref="AudioTimeline"/>. Si el dispositivo se pierde, deja de escribir (el mezclador
/// pone silencio) y avisa por <see cref="Failed"/>.
/// </summary>
internal sealed class AudioCaptureSource : IDisposable
{
    private static readonly Guid SubtypePcm = new("00000001-0000-0010-8000-00aa00389b71");
    private static readonly Guid SubtypeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    private readonly MMDevice _device;
    private readonly WasapiCapture _capture;
    private readonly AudioConverter _converter;
    private readonly object _originLock = new();
    private long _originQpc;
    private int _failed;
    private bool _disposed;

    public string Id { get; }
    public string Name { get; }
    public bool IsLoopback { get; }
    public string DeviceId => _device.ID;
    public AudioTimeline Timeline { get; }
    public bool HasFailed => Volatile.Read(ref _failed) != 0;

    /// <summary>Se dispara (una vez) si el dispositivo deja de entregar audio por un error.</summary>
    public event Action<AudioCaptureSource, Exception?>? Failed;

    private AudioCaptureSource(string id, MMDevice device, bool loopback, bool buffering)
    {
        Id = id;
        _device = device;
        IsLoopback = loopback;
        Name = AudioDeviceCatalog.SafeName(device);
        Timeline = new AudioTimeline(buffering);
        _capture = loopback ? new WasapiLoopbackCapture(device) : new WasapiCapture(device, true, 100);
        _converter = new AudioConverter(ToSourceFormat(_capture.WaveFormat));
        _capture.DataAvailable += OnData;
        _capture.RecordingStopped += OnStopped;
        _originQpc = Stopwatch.GetTimestamp();
        var f = _converter.Format;
        RecordingLog.Write($"Audio «{Name}» ({(loopback ? "loopback" : "micrófono")}): {f.SampleRate} Hz, {f.Channels} canales, {f.Kind}");
    }

    private long _bytesReceived;
    private float _rawPeak;
    public long BytesReceived => Interlocked.Read(ref _bytesReceived);

    /// <summary>Abre el loopback de la salida predeterminada (rol Multimedia).</summary>
    public static AudioCaptureSource OpenSystem(bool buffering)
    {
        using var en = new MMDeviceEnumerator();
        if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
            throw new InvalidOperationException("No hay una salida de audio predeterminada.");
        var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        try { return new AudioCaptureSource(AudioSourceIds.System, dev, loopback: true, buffering); }
        catch { dev.Dispose(); throw; }
    }

    public static AudioCaptureSource OpenMicrophone(string deviceId, bool buffering)
    {
        using var en = new MMDeviceEnumerator();
        MMDevice dev;
        try { dev = en.GetDevice(deviceId); }
        catch (Exception ex) { throw new InvalidOperationException("El micrófono no está conectado.", ex); }
        try
        {
            if (dev.State != DeviceState.Active) throw new InvalidOperationException($"El micrófono «{AudioDeviceCatalog.SafeName(dev)}» no está disponible.");
            return new AudioCaptureSource(deviceId, dev, loopback: false, buffering);
        }
        catch { dev.Dispose(); throw; }
    }

    public void Start() => _capture.StartRecording();

    /// <summary>true si el dispositivo está silenciado en Windows (WASAPI entrega ceros).</summary>
    public bool IsMutedAtEndpoint
    {
        get { try { return _device.AudioEndpointVolume.Mute; } catch { return false; } }
    }

    /// <summary>
    /// Fija el origen de la línea de tiempo (t0 de la grabación, en ticks de QPC) y vacía el buffer. Al reanudar una
    /// pausa, el origen se corre lo que duró la pausa y la escritura sigue en <paramref name="position"/>.
    /// </summary>
    public void SetOrigin(long originQpc, long position = 0)
    {
        lock (_originLock)
        {
            _originQpc = originQpc;
            Timeline.Reset(position);
        }
    }

    private void OnData(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0) return;
        long now = Stopwatch.GetTimestamp();
        Interlocked.Add(ref _bytesReceived, e.BytesRecorded);
        try
        {
            lock (_originLock)
            {
                var stereo = _converter.Convert(e.Buffer.AsSpan(0, e.BytesRecorded));
                foreach (var v in stereo) { float a = Math.Abs(v); if (a > _rawPeak) _rawPeak = a; }
                long arrivalFrame = (long)((now - _originQpc) * (double)AudioTimeline.Rate / Stopwatch.Frequency);
                Timeline.Write(stereo, arrivalFrame);
            }
        }
        catch (Exception ex)
        {
            RecordingLog.Write($"Audio «{Name}»: error al convertir: {ex.Message}");
        }
    }

    private void OnStopped(object? sender, StoppedEventArgs e)
    {
        if (_disposed || e.Exception == null) return;
        if (Interlocked.Exchange(ref _failed, 1) != 0) return;
        RecordingLog.Write($"Audio «{Name}» se detuvo: {e.Exception.Message}");
        Failed?.Invoke(this, e.Exception);
    }

    internal static SourceFormat ToSourceFormat(WaveFormat wf)
    {
        SampleKind kind;
        if (wf.Encoding == WaveFormatEncoding.IeeeFloat) kind = SampleKind.Float32;
        else if (wf.Encoding == WaveFormatEncoding.Extensible && wf is WaveFormatExtensible ext && ext.SubFormat == SubtypeFloat)
            kind = SampleKind.Float32;
        else if (wf.Encoding == WaveFormatEncoding.Pcm ||
                 (wf.Encoding == WaveFormatEncoding.Extensible && (wf is not WaveFormatExtensible e2 || e2.SubFormat == SubtypePcm)))
            kind = wf.BitsPerSample switch
            {
                16 => SampleKind.Int16,
                24 => SampleKind.Int24,
                32 => SampleKind.Int32,
                _ => throw new NotSupportedException($"Formato de audio no soportado ({wf.BitsPerSample} bits)."),
            };
        else throw new NotSupportedException($"Formato de audio no soportado ({wf.Encoding}).");
        return new SourceFormat(wf.SampleRate, wf.Channels, kind);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        RecordingLog.Write($"Audio «{Name}»: recibió {BytesReceived} bytes, pico {_rawPeak:0.0000}");
        try { _capture.DataAvailable -= OnData; } catch { }
        try { _capture.StopRecording(); } catch { }
        try { _capture.Dispose(); } catch (Exception ex) { RecordingLog.Write($"Audio «{Name}»: al cerrar: {ex.Message}"); }
        try { _device.Dispose(); } catch { }
    }
}
