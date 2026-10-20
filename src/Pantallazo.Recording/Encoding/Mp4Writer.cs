using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Vortice.MediaFoundation;

namespace Pantallazo.Recording;

/// <param name="InputWidth">Tamaño de lo que se entrega (lienzo en hardware; ya escalado en software).</param>
/// <param name="Software">true = encoder por software: entrada NV12 en memoria del sistema, sin D3D.</param>
/// <summary>
/// <paramref name="GpuNv12"/>: por hardware, los cuadros llegan ya escalados y en NV12 (texturas de <see cref="Nv12Pool"/>);
/// si no, por hardware llegan en BGRA y el sink writer convierte y escala.
/// </summary>
internal sealed record VideoStreamConfig(int InputWidth, int InputHeight, int OutputWidth, int OutputHeight,
    int Fps, int Bitrate, bool Software, bool GpuNv12 = false);

/// <summary>
/// IMFSinkWriter → MP4 con H.264 (perfil High si se acepta) y AAC 48 kHz estéreo 192 kbps.
/// Todas las llamadas a WriteSample/Finalize ocurren en un único hilo propio (cola). Throttling desactivado:
/// el límite lo pone el pool de texturas del lado del video.
/// </summary>
internal sealed class Mp4Writer : IDisposable
{
    public const int AudioBytesPerSecond = 192_000 / 8;

    /// <summary>Solo pruebas (RecHarness): simula que el encoder por hardware falla tras N cuadros. -1 = apagado.</summary>
    internal static int SimulateHardwareFaultAfter = -1;

    private readonly BlockingCollection<(int Stream, IMFSample Sample)> _queue = new(new ConcurrentQueue<(int, IMFSample)>());
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _done = new(false);
    private IMFSinkWriter? _writer;
    private IMFDXGIDeviceManager? _manager;
    private int _videoIndex = -1, _audioIndex = -1;
    private int _faulted;
    private bool _abandoned;
    private volatile bool _finalizeRequested;
    private Exception? _finalizeError;
    private long _videoWritten, _audioWritten;

    public string Path { get; }
    public VideoStreamConfig Config { get; }
    public bool HasAudio => _audioIndex >= 0;
    public string? EncoderName { get; private set; }
    public uint Profile { get; private set; }
    public long VideoSamplesWritten => Interlocked.Read(ref _videoWritten);
    public long AudioSamplesWritten => Interlocked.Read(ref _audioWritten);
    public int PendingCount => _queue.Count;
    private int _pendingVideo;
    /// <summary>Cuadros de video encolados que todavía no pasaron por WriteSample.</summary>
    public int PendingVideo => Volatile.Read(ref _pendingVideo);

    /// <summary>Error de WriteSample (una vez). Se dispara en el hilo del escritor.</summary>
    public event Action<Exception>? Faulted;

    public Mp4Writer(string path, VideoStreamConfig cfg, GpuDevice? gpu, bool withAudio)
    {
        Path = path;
        Config = cfg;
        MfRuntime.EnsureStarted();
        if (!cfg.Software)
        {
            if (gpu == null) throw new ArgumentNullException(nameof(gpu));
            _manager = MediaFactory.MFCreateDXGIDeviceManager();
            _manager.ResetDevice(gpu.Device).CheckError();
        }

        Exception? last = null;
        foreach (uint profile in new[] { MfGuids.H264ProfileHigh, MfGuids.H264ProfileMain, 0u })
        {
            try
            {
                Create(profile, withAudio);
                Profile = profile;
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                RecordingLog.Write($"Sink writer con perfil {profile} falló: {ex.Message}");
                DisposeWriter();
                TryDeleteFile(path);
            }
        }
        if (last != null)
        {
            _manager?.Dispose();
            throw new InvalidOperationException($"No se pudo iniciar el encoder de video: {last.Message}", last);
        }

        _thread = new Thread(WriterLoop) { IsBackground = true, Name = "Pantallazo MP4 writer" };
        _thread.Start();
    }

