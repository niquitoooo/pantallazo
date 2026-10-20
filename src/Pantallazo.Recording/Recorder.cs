using System.Diagnostics;
using NAudio.CoreAudioApi;
using Vortice.MediaFoundation;
using DataFlow = NAudio.CoreAudioApi.DataFlow;
using Role = NAudio.CoreAudioApi.Role;

namespace Pantallazo.Recording;

/// <summary>Datos técnicos de la última grabación (para diagnóstico y RecHarness).</summary>
internal sealed record RecorderDiagnostics(
    string Adapter, string? Encoder, bool Software, int CanvasWidth, int CanvasHeight, int OutputWidth, int OutputHeight,
    int Fps, int Bitrate, long FramesArrived, long FramesEmitted, long FramesDropped, long VideoSamplesWritten,
    long AudioSamplesWritten, IReadOnlyList<string> AudioSources, TimeSpan StartTime, TimeSpan StopTime);

/// <summary>
/// Grabador de pantalla: Windows Graphics Capture → D3D11 → IMFSinkWriter (H.264 + AAC) con audio WASAPI
/// mezclado por reloj. Estados: Idle → Starting → Recording → Stopping → Idle.
/// Los eventos se disparan en orden en un hilo propio del grabador (nunca en el hilo de la UI).
/// </summary>
public sealed class Recorder : IDisposable
{
    // Umbrales de disco (internos y modificables solo para pruebas de RecHarness).
    internal static long MinFreeToStart = 200L * 1024 * 1024;
    internal static long MinFreeWhileRecording = 100L * 1024 * 1024;
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HardwareGracePeriod = TimeSpan.FromSeconds(3);
    private const int AudioLatencyFrames = AudioTimeline.Rate / 10;   // el mezclador va 100 ms detrás del reloj
    private const int AudioChunkFrames = AudioTimeline.Rate / 10;

    private readonly object _lock = new();
    private readonly EventPump _events = new("Pantallazo recorder events");
    private RecorderState _state = RecorderState.Idle;
    private Session? _session;
    private Task? _startTask;
    private Task<RecordingResult>? _stopTask;
    private TimeSpan _finalElapsed;
    private bool _disposed;

    /// <summary>Para pruebas (RecHarness): saltear el sondeo y grabar por software.</summary>
    internal bool ForceSoftwareEncoder { get; set; }
    internal RecorderDiagnostics? LastDiagnostics { get; private set; }

    public RecorderState State { get { lock (_lock) return _state; } }

    public TimeSpan Elapsed
    {
        get
        {
            lock (_lock)
                return _session != null && _state is RecorderState.Recording or RecorderState.Stopping or RecorderState.Paused
                    ? _session.Clock.Elapsed
                    : _finalElapsed;
        }
    }

    /// <summary>Pausa la grabación (no se graba video ni audio hasta <see cref="Resume"/>). false si no estaba grabando.</summary>
    public bool Pause()
    {
        lock (_lock)
        {
            if (_state != RecorderState.Recording || _session == null || _stopTask != null) return false;
            _session.Pause();
            SetStateLocked(RecorderState.Paused);
            return true;
        }
    }

    /// <summary>Reanuda: el archivo sigue sin hueco desde donde se pausó. false si no estaba en pausa.</summary>
    public bool Resume()
    {
        lock (_lock)
        {
            if (_state != RecorderState.Paused || _session == null || _stopTask != null) return false;
            _session.Resume();
            SetStateLocked(RecorderState.Recording);
            return true;
        }
    }

    public event EventHandler<RecorderState>? StateChanged;
    public event EventHandler<IReadOnlyList<AudioLevel>>? LevelsChanged;
    /// <summary>Avisos en castellano (mic desconectado, cuadros perdidos, poco disco, encoder por software…).</summary>
    public event EventHandler<string>? Warning;
    /// <summary>Fin de la grabación, también cuando se detiene sola (disco lleno, monitor desconectado, encoder roto).</summary>
    public event EventHandler<RecordingResult>? Finished;

    // ------------------------------------------------------------------ inicio