    private void Create(uint profile, bool withAudio)
    {
        var cfg = Config;
        using var attrs = MediaFactory.MFCreateAttributes(6);
        attrs.Set(MfGuids.READWRITE_ENABLE_HARDWARE_TRANSFORMS, cfg.Software ? 0u : 1u);
        attrs.Set(MfGuids.SINK_WRITER_DISABLE_THROTTLING, 1u);
        attrs.Set(MfGuids.LOW_LATENCY, 0u);   // MF_LOW_LATENCY cuelga el encoder AMD: siempre apagado.
        attrs.Set(MfGuids.TRANSCODE_CONTAINERTYPE, MfGuids.TranscodeContainerType_MPEG4);
        if (_manager != null) attrs.Set(MfGuids.SINK_WRITER_D3D_MANAGER, _manager);

        var swc = System.Diagnostics.Stopwatch.StartNew();
        _writer = MediaFactory.MFCreateSinkWriterFromURL(Path, null!, attrs);

        // --- Video ---
        using (var outType = MediaFactory.MFCreateMediaType())
        {
            outType.Set(MfGuids.MT_MAJOR_TYPE, MfGuids.MediaTypeVideo);
            outType.Set(MfGuids.MT_SUBTYPE, MfGuids.VideoH264);
            outType.Set(MfGuids.MT_AVG_BITRATE, (uint)cfg.Bitrate);
            outType.Set(MfGuids.MT_FRAME_SIZE, MfGuids.Pack(cfg.OutputWidth, cfg.OutputHeight));
            outType.Set(MfGuids.MT_FRAME_RATE, MfGuids.Pack(cfg.Fps, 1));
            outType.Set(MfGuids.MT_PIXEL_ASPECT_RATIO, MfGuids.Pack(1, 1));
            outType.Set(MfGuids.MT_INTERLACE_MODE, MfGuids.InterlaceProgressive);
            if (profile != 0) outType.Set(MfGuids.MT_MPEG2_PROFILE, profile);
            _videoIndex = _writer.AddStream(outType);
        }
        using (var inType = MediaFactory.MFCreateMediaType())
        {
            inType.Set(MfGuids.MT_MAJOR_TYPE, MfGuids.MediaTypeVideo);
            bool nv12 = cfg.Software || cfg.GpuNv12;
            inType.Set(MfGuids.MT_SUBTYPE, nv12 ? MfGuids.VideoNV12 : MfGuids.VideoARGB32);
            inType.Set(MfGuids.MT_FRAME_SIZE, MfGuids.Pack(cfg.InputWidth, cfg.InputHeight));
            inType.Set(MfGuids.MT_FRAME_RATE, MfGuids.Pack(cfg.Fps, 1));
            inType.Set(MfGuids.MT_PIXEL_ASPECT_RATIO, MfGuids.Pack(1, 1));
            inType.Set(MfGuids.MT_INTERLACE_MODE, MfGuids.InterlaceProgressive);
            inType.Set(MfGuids.MT_ALL_SAMPLES_INDEPENDENT, 1u);
            // Texturas de arriba hacia abajo: stride positivo (si no, ARGB32 se toma invertido).
            inType.Set(MfGuids.MT_DEFAULT_STRIDE, (uint)(nv12 ? cfg.InputWidth : cfg.InputWidth * 4));
            _writer.SetInputMediaType(_videoIndex, inType, null!);
        }

        RecordingLog.Write($"SetInputMediaType del video (inicializa el encoder): {swc.ElapsedMilliseconds} ms");
        ConfigureVideoTransforms();

        // --- Audio ---
        if (withAudio)
        {
            using (var outType = MediaFactory.MFCreateMediaType())
            {
                outType.Set(MfGuids.MT_MAJOR_TYPE, MfGuids.MediaTypeAudio);
                outType.Set(MfGuids.MT_SUBTYPE, MfGuids.AudioAAC);
                outType.Set(MfGuids.MT_AUDIO_SAMPLES_PER_SECOND, 48000u);
                outType.Set(MfGuids.MT_AUDIO_NUM_CHANNELS, 2u);
                outType.Set(MfGuids.MT_AUDIO_BITS_PER_SAMPLE, 16u);
                outType.Set(MfGuids.MT_AUDIO_AVG_BYTES_PER_SECOND, (uint)AudioBytesPerSecond);
                outType.Set(MfGuids.MT_AAC_PAYLOAD_TYPE, 0u);
                _audioIndex = _writer.AddStream(outType);
            }
            using (var inType = MediaFactory.MFCreateMediaType())
            {
                inType.Set(MfGuids.MT_MAJOR_TYPE, MfGuids.MediaTypeAudio);
                inType.Set(MfGuids.MT_SUBTYPE, MfGuids.AudioPCM);
                inType.Set(MfGuids.MT_AUDIO_SAMPLES_PER_SECOND, 48000u);
                inType.Set(MfGuids.MT_AUDIO_NUM_CHANNELS, 2u);
                inType.Set(MfGuids.MT_AUDIO_BITS_PER_SAMPLE, 16u);
                inType.Set(MfGuids.MT_AUDIO_BLOCK_ALIGNMENT, 4u);
                inType.Set(MfGuids.MT_AUDIO_AVG_BYTES_PER_SECOND, 48000u * 4);
                inType.Set(MfGuids.MT_ALL_SAMPLES_INDEPENDENT, 1u);
                _writer.SetInputMediaType(_audioIndex, inType, null!);
            }
        }

        _writer.BeginWriting();
        RecordingLog.Write($"MP4: {Config.InputWidth}×{Config.InputHeight} → {Config.OutputWidth}×{Config.OutputHeight} " +
                           $"@{Config.Fps}, {Config.Bitrate / 1_000_000.0:0.#} Mbps, perfil {profile}, " +
                           $"{(Config.Software ? "software" : "hardware")}, encoder «{EncoderName ?? "?"}», audio {(withAudio ? "sí" : "no")}");
    }

    /// <summary>Encoder: nombre y bitrate por CODECAPI. Procesador de video: sin FRC (no rellenar cuadros).</summary>
    private void ConfigureVideoTransforms()
    {
        IMFSinkWriterEx? ex = null;
        try { ex = _writer!.QueryInterfaceOrNull<IMFSinkWriterEx>(); } catch { }
        if (ex == null)
        {
            RecordingLog.Write("IMFSinkWriterEx no disponible: no se pudo ajustar el encoder.");
            return;
        }
        using (ex)
        {
            for (int i = 0; i < 8; i++)
            {
                Guid category;
                IMFTransform transform;
                try { ex.GetTransformForStream(_videoIndex, i, out category, out transform); }
                catch { break; }
                using (transform)
                {
                    if (category == MfGuids.MFT_CATEGORY_VIDEO_ENCODER)
                    {
                        EncoderName = ReadFriendlyName(transform);
                        ApplyBitrate(transform);
                    }
                    else if (category == MfGuids.MFT_CATEGORY_VIDEO_PROCESSOR)
                    {
                        try
                        {
                            using var a = transform.Attributes;
                            a.Set(MfGuids.XVP_DISABLE_FRC, 1u);
                        }
                        catch (Exception e) { RecordingLog.Write($"No se pudo desactivar FRC: {e.Message}"); }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Nombre del encoder elegido. Los MFT por hardware no siempre traen MFT_FRIENDLY_NAME en sus atributos, pero
    /// sí su URL de hardware, que se busca entre los activadores. Sin URL es un encoder por software.
    /// </summary>
    internal static string? ReadFriendlyName(IMFTransform transform)
    {
        IMFAttributes? a = null;
        try { a = transform.Attributes; } catch { }
        if (a == null) return null;
        using (a)
        {
            try
            {
                string? name = a.GetAllocatedString(MfGuids.MFT_FRIENDLY_NAME);
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }
            catch { }
            string? url = null;
            try { url = a.GetAllocatedString(MfGuids.MFT_ENUM_HARDWARE_URL); } catch { }
            if (string.IsNullOrWhiteSpace(url)) return "H264 Encoder MFT (software)";
            return EncoderProbe.HardwareEncoderNameByUrl(url) ?? $"hardware ({url})";
        }
    }

    private void ApplyBitrate(IMFTransform encoder)
    {
        nint api = CodecApi.Query(encoder.NativePointer);
        if (api == 0)
        {
            RecordingLog.Write("El encoder no expone ICodecAPI: se usa solo MF_MT_AVG_BITRATE.");
            return;
        }
        try
        {
            int b = Config.Bitrate;
            int hr = CodecApi.SetUInt32(api, MfGuids.CODECAPI_AVEncCommonRateControlMode, MfGuids.RateControl_PeakConstrainedVBR);
            bool peak = hr >= 0;
            if (!peak) hr = CodecApi.SetUInt32(api, MfGuids.CODECAPI_AVEncCommonRateControlMode, MfGuids.RateControl_UnconstrainedVBR);
            int hrMean = CodecApi.SetUInt32(api, MfGuids.CODECAPI_AVEncCommonMeanBitRate, (uint)b);
            int hrMax = peak ? CodecApi.SetUInt32(api, MfGuids.CODECAPI_AVEncCommonMaxBitRate, (uint)Math.Min((long)b * 3 / 2, 90_000_000)) : 0;
            int hrGop = CodecApi.SetUInt32(api, MfGuids.CODECAPI_AVEncMPVGOPSize, (uint)Math.Max(1, Config.Fps * 2));
            RecordingLog.Write($"CODECAPI: modo {(peak ? "VBR con pico" : "VBR")} 0x{hr:X8}, media 0x{hrMean:X8}, máx 0x{hrMax:X8}, GOP 0x{hrGop:X8}");
        }
        finally { Marshal.Release(api); }
    }

    public void WriteVideo(IMFSample sample)
    {
        Interlocked.Increment(ref _pendingVideo);
        if (!Enqueue(_videoIndex, sample)) Interlocked.Decrement(ref _pendingVideo);
    }

    public void WriteAudio(IMFSample sample)
    {
        if (_audioIndex < 0) { sample.Dispose(); return; }
        Enqueue(_audioIndex, sample);
    }

    private bool Enqueue(int stream, IMFSample sample)
    {
        bool ok = false;
        try { ok = !_finalizeRequested && Volatile.Read(ref _faulted) == 0 && _queue.TryAdd((stream, sample)); }
        catch (InvalidOperationException) { }   // CompleteAdding en carrera con el cierre
        if (!ok) sample.Dispose();
        return ok;
    }

    private void WriterLoop()
    {
        try
        {
            foreach (var (stream, sample) in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (Volatile.Read(ref _faulted) == 0)
                    {
                        if (stream == _videoIndex && !Config.Software && SimulateHardwareFaultAfter >= 0 && VideoSamplesWritten >= SimulateHardwareFaultAfter)
                            throw new System.Runtime.InteropServices.COMException("falla simulada del encoder", unchecked((int)0x8007000E));
                        _writer!.WriteSample(stream, sample);
                        if (stream == _videoIndex) Interlocked.Increment(ref _videoWritten);
                        else Interlocked.Increment(ref _audioWritten);
                    }
                }
                catch (Exception ex)
                {
                    if (Interlocked.Exchange(ref _faulted, 1) == 0)
                    {
                        RecordingLog.Write($"WriteSample falló ({(stream == _videoIndex ? "video" : "audio")}): {ex.Message}");
                        try { Faulted?.Invoke(ex); } catch { }
                    }
                }
                finally
                {
                    if (stream == _videoIndex) Interlocked.Decrement(ref _pendingVideo);
                    sample.Dispose();
                }
            }
            if (_finalizeRequested && Volatile.Read(ref _faulted) == 0 && VideoSamplesWritten > 0)
            {
                try { _writer!.Finalize(); }
                catch (Exception ex) { _finalizeError = ex; RecordingLog.Write($"Finalize falló: {ex.Message}"); }
            }
        }
        finally
        {
            _done.Set();
        }
    }

    public bool IsFaulted => Volatile.Read(ref _faulted) != 0;

    /// <summary>
    /// Escribe lo pendiente y cierra el MP4. false si no terminó a tiempo (el escritor queda abandonado).
    /// <paramref name="error"/> trae el error de Finalize o de escritura, si hubo.
    /// </summary>
    public bool Finish(TimeSpan timeout, out Exception? error)
    {
        _finalizeRequested = true;
        _queue.CompleteAdding();
        if (!_done.Wait(timeout))
        {
            _abandoned = true;
            error = null;
            return false;
        }
        error = _finalizeError ?? (IsFaulted ? new IOException("El encoder falló durante la grabación.") : null);
        return true;
    }

    /// <summary>Corta sin cerrar el archivo (falla del encoder: se descarta el temporal).</summary>
    public void Abort()
    {
        Interlocked.Exchange(ref _faulted, 1);
        _queue.CompleteAdding();
        if (!_done.Wait(TimeSpan.FromSeconds(5))) _abandoned = true;
    }

    private void DisposeWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null;
        _videoIndex = _audioIndex = -1;
    }

    internal static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (!_queue.IsAddingCompleted) Abort();
        if (_abandoned) return;   // el hilo sigue colgado adentro de MF: no tocar nada
        while (_queue.TryTake(out var item)) item.Sample.Dispose();
        DisposeWriter();
        try { _manager?.Dispose(); } catch { }
        _manager = null;
        _queue.Dispose();
    }
}

internal static class MfRuntime
{
    private static int _started;

    public static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 0)
            MediaFactory.MFStartup(false).CheckError();
    }
}