    /// <summary>Arranca. Lanza <see cref="InvalidOperationException"/> con mensaje en castellano si no puede.</summary>
    public Task StartAsync(RecordingOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state != RecorderState.Idle) throw new InvalidOperationException("Ya hay una grabación en curso.");
            _stopTask = null;
            _finalElapsed = TimeSpan.Zero;
            SetStateLocked(RecorderState.Starting);
            _startTask = StartCoreAsync(options, ct);
            return _startTask;
        }
    }

    private async Task StartCoreAsync(RecordingOptions options, CancellationToken ct)
    {
        Session? session = null;
        try
        {
            // WASAPI, MF y WGC se preparan en un hilo MTA del pool (el que llama suele ser el hilo STA de la UI).
            session = await Task.Run(() => BuildSession(options, ct), ct).ConfigureAwait(false);
            lock (_lock)
            {
                _session = session;
                SetStateLocked(RecorderState.Recording);
            }
            foreach (var w in session.Warnings.ToArray()) PostWarning(w);
            session.StartWatchdog();
        }
        catch (Exception ex)
        {
            session?.DisposeAll();
            lock (_lock) { _session = null; SetStateLocked(RecorderState.Idle); }
            RecordingLog.Write($"No arrancó la grabación: {ex}");
            if (ex is OperationCanceledException) throw;
            if (ex is RecorderException) throw new InvalidOperationException(ex.Message, ex);
            throw new InvalidOperationException($"No se pudo iniciar la grabación: {ex.Message}", ex);
        }
    }

    private Session BuildSession(RecordingOptions options, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Carpeta y espacio
        string finalPath;
        try { finalPath = Path.GetFullPath(options.OutputPath); }
        catch (Exception ex) { throw new RecorderException($"La ruta del archivo no es válida: {ex.Message}"); }
        string dir = Path.GetDirectoryName(finalPath) ?? throw new RecorderException("La ruta del archivo no es válida.");
        try { Directory.CreateDirectory(dir); }
        catch (Exception ex) { throw new RecorderException($"No se pudo crear la carpeta «{dir}»: {ex.Message}"); }
        long free = FreeSpace(dir);
        if (free >= 0 && free < MinFreeToStart)
            throw new RecorderException($"Queda poco espacio en el disco ({free / (1024 * 1024)} MB libres). Hacen falta al menos {MinFreeToStart / (1024 * 1024)} MB para grabar.");

        if (!WgcInterop.IsSupported)
            throw new RecorderException("Este Windows no permite capturar la pantalla (falta Windows Graphics Capture).");

        // Monitores y lienzo
        var displays = DisplayCatalog.GetDisplays();
        RectI canvas;
        try { canvas = CanvasLayout.ResolveCanvas(options.Target, displays, options.DisplayId, options.Region); }
        catch (ArgumentException ex) { throw new RecorderException(ex.Message); }
        var placements = CanvasLayout.Place(canvas, displays);
        if (placements.Count == 0) throw new RecorderException("El área elegida no toca ningún monitor.");
        ct.ThrowIfCancellationRequested();

        var s = new Session(this, options, finalPath, canvas, placements);
        try
        {
            // Placa de video: la del monitor principal (todos) o la del monitor con más área (monitor/región).
            var anchor = options.Target == CaptureTargetKind.AllDisplays
                ? displays.FirstOrDefault(d => d.IsPrimary) ?? placements[0].Display
                : placements[0].Display;
            s.Gpu = GpuDevice.CreateForMonitor(anchor.Handle, out bool found);
            if (!found) RecordingLog.Write($"No se encontró la placa de {anchor.Id}; se usa «{s.Gpu.AdapterName}».");
            try
            {
                var map = GpuDevice.MonitorAdapters();
                foreach (var p in placements)
                    if (map.TryGetValue(p.Display.Handle, out long luid) && luid != s.Gpu.Luid)
                        s.AddWarning($"«{p.Display.FriendlyName}» está conectado a otra placa de video; se captura igual a través de " +
                                     $"Windows usando «{s.Gpu.AdapterName}», pero puede rendir menos.");
            }
            catch (Exception ex) { RecordingLog.Write($"No se pudo revisar los adaptadores: {ex.Message}"); }

            // Encoder
            bool software;
            if (ForceSoftwareEncoder) { software = true; s.EncoderNote = "forzado a software"; }
            else
            {
                var decision = EncoderProbe.Decide(s.Gpu);
                software = !decision.UseHardware;
                s.EncoderNote = decision.Reason;
                if (software)
                    s.AddWarning($"Se graba con el encoder por software de Windows ({decision.Reason}). " +
                                 "Máximo 1080p y 60 fps; los fps reales pueden ser menores.");
            }
            ct.ThrowIfCancellationRequested();

            RecordingLog.Write($"Placa y encoder decididos en {sw.ElapsedMilliseconds} ms");
            // Audio y video se abren en paralelo (WASAPI ~0,5 s, encoder ~0,2 s): no dependen entre sí y se
            // alinean recién en Begin con el mismo t0. La pista de audio se decide por lo pedido; si ninguna fuente
            // abre, el mezclador escribe silencio (y queda el aviso).
            s.WantsAudio = options.SystemAudio || (options.Microphones ?? []).Any(m => !string.IsNullOrWhiteSpace(m.DeviceId));
            var audioTask = Task.Run(s.OpenAudio);
            try
            {
                // Video + escritor (si el hardware no arranca: una vez por software)
                try
                {
                    s.CreateVideo(software);
                }
                catch (Exception ex) when (!software)
                {
                    RecordingLog.Write($"El encoder por hardware no arrancó: {ex}");
                    EncoderProbe.MarkHardwareFailed(s.Gpu, null, ex.Message);
                    s.AddWarning("El encoder por hardware no arrancó; se graba por software (máximo 1080p y 60 fps).");
                    s.RetriedSoftware = true;
                    s.CreateVideo(software: true);
                }
            }
            catch
            {
                try { audioTask.Wait(); } catch { }
                throw;
            }
            audioTask.Wait();
            RecordingLog.Write($"Audio y encoder listos en {sw.ElapsedMilliseconds} ms");
            ct.ThrowIfCancellationRequested();

            s.StartTime = sw.Elapsed;
            s.Begin();
            RecordingLog.Write($"Grabación iniciada en {sw.ElapsedMilliseconds} ms: lienzo {canvas.Width}×{canvas.Height} en ({canvas.X},{canvas.Y}), " +
                               $"{placements.Count} monitor(es), {(s.Software ? "software" : "hardware")}, audio: {string.Join(", ", s.SourceNames)}");
            return s;
        }
        catch
        {
            s.DisposeAll();
            throw;
        }
    }

    internal static long FreeSpace(string dir)
    {
        try
        {
            var root = Path.GetPathRoot(dir);
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch { return -1; }
    }

    // ------------------------------------------------------------------ detención

    /// <summary>Detiene, cierra el MP4 y lo renombra. Si ya se estaba deteniendo, devuelve el mismo resultado.</summary>
    public async Task<RecordingResult> StopAsync()
    {
        Task? starting;
        Task<RecordingResult>? pending;
        lock (_lock)
        {
            pending = _stopTask;
            starting = _state == RecorderState.Starting ? _startTask : null;
        }
        if (pending != null) return await pending.ConfigureAwait(false);
        if (starting != null)
        {
            try { await starting.ConfigureAwait(false); } catch { }
        }
        Session? s;
        lock (_lock)
        {
            pending = _stopTask;
            s = _session;
        }
        if (pending != null) return await pending.ConfigureAwait(false);
        if (s == null) return new RecordingResult(false, null, "No hay ninguna grabación en curso.", [], TimeSpan.Zero, 0, 0);
        return await BeginStop(s, null).ConfigureAwait(false);
    }

    private Task<RecordingResult> BeginStop(Session s, string? reason)
    {
        lock (_lock)
        {
            if (_stopTask != null) return _stopTask;
            if (!ReferenceEquals(_session, s) || _state is not (RecorderState.Recording or RecorderState.Paused))
                return Task.FromResult(new RecordingResult(false, null, "No hay ninguna grabación en curso.", [], TimeSpan.Zero, 0, 0));
            SetStateLocked(RecorderState.Stopping);
            _stopTask = Task.Run(() => StopCore(s, reason));
            return _stopTask;
        }
    }

    private RecordingResult StopCore(Session s, string? reason)
    {
        RecordingResult result;
        try
        {
            result = s.Stop(reason);
        }
        catch (Exception ex)
        {
            RecordingLog.Write($"Error al detener: {ex}");
            result = new RecordingResult(false, File.Exists(s.TempPath) ? s.TempPath : null,
                $"No se pudo guardar la grabación: {ex.Message}", s.Warnings.ToArray(), s.Clock.Elapsed, s.OutputWidth, s.OutputHeight);
            s.DisposeAll();
        }
        LastDiagnostics = s.Diagnostics();
        lock (_lock)
        {
            _finalElapsed = result.Duration;
            _session = null;
            _stopTask = null;   // los que ya lo tenían reciben este resultado; los siguientes, "no hay grabación"
            SetStateLocked(RecorderState.Idle);
            var finished = Finished;
            _events.Post(() => finished?.Invoke(this, result));
        }
        return result;
    }

    // ------------------------------------------------------------------ eventos

    private void SetStateLocked(RecorderState state)
    {
        if (_state == state) return;
        _state = state;
        var h = StateChanged;
        _events.Post(() => h?.Invoke(this, state));
    }

    private void PostWarning(string text)
    {
        RecordingLog.Write("Aviso: " + text);
        var h = Warning;
        _events.Post(() => h?.Invoke(this, text));
    }

    private void PostLevels(IReadOnlyList<AudioLevel> levels)
    {
        var h = LevelsChanged;
        if (h != null) _events.Post(() => h(this, levels));
    }

    public void Dispose()
    {
        Task<RecordingResult>? pending = null;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try
        {
            if (State is RecorderState.Recording or RecorderState.Starting or RecorderState.Paused) pending = StopAsync();
            else pending = _stopTask;
            pending?.Wait(TimeSpan.FromSeconds(15));
        }
        catch { }
        _events.Dispose();
    }

    // ================================================================== sesión

    /// <summary>Todo lo que vive mientras dura una grabación.</summary>
    private sealed class Session
    {
        private readonly Recorder _owner;
        private readonly object _sync = new();
        private readonly List<AudioCaptureSource> _sources = [];
        private readonly HashSet<string> _warned = [];
        private Thread? _mixerThread;
        private volatile bool _mixerStop;
        private AudioMixer? _mixer;

        /// <summary>Se pidió audio: el MP4 lleva pista aunque ninguna fuente haya abierto (queda en silencio).</summary>
        public bool WantsAudio { get; set; }
        private byte[] _pcm = new byte[AudioChunkFrames * 4];
        private Timer? _watchdog;
        private MMDeviceEnumerator? _notifyEnumerator;
        private AudioDeviceCatalog.NotificationClient? _notifyClient;
        private int _stopRequested;
        private long _lastEmitted, _lastDropped;
        private int _badSeconds;
        private bool _dropWarned;

        public RecordingOptions Options { get; }
        public string FinalPath { get; }
        public string TempPath { get; }
        public RectI Canvas { get; }
        public IReadOnlyList<MonitorPlacement> Placements { get; }
        public GpuDevice Gpu { get; set; } = null!;
        public Mp4Writer? Writer { get; private set; }
        public VideoPipeline? Pipeline { get; private set; }
        public bool Software { get; private set; }
        public bool RetriedSoftware { get; set; }
        public string EncoderNote { get; set; } = "";
        public int OutputWidth { get; private set; }
        public int OutputHeight { get; private set; }
        public int Fps { get; private set; }
        public int Bitrate { get; private set; }
        public long T0Qpc { get; private set; }
        public Stopwatch Clock { get; } = new();
        public List<string> Warnings { get; } = [];
        public TimeSpan StartTime { get; set; }
        public TimeSpan StopTime { get; private set; }
        public IEnumerable<string> SourceNames => _sources.Count == 0 ? ["ninguno"] : _sources.Select(s => $"{s.Id} «{s.Name}»");

        public Session(Recorder owner, RecordingOptions options, string finalPath, RectI canvas, IReadOnlyList<MonitorPlacement> placements)
        {
            _owner = owner;
            Options = options;
            FinalPath = finalPath;
            TempPath = FileNaming.TempPathFor(finalPath);
            Canvas = canvas;
            Placements = placements;
        }

        public void AddWarning(string text)
        {
            lock (Warnings)
            {
                if (!_warned.Add(text)) return;
                Warnings.Add(text);
            }
        }

        /// <summary>Agrega y publica (durante la grabación).</summary>
        public void Warn(string text)
        {
            lock (Warnings)
            {
                if (!_warned.Add(text)) return;
                Warnings.Add(text);
            }
            _owner.PostWarning(text);
        }

        // ---------------- audio

        public void OpenAudio()
        {
            if (Options.SystemAudio)
            {
                try
                {
                    var src = AudioCaptureSource.OpenSystem(buffering: true);
                    _sources.Add(src);
                }
                catch (Exception ex) { AddWarning($"No se pudo abrir el audio del sistema: {ex.Message}"); }
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { AudioSourceIds.System };
            foreach (var mic in Options.Microphones ?? [])
            {
                if (string.IsNullOrWhiteSpace(mic.DeviceId) || !seen.Add(mic.DeviceId)) continue;
                try
                {
                    var src = AudioCaptureSource.OpenMicrophone(mic.DeviceId, buffering: true);
                    _sources.Add(src);
                    if (src.IsMutedAtEndpoint)
                        AddWarning($"El micrófono «{src.Name}» está silenciado en Windows: se va a grabar silencio.");
                }
                catch (Exception ex) { AddWarning($"No se pudo abrir un micrófono: {ex.Message}"); }
            }
            foreach (var src in _sources.ToArray())
            {
                src.Failed += OnSourceFailed;
                try { src.Start(); }
                catch (Exception ex)
                {
                    AddWarning($"No se pudo iniciar «{src.Name}»: {ex.Message}");
                    _sources.Remove(src);
                    src.Dispose();
                }
            }
            if ((Options.SystemAudio || (Options.Microphones?.Count ?? 0) > 0) && _sources.Count == 0)
                AddWarning("No se pudo abrir ninguna fuente de audio: el video se graba sin sonido.");

            if (_sources.Any(s => s.IsLoopback))
            {
                try
                {
                    _notifyEnumerator = new MMDeviceEnumerator();
                    _notifyClient = new AudioDeviceCatalog.NotificationClient(() => { }, OnDefaultDeviceChanged);
                    _notifyEnumerator.RegisterEndpointNotificationCallback(_notifyClient);
                }
                catch (Exception ex) { RecordingLog.Write($"Sin aviso de cambio de salida: {ex.Message}"); }
            }
        }

        private void OnSourceFailed(AudioCaptureSource src, Exception? ex)
        {
            Warn(src.IsLoopback
                ? $"Se perdió el audio del sistema («{src.Name}»): sigue en silencio."
                : $"Se desconectó el micrófono «{src.Name}»: esa fuente sigue en silencio.");
        }

        private void OnDefaultDeviceChanged(DataFlow flow, Role role, string newId)
        {
            if (flow != DataFlow.Render || role != Role.Multimedia) return;
            var sys = _sources.FirstOrDefault(s => s.IsLoopback);
            if (sys == null || string.Equals(sys.DeviceId, newId, StringComparison.OrdinalIgnoreCase)) return;
            Warn($"Cambió la salida de audio predeterminada: se sigue grabando el sonido de «{sys.Name}».");
        }

        private static readonly string[] NoSources = [];

        // ---------------- video

        public void CreateVideo(bool software)
        {
            Software = software;
            Fps = VideoMath.EffectiveFps(Options.Fps, software);
            (OutputWidth, OutputHeight) = VideoMath.OutputSize(Canvas.Width, Canvas.Height, software, Options.MaxOutputHeight);
            // Lo que pidió el usuario (resolución elegida, medidas pares); se avisa solo si el encoder obligó a achicar más.
            var (wantedW, wantedH) = VideoMath.WantedSize(Canvas.Width, Canvas.Height, Options.MaxOutputHeight);
            Bitrate = VideoMath.Bitrate(OutputWidth, OutputHeight, Fps, Options.Quality);
            // Por hardware los cuadros llegan ya escalados y en NV12 (Nv12Pool); por software los escala y convierte Nv12Converter.
            var cfg = new VideoStreamConfig(OutputWidth, OutputHeight, OutputWidth, OutputHeight, Fps, Bitrate, software, GpuNv12: !software);
            if (software && Options.Fps > Fps) AddWarning($"Por software se graba a {Fps} fps como máximo.");
            if (!software && (OutputWidth != wantedW || OutputHeight != wantedH))
                AddWarning($"El área ({Canvas.Width}×{Canvas.Height}) supera lo que acepta el encoder: se graba a {OutputWidth}×{OutputHeight}.");

            Mp4Writer.TryDeleteFile(TempPath);
            Mp4Writer? writer = null;
            try
            {
                writer = new Mp4Writer(TempPath, cfg, software ? null : Gpu, withAudio: WantsAudio);
                writer.Faulted += OnWriterFaulted;
                var pipeline = new VideoPipeline(Gpu, Canvas, Placements, cfg, Options.CaptureCursor, writer);
                pipeline.Lost += reason => RequestStop($"Se detuvo la grabación: {reason} Se guardó lo grabado hasta ahí.");
                Writer = writer;
                Pipeline = pipeline;
            }
            catch
            {
                writer?.Dispose();
                Mp4Writer.TryDeleteFile(TempPath);
                Writer = null;
                throw;
            }
        }

        public static long QpcToTicks(long qpc) => (long)(qpc * (10_000_000.0 / Stopwatch.Frequency));

        /// <summary>Arranca audio y video con el mismo t0.</summary>
        public void Begin()
        {
            T0Qpc = Stopwatch.GetTimestamp();
            foreach (var s in _sources) s.SetOrigin(T0Qpc);
            if (WantsAudio)
            {
                _mixer = new AudioMixer(_sources.Select(s => new MixerInput(s.Id, s.Timeline,
                    s.Id == AudioSourceIds.System
                        ? Options.SystemAudioGain
                        : (Options.Microphones ?? []).FirstOrDefault(m => string.Equals(m.DeviceId, s.Id, StringComparison.OrdinalIgnoreCase))?.Gain ?? 1f)));
                _mixerStop = false;
                _mixerThread = new Thread(MixerLoop) { IsBackground = true, Name = "Pantallazo audio mixer", Priority = ThreadPriority.AboveNormal };
                _mixerThread.Start();
            }
            Pipeline!.Start(QpcToTicks(T0Qpc));
            Clock.Restart();
        }

        // ---------------- pausa
        // El tiempo de la grabación es el del reloj menos lo que estuvo en pausa. En pausa el mezclador termina de
        // escribir el audio hasta el instante exacto de la pausa y se queda ahí; al reanudar, las fuentes corren su
        // origen lo que duró la pausa, así lo que llega después sigue pegado (sin hueco ni desfase con el video).
        private readonly object _pauseLock = new();
        private long _pauseQpc;        // 0 = grabando
        private long _pausedQpc;       // total en pausa
        private static readonly long LatencyQpc = Stopwatch.Frequency * AudioLatencyFrames / AudioTimeline.Rate;

        private long FramesAt(long qpc) => (long)((qpc - T0Qpc - _pausedQpc) * (double)AudioTimeline.Rate / Stopwatch.Frequency);

        public void Pause()
        {
            lock (_pauseLock)
            {
                if (_pauseQpc != 0) return;
                _pauseQpc = Stopwatch.GetTimestamp();
                Clock.Stop();
                Pipeline?.Pause(QpcToTicks(_pauseQpc));
            }
            RecordingLog.Write($"Pausa en {Clock.Elapsed:mm\\:ss\\.ff}");
        }

        public void Resume()
        {
            lock (_pauseLock)
            {
                if (_pauseQpc == 0) return;
                long now = Stopwatch.GetTimestamp();
                long pausePoint = FramesAt(_pauseQpc);
                _pausedQpc += now - _pauseQpc;
                _pauseQpc = 0;
                foreach (var s in _sources) s.SetOrigin(T0Qpc + _pausedQpc, pausePoint);
                Pipeline?.Resume(QpcToTicks(now));
                Clock.Start();
            }
            RecordingLog.Write($"Reanudada en {Clock.Elapsed:mm\\:ss\\.ff}");
        }

        /// <summary>Hasta dónde puede mezclar el audio ahora: 100 ms detrás del reloj, y nunca más allá de una pausa.</summary>
        private long MixTarget()
        {
            lock (_pauseLock)
            {
                long t = Stopwatch.GetTimestamp() - LatencyQpc;
                if (_pauseQpc != 0) t = Math.Min(t, _pauseQpc);
                return FramesAt(t);
            }
        }

        private void MixerLoop()
        {
            var levelsClock = Stopwatch.StartNew();
            while (!_mixerStop)
            {
                Thread.Sleep(10);
                try
                {
                    MixUpTo(MixTarget());
                    if (levelsClock.ElapsedMilliseconds >= 50)
                    {
                        levelsClock.Restart();
                        _owner.PostLevels(_mixer!.TakeLevels());
                    }
                }
                catch (Exception ex)
                {
                    RecordingLog.Write($"Mezclador: {ex.Message}");
                }
            }
        }

        private void MixUpTo(long target)
        {
            var mixer = _mixer!;
            while (mixer.Position < target)
            {
                int n = (int)Math.Min(target - mixer.Position, AudioChunkFrames);
                long start = mixer.Position;
                var mix = mixer.Mix(n);
                int bytes = n * 4;
                if (_pcm.Length < bytes) _pcm = new byte[bytes];
                AudioMixer.ToPcm16(mix, _pcm);
                var buffer = MediaFactory.MFCreateMemoryBuffer(bytes);
                try
                {
                    buffer.Lock(out nint p, out _, out _);
                    System.Runtime.InteropServices.Marshal.Copy(_pcm, 0, p, bytes);
                    buffer.Unlock();
                    buffer.CurrentLength = bytes;
                    var sample = MediaFactory.MFCreateSample();
                    sample.AddBuffer(buffer);
                    sample.SampleTime = start * 10_000_000L / AudioTimeline.Rate;
                    sample.SampleDuration = n * 10_000_000L / AudioTimeline.Rate;
                    Writer!.WriteAudio(sample);
                }
                finally { buffer.Dispose(); }
            }
        }

        private void StopMixer()
        {
            _mixerStop = true;
            _mixerThread?.Join(2000);
            _mixerThread = null;
        }

        // ---------------- vigilancia

        public void StartWatchdog() => _watchdog = new Timer(_ => Watch(), null, 1000, 1000);

        private void Watch()
        {
            try
            {
                if (Volatile.Read(ref _stopRequested) != 0) return;
                long free = FreeSpace(Path.GetDirectoryName(TempPath)!);
                if (free >= 0 && free < MinFreeWhileRecording)
                {
                    RequestStop("Se detuvo la grabación porque queda muy poco espacio en el disco (menos de 100 MB). Se guardó lo grabado.");
                    return;
                }
                var p = Pipeline;
                if (p == null) return;
                long emitted = p.FramesEmitted, dropped = p.FramesDropped;
                long de = emitted - _lastEmitted, dd = dropped - _lastDropped;
                _lastEmitted = emitted; _lastDropped = dropped;
                if (de + dd > 0 && dd * 20 > de + dd) _badSeconds++; else _badSeconds = 0;
                if (_badSeconds >= 5 && !_dropWarned)
                {
                    _dropWarned = true;
                    bool scaled = OutputWidth == Canvas.Width && Canvas.Width > 1920;
                    Warn("La placa no da abasto: se están perdiendo cuadros. Probá con menos fps" +
                         (scaled ? " o grabando un área más chica." : "."));
                }
            }
            catch (Exception ex) { RecordingLog.Write($"Vigilancia: {ex.Message}"); }
        }

        private void RequestStop(string reason)
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) != 0) return;
            Warn(reason);
            _ = _owner.BeginStop(this, reason);
        }

        // ---------------- fallas del encoder

        private void OnWriterFaulted(Exception ex)
        {
            if (Volatile.Read(ref _stopRequested) != 0) return;
            bool early = Clock.Elapsed < HardwareGracePeriod;
            if (!Software && !RetriedSoftware && early)
            {
                RetriedSoftware = true;
                ThreadPool.QueueUserWorkItem(_ => RestartInSoftware(ex));
                return;
            }
            RequestStop($"Se detuvo la grabación porque falló el encoder de video ({ex.Message}).");
        }

        private void RestartInSoftware(Exception cause)
        {
            lock (_sync)
            {
                if (Volatile.Read(ref _stopRequested) != 0) return;
                RecordingLog.Write($"El encoder por hardware falló en los primeros segundos; se reinicia por software: {cause.Message}");
                try
                {
                    StopMixer();
                    Pipeline?.Dispose();
                    Pipeline = null;
                    string? encoder = Writer?.EncoderName;
                    Writer?.Abort();
                    Writer?.Dispose();
                    Writer = null;
                    Mp4Writer.TryDeleteFile(TempPath);
                    EncoderProbe.MarkHardwareFailed(Gpu, encoder, cause.Message);
                    CreateVideo(software: true);
                    Begin();
                    Warn("El encoder por hardware falló al empezar; se reinició la grabación por software (máximo 1080p y 60 fps).");
                }
                catch (Exception ex)
                {
                    RecordingLog.Write($"Tampoco arrancó por software: {ex}");
                    RequestStop($"Se detuvo la grabación: el encoder falló y no se pudo seguir por software ({ex.Message}).");
                }
            }
        }

        // ---------------- cierre

        public RecordingResult Stop(string? reason)
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            lock (_sync)
            {
                var stopwatch = Stopwatch.StartNew();
                _watchdog?.Dispose();
                _watchdog = null;
                long stopQpc = Stopwatch.GetTimestamp();
                lock (_pauseLock) { if (_pauseQpc != 0) stopQpc = _pauseQpc; }   // detenido en pausa: termina donde se pausó
                var duration = Clock.Elapsed;
                Clock.Stop();

                Pipeline?.Stop(QpcToTicks(stopQpc));

                if (_mixer != null)
                {
                    Thread.Sleep(150);   // que lleguen los últimos paquetes de WASAPI
                    StopMixer();
                    try { MixUpTo(FramesAt(stopQpc)); }
                    catch (Exception ex) { RecordingLog.Write($"Último audio: {ex.Message}"); }
                }
                DisposeAudio();

                RecordingResult result;
                var writer = Writer;
                string[] warnings() { lock (Warnings) return Warnings.ToArray(); }
                if (writer == null)
                {
                    result = new RecordingResult(false, null, reason ?? "La grabación no llegó a empezar.", warnings(), duration, OutputWidth, OutputHeight);
                }
                else if (writer.VideoSamplesWritten == 0 && writer.PendingCount == 0 && (Pipeline?.FramesEmitted ?? 0) == 0)
                {
                    writer.Abort();
                    writer.Dispose();
                    Writer = null;
                    Mp4Writer.TryDeleteFile(TempPath);
                    result = new RecordingResult(false, null,
                        "No llegó ningún cuadro de video, así que no se guardó nada. Probá de nuevo; si se repite, revisá que el monitor esté encendido.",
                        warnings(), duration, OutputWidth, OutputHeight);
                }
                else if (!writer.Finish(FinishTimeout, out var error))
                {
                    RecordingLog.Write("Finalize no terminó en 10 s: se abandona el escritor.");
                    Writer = null;   // abandonado: no se libera (MF sigue adentro)
                    result = new RecordingResult(false, TempPath,
                        $"La grabación no se pudo cerrar a tiempo. El archivo temporal quedó en «{TempPath}».",
                        warnings(), duration, OutputWidth, OutputHeight);
                }
                else if (error != null || writer.VideoSamplesWritten == 0)
                {
                    writer.Dispose();
                    Writer = null;
                    long len = SafeLength(TempPath);
                    if (len <= 0 || writer.VideoSamplesWritten == 0)
                    {
                        Mp4Writer.TryDeleteFile(TempPath);
                        result = new RecordingResult(false, null, $"No se pudo guardar la grabación: {error?.Message ?? "no se escribió ningún cuadro"}",
                            warnings(), duration, OutputWidth, OutputHeight);
                    }
                    else
                    {
                        result = new RecordingResult(false, TempPath,
                            $"No se pudo cerrar bien el archivo ({error!.Message}). Quedó el temporal en «{TempPath}».",
                            warnings(), duration, OutputWidth, OutputHeight);
                    }
                }
                else
                {
                    writer.Dispose();
                    Writer = null;
                    if (SafeLength(TempPath) <= 0)
                    {
                        Mp4Writer.TryDeleteFile(TempPath);
                        result = new RecordingResult(false, null, "El archivo quedó vacío y se descartó.", warnings(), duration, OutputWidth, OutputHeight);
                    }
                    else
                    {
                        string final;
                        try
                        {
                            final = FileNaming.UniquePath(FinalPath, File.Exists);
                            File.Move(TempPath, final);
                        }
                        catch (Exception ex)
                        {
                            final = TempPath;
                            Warn($"No se pudo renombrar el archivo: {ex.Message}. Quedó como «{TempPath}».");
                        }
                        result = new RecordingResult(true, final, null, warnings(), duration, OutputWidth, OutputHeight);
                    }
                }
                if (writer != null)
                {
                    _lastEncoder = writer.EncoderName;
                    _videoWritten = writer.VideoSamplesWritten;
                    _audioWritten = writer.AudioSamplesWritten;
                }
                StopTime = stopwatch.Elapsed;
                RecordingLog.Write($"Grabación cerrada en {stopwatch.ElapsedMilliseconds} ms: {(result.Success ? result.FilePath : result.Error)} " +
                                   $"(cuadros: llegaron {Pipeline?.FramesArrived}, nuevos {Pipeline?.FramesEmitted}, repetidos {Pipeline?.FramesRepeated}, descartados {Pipeline?.FramesDropped})");
                DisposeVideo();
                return result;
            }
        }

        private static long SafeLength(string path)
        {
            try { return File.Exists(path) ? new FileInfo(path).Length : -1; } catch { return -1; }
        }

        public RecorderDiagnostics Diagnostics() => new(
            Gpu?.AdapterName ?? _adapterName, Writer?.EncoderName ?? _lastEncoder, Software, Canvas.Width, Canvas.Height, OutputWidth, OutputHeight,
            Fps, Bitrate, _framesArrived, _framesEmitted, _framesDropped, _videoWritten, _audioWritten,
            _sources.Count > 0 ? _sources.Select(s => $"{s.Id} «{s.Name}»").ToArray() : _sourceNamesAtStop,
            StartTime, StopTime);

        private string? _lastEncoder;
        private string _adapterName = "?";
        private long _framesArrived, _framesEmitted, _framesDropped, _videoWritten, _audioWritten;
        private string[] _sourceNamesAtStop = NoSources;

        private void DisposeAudio()
        {
            StopMixer();
            if (_notifyClient != null && _notifyEnumerator != null)
            {
                try { _notifyEnumerator.UnregisterEndpointNotificationCallback(_notifyClient); } catch { }
            }
            _notifyEnumerator?.Dispose();
            _notifyEnumerator = null;
            _notifyClient = null;
            if (_sources.Count > 0) _sourceNamesAtStop = _sources.Select(s => $"{s.Id} «{s.Name}»").ToArray();
            foreach (var s in _sources)
            {
                s.Failed -= OnSourceFailed;
                s.Dispose();
            }
            _sources.Clear();
        }

        private void DisposeVideo()
        {
            if (Pipeline != null)
            {
                _framesArrived = Pipeline.FramesArrived;
                _framesEmitted = Pipeline.FramesEmitted;
                _framesDropped = Pipeline.FramesDropped;
                Pipeline.Dispose();
                Pipeline = null;
            }
            if (Writer != null)
            {
                _lastEncoder = Writer.EncoderName;
                _videoWritten = Writer.VideoSamplesWritten;
                _audioWritten = Writer.AudioSamplesWritten;
                Writer.Dispose();
                Writer = null;
            }
            if (Gpu != null) _adapterName = Gpu.AdapterName;
            try { Gpu?.Dispose(); } catch { }
            Gpu = null!;
        }

        /// <summary>Libera todo (inicio fallido o error al detener).</summary>
        public void DisposeAll()
        {
            Interlocked.Exchange(ref _stopRequested, 1);
            _watchdog?.Dispose();
            try { DisposeAudio(); } catch { }
            try { Pipeline?.Dispose(); } catch { }
            Pipeline = null;
            try { Writer?.Abort(); Writer?.Dispose(); } catch { }
            Writer = null;
            Mp4Writer.TryDeleteFile(TempPath);
            try { Gpu?.Dispose(); } catch { }
            Gpu = null!;
        }
    }
}

/// <summary>Error de arranque con mensaje ya listo para el usuario.</summary>
internal sealed class RecorderException(string message) : Exception(message);
