using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Pantallazo.Recording;

namespace RecHarness;

internal static class Program
{
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint value);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains(EncoderProbe.Argument)) return EncoderProbe.RunChild(args);

        SetProcessDpiAwarenessContext(-4);   // PerMonitorV2, como la app
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        EncoderProbe.ProbeExecutablePath = Environment.ProcessPath;
        if (args.Length == 0) return Usage();
        try
        {
            return args[0] switch
            {
                "list" => List(),
                "record" => Record(args.Skip(1).ToArray()),
                "inspect" when args.Length > 1 => Inspect(args[1]),
                "frame" when args.Length > 3 => Frame(args[1], args[2], args[3]),
                "meter" => Meter(args.Skip(1).ToArray()),
                "adapters" => Adapters(),
                "lifecycle" when args.Length > 1 => Lifecycle(args[1]),
                "decide" when args.Length > 1 => Decide(args[1]),
                _ => Usage(),
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static int Usage()
    {
        Console.WriteLine("""
            RecHarness list
            RecHarness record <carpeta> <segundos> [--target all|display:<id>|region:x,y,w,h] [--fps N] [--system]
                              [--mic <id>]... [--quality low|balanced|high] [--software] [--no-cursor] [--no-anim]
            RecHarness inspect <archivo.mp4>
            RecHarness meter [--system] [--mic <id>]... [--seconds N]
            """);
        return 2;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    /// <summary>Mismo dato que Win32_Battery.BatteryStatus (1 = a batería), sin depender de WMI.</summary>
    private static string Power()
    {
        if (!GetSystemPowerStatus(out var s)) return "no se pudo leer";
        string line = s.ACLineStatus switch { 0 => "A BATERÍA", 1 => "enchufado", _ => "desconocido" };
        return $"{line}, batería {(s.BatteryLifePercent == 255 ? "?" : s.BatteryLifePercent + " %")}";
    }
    private static int List()
    {
        Console.WriteLine($"Energía: {Power()}");
        Console.WriteLine("Monitores:");
        var adapters = GpuDeviceInfo();
        foreach (var d in DisplayCatalog.GetDisplays())
            Console.WriteLine($"  {d.Id}  «{d.FriendlyName}»  {d.Width}×{d.Height} en ({d.X},{d.Y})  {d.RefreshHz} Hz" +
                              $"{(d.IsPrimary ? "  [principal]" : "")}  placa: {(adapters.TryGetValue(d.Handle, out var a) ? a : "?")}");
        Console.WriteLine("Micrófonos:");
        foreach (var m in AudioDeviceCatalog.GetInputDevices())
        {
            string vol = "";
            try
            {
                using var en = new NAudio.CoreAudioApi.MMDeviceEnumerator();
                using var dev = en.GetDevice(m.Id);
                var ev = dev.AudioEndpointVolume;
                vol = $"  volumen {ev.MasterVolumeLevelScalar * 100:0} %{(ev.Mute ? ", SILENCIADO" : "")}";
            }
            catch { }
            Console.WriteLine($"  {(m.IsDefault ? "*" : " ")} «{m.Name}»  {m.Id}{vol}");
        }
        var o = AudioDeviceCatalog.GetDefaultOutput();
        Console.WriteLine($"Salida predeterminada: {(o == null ? "ninguna" : $"«{o.Name}»  {o.Id}")}");
        return 0;
    }

    private static Dictionary<nint, string> GpuDeviceInfo()
    {
        var result = new Dictionary<nint, string>();
        try
        {
            var map = GpuDevice.MonitorAdapters();
            foreach (var d in DisplayCatalog.GetDisplays())
            {
                if (!map.TryGetValue(d.Handle, out long luid)) continue;
                using var gpu = GpuDevice.CreateForLuid(luid);
                string? enc = EncoderProbe.EnumerateHardwareEncoder(luid);
                result[d.Handle] = $"«{gpu.AdapterName}» driver {gpu.DriverVersion}, LUID {GpuDevice.LuidText(luid)}, encoder listado: {enc ?? "ninguno"}";
            }
        }
        catch (Exception ex) { Console.WriteLine($"  (no se pudo leer las placas: {ex.Message})"); }
        return result;
    }

    private static int Inspect(string path)
    {
        PrintReport(MediaInspector.Inspect(path), 0);
        return 0;
    }

    /// <summary>Todas las placas (también las que no tienen monitores), con el encoder que lista Windows.</summary>
    private static int Adapters()
    {
        using var factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<Vortice.DXGI.IDXGIFactory1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var a).Success; i++)
        {
            using (a)
            {
                var d = a.Description1;
                long luid = System.Runtime.CompilerServices.Unsafe.As<Vortice.Luid, long>(ref d.Luid);
                Console.WriteLine($"  «{d.Description}»  LUID {GpuDevice.LuidText(luid)}  flags {d.Flags}  encoder listado: {EncoderProbe.EnumerateHardwareEncoder(luid) ?? "ninguno"}");
            }
        }
        return 0;
    }

    /// <summary>Ciclo de vida: doble Stop, reutilizar el grabador, Start durante una grabación, Dispose grabando.</summary>
    private static int Lifecycle(string folder)
    {
        folder = Path.GetFullPath(folder);
        var win = AnimationWindow.Show(new Rectangle(100, 100, 400, 300));
        RecordingOptions Opt(string name) => new() { OutputPath = Path.Combine(folder, name + ".mp4"), SystemAudio = true, Fps = 30 };
        using (var rec = new Recorder())
        {
            int finished = 0;
            rec.Finished += (_, _) => Interlocked.Increment(ref finished);
            rec.StartAsync(Opt("ciclo")).GetAwaiter().GetResult();
            try { rec.StartAsync(Opt("ciclo-bis")).GetAwaiter().GetResult(); Console.WriteLine("MAL: el segundo Start no falló"); }
            catch (InvalidOperationException ex) { Console.WriteLine($"Start durante grabación → «{ex.Message}»"); }
            Thread.Sleep(1500);
            var t1 = rec.StopAsync();
            var t2 = rec.StopAsync();
            var r1 = t1.GetAwaiter().GetResult();
            var r2 = t2.GetAwaiter().GetResult();
            Console.WriteLine($"Doble Stop: mismo resultado = {ReferenceEquals(r1, r2)}, OK = {r1.Success}, {Path.GetFileName(r1.FilePath)}");
            var r3 = rec.StopAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Stop sin grabación → «{r3.Error}», Elapsed = {rec.Elapsed.TotalSeconds:0.00} s");
            // Mismo nombre otra vez: tiene que salir "ciclo (2).mp4"
            rec.StartAsync(Opt("ciclo")).GetAwaiter().GetResult();
            Thread.Sleep(1000);
            var r4 = rec.StopAsync().GetAwaiter().GetResult();
            Console.WriteLine($"Reutilizado: OK = {r4.Success}, {Path.GetFileName(r4.FilePath)}");
            Thread.Sleep(300);
            Console.WriteLine($"Finished disparado {finished} veces (esperado 2)");
        }
        var rec2 = new Recorder();
        RecordingResult? last = null;
        rec2.Finished += (_, r) => last = r;
        rec2.StartAsync(Opt("dispose")).GetAwaiter().GetResult();
        Thread.Sleep(1000);
        var sw = Stopwatch.StartNew();
        rec2.Dispose();
        Console.WriteLine($"Dispose grabando: {sw.ElapsedMilliseconds} ms, estado {rec2.State}, archivo {Path.GetFileName(last?.FilePath ?? "(Finished no llegó)")}");
        // Disco: rechazo al arrancar y detención automática (umbral forzado por encima del espacio libre).
        long oldStart = Recorder.MinFreeToStart, oldRec = Recorder.MinFreeWhileRecording;
        try
        {
            Recorder.MinFreeToStart = long.MaxValue / 2;
            using (var rec3 = new Recorder())
            {
                try { rec3.StartAsync(Opt("sin-disco")).GetAwaiter().GetResult(); Console.WriteLine("MAL: arrancó sin disco"); }
                catch (InvalidOperationException ex) { Console.WriteLine($"Sin disco al arrancar → «{ex.Message[..Math.Min(80, ex.Message.Length)]}…»  estado {rec3.State}"); }
            }
            Recorder.MinFreeToStart = oldStart;
            using var rec4 = new Recorder();
            var done = new ManualResetEventSlim();
            RecordingResult? auto = null;
            var warns = new List<string>();
            rec4.Warning += (_, w) => { lock (warns) warns.Add(w); };
            rec4.Finished += (_, r) => { auto = r; done.Set(); };
            rec4.StartAsync(Opt("disco-lleno")).GetAwaiter().GetResult();
            Thread.Sleep(1500);
            Recorder.MinFreeWhileRecording = long.MaxValue / 2;
            bool ok = done.Wait(10_000);
            Console.WriteLine($"Poco disco durante: se detuvo sola = {ok}, guardó = {auto?.Success} {Path.GetFileName(auto?.FilePath)}, " +
                              $"duración {auto?.Duration.TotalSeconds:0.0} s, aviso: «{warns.LastOrDefault()}»");
        }
        finally
        {
            Recorder.MinFreeToStart = oldStart;
            Recorder.MinFreeWhileRecording = oldRec;
        }

        // Falla del encoder por hardware a los pocos cuadros: tiene que reiniciar por software y guardar.
        string cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pantallazo", "encoder-cache.json");
        string? cacheBackup = File.Exists(cache) ? File.ReadAllText(cache) : null;
        try
        {
            Mp4Writer.SimulateHardwareFaultAfter = 10;
            using var rec5 = new Recorder();
            var warns = new List<string>();
            rec5.Warning += (_, w) => { lock (warns) warns.Add(w); };
            rec5.StartAsync(Opt("reintento")).GetAwaiter().GetResult();
            Thread.Sleep(3000);
            var r = rec5.StopAsync().GetAwaiter().GetResult();
            var d = rec5.LastDiagnostics;
            Console.WriteLine($"Falla simulada del hardware: OK = {r.Success} {Path.GetFileName(r.FilePath)}, {r.Width}×{r.Height}, " +
                              $"modo {(d?.Software == true ? "software" : "hardware")}, cuadros {d?.VideoSamplesWritten}, avisos: {string.Join(" | ", warns)}");
            if (r.Success) PrintReport(MediaInspector.Inspect(r.FilePath!), r.Duration.TotalSeconds);
        }
        finally
        {
            Mp4Writer.SimulateHardwareFaultAfter = -1;
            if (cacheBackup != null) File.WriteAllText(cache, cacheBackup);
        }

        var leftovers = Directory.GetFiles(folder, "*.grabando.mp4");
        Console.WriteLine($"Temporales que quedaron: {leftovers.Length}");
        win.CloseFromAnyThread();
        return 0;
    }

    /// <summary>Corre la decisión de encoder (sondeo en proceso hijo + caché) para una placa.</summary>
    private static int Decide(string luidHex)
    {
        long luid = long.Parse(luidHex, System.Globalization.NumberStyles.HexNumber);
        using var gpu = GpuDevice.CreateForLuid(luid);
        for (int i = 0; i < 2; i++)
        {
            var sw = Stopwatch.StartNew();
            var d = EncoderProbe.Decide(gpu);
            Console.WriteLine($"  «{gpu.AdapterName}»: {(d.UseHardware ? "HARDWARE" : "SOFTWARE")}, encoder «{d.EncoderName}», " +
                              $"motivo: {d.Reason}, de caché: {d.FromCache}, {sw.ElapsedMilliseconds} ms");
        }
        return 0;
    }

    private static int Frame(string mp4, string seconds, string png)
    {
        MediaInspector.SaveFrame(mp4, TimeSpan.FromSeconds(double.Parse(seconds, System.Globalization.CultureInfo.InvariantCulture)), png);
        return 0;
    }

    private static int Meter(string[] args)
    {
        bool system = args.Contains("--system");
        var mics = new List<string>();
        int seconds = 5;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--mic" && i + 1 < args.Length) mics.Add(args[++i]);
            if (args[i] == "--seconds" && i + 1 < args.Length) seconds = int.Parse(args[++i]);
        }
        var max = new Dictionary<string, float>();
        int events = 0;
        using var m = new AudioLevelMonitor(system, mics);
        m.SourceFailed += (_, id) => Console.WriteLine($"Falló la fuente {id}");
        m.LevelsChanged += (_, levels) =>
        {
            Interlocked.Increment(ref events);
            lock (max) foreach (var l in levels) max[l.SourceId] = Math.Max(max.GetValueOrDefault(l.SourceId), l.Peak);
        };
        if (system) PlayTone(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Thread.Sleep(seconds * 1000);
        Console.WriteLine($"Eventos de nivel: {events} en {seconds} s");
        lock (max) foreach (var (k, v) in max) Console.WriteLine($"  {k}: pico máx {v:0.000}");
        return 0;
    }

    private static int Record(string[] args)
    {
        if (args.Length < 2) return Usage();
        string folder = Path.GetFullPath(args[0]);
        double seconds = double.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
        var displays = DisplayCatalog.GetDisplays();
        var primary = displays.First(d => d.IsPrimary);
        var options = new RecordingOptions
        {
            OutputPath = Path.Combine(folder, $"RecHarness {DateTime.Now:yyyy-MM-dd HH.mm.ss}.mp4"),
            SystemAudio = false,
        };
        bool software = false, anim = true;
        double pauseAt = -1, pauseFor = 0, toneAt = 1;
        var mics = new List<MicrophoneSelection>();
        Rectangle animBounds = new(primary.X + primary.Width / 2 - 240, primary.Y + primary.Height / 2 - 180, 480, 360);
        for (int i = 2; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--target":
                    string t = args[++i];
                    if (t == "all") options = options with { Target = CaptureTargetKind.AllDisplays };
                    else if (t.StartsWith("display:"))
                    {
                        string id = t[8..];
                        var d = displays.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase) || x.Id.EndsWith(id))
                                ?? throw new ArgumentException($"No existe el monitor {id}");
                        options = options with { Target = CaptureTargetKind.Display, DisplayId = d.Id };
                        animBounds = new(d.X + d.Width / 2 - 240, d.Y + d.Height / 2 - 180, 480, 360);
                    }
                    else if (t.StartsWith("region:"))
                    {
                        var p = t[7..].Split(',').Select(int.Parse).ToArray();
                        options = options with { Target = CaptureTargetKind.Region, Region = (p[0], p[1], p[2], p[3]) };
                        int aw = Math.Min(480, p[2] - 40), ah = Math.Min(360, p[3] - 40);
                        animBounds = new(p[0] + (p[2] - aw) / 2, p[1] + (p[3] - ah) / 2, aw, ah);
                    }
                    break;
                case "--fps": options = options with { Fps = int.Parse(args[++i]) }; break;
                case "--max-height": options = options with { MaxOutputHeight = int.Parse(args[++i]) }; break;
                case "--system": options = options with { SystemAudio = true }; break;
                case "--mic": mics.Add(new MicrophoneSelection(args[++i])); break;
                case "--quality": options = options with { Quality = Enum.Parse<VideoQuality>(args[++i], true) }; break;
                case "--software": software = true; break;
                case "--no-cursor": options = options with { CaptureCursor = false }; break;
                case "--no-anim": anim = false; break;
                case "--pause-at": pauseAt = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--pause-for": pauseFor = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                case "--tone-at": toneAt = double.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
                default: throw new ArgumentException($"Opción desconocida: {args[i]}");
            }
        }
        options = options with { Microphones = mics };

        Console.WriteLine($"Energía: {Power()}");
        Console.WriteLine($"Destino: {options.Target} {options.DisplayId}{options.Region}, {options.Fps} fps, {options.Quality}, " +
                          $"sistema={options.SystemAudio}, mics={mics.Count}{(software ? ", SOFTWARE forzado" : "")}");
        var log = new List<string>();
        RecordingLog.Message += m => { lock (log) log.Add($"{DateTime.Now:HH:mm:ss.fff} {m}"); };

        AnimationWindow? win = anim ? AnimationWindow.Show(animBounds) : null;
        using var rec = new Recorder { ForceSoftwareEncoder = software };
        var levelMax = new Dictionary<string, float>();
        int levelEvents = 0;
        var states = new List<string>();
        rec.StateChanged += (_, s) => { lock (states) states.Add(s.ToString()); };
        rec.Warning += (_, w) => Console.WriteLine($"  AVISO: {w}");
        rec.LevelsChanged += (_, levels) =>
        {
            Interlocked.Increment(ref levelEvents);
            lock (levelMax) foreach (var l in levels) levelMax[l.SourceId] = Math.Max(levelMax.GetValueOrDefault(l.SourceId), l.Peak);
        };
        var finished = new ManualResetEventSlim();
        RecordingResult? finishedResult = null;
        rec.Finished += (_, r) => { finishedResult = r; finished.Set(); };

        var sw = Stopwatch.StartNew();
        try
        {
            rec.StartAsync(options).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"No arrancó: {ex.Message}");
            win?.CloseFromAnyThread();
            DumpLog(log);
            return 1;
        }
        var startMs = sw.ElapsedMilliseconds;
        Console.WriteLine($"Grabando ({startMs} ms en arrancar)…");
        if (options.SystemAudio) PlayTone(TimeSpan.FromSeconds(toneAt), TimeSpan.FromSeconds(2));

        var until = TimeSpan.FromSeconds(seconds);
        bool pausedOnce = false;
        while (sw.Elapsed - TimeSpan.FromMilliseconds(startMs) < until && rec.State is RecorderState.Recording or RecorderState.Paused)
        {
            var el = sw.Elapsed - TimeSpan.FromMilliseconds(startMs);
            if (pauseAt >= 0 && !pausedOnce && el.TotalSeconds >= pauseAt)
            {
                pausedOnce = true;
                Console.WriteLine($"Pausa a los {el.TotalSeconds:0.00} s (reloj): {rec.Pause()}, grabado {rec.Elapsed.TotalSeconds:0.00} s");
                Thread.Sleep(TimeSpan.FromSeconds(pauseFor));
                Console.WriteLine($"Reanuda: {rec.Resume()}, grabado {rec.Elapsed.TotalSeconds:0.00} s");
            }
            Thread.Sleep(50);
        }
        long animPaints = win?.Paints ?? 0;
        var stopSw = Stopwatch.StartNew();
        var result = rec.StopAsync().GetAwaiter().GetResult();
        var stopMs = stopSw.ElapsedMilliseconds;
        win?.CloseFromAnyThread();
        finished.Wait(2000);
        Thread.Sleep(200);

        Console.WriteLine($"Estados: {string.Join(" → ", states)}");
        Console.WriteLine($"Resultado: {(result.Success ? "OK" : "ERROR")} {result.FilePath} {result.Error}");
        Console.WriteLine($"  Duración (reloj): {result.Duration.TotalSeconds:0.00} s, {result.Width}×{result.Height}, detener tardó {stopMs} ms");
        Console.WriteLine($"  Finished recibido: {finishedResult != null}");
        foreach (var w in result.Warnings) Console.WriteLine($"  aviso en resultado: {w}");
        var diag = rec.LastDiagnostics;
        if (diag != null)
        {
            Console.WriteLine($"  Placa: {diag.Adapter}; encoder: {diag.Encoder ?? "?"} ({(diag.Software ? "software" : "hardware")})");
            Console.WriteLine($"  Lienzo {diag.CanvasWidth}×{diag.CanvasHeight} → salida {diag.OutputWidth}×{diag.OutputHeight} @ {diag.Fps} fps, {diag.Bitrate / 1e6:0.0} Mbps objetivo");
            Console.WriteLine($"  Cuadros WGC llegados {diag.FramesArrived}, emitidos {diag.FramesEmitted}, descartados {diag.FramesDropped}, escritos {diag.VideoSamplesWritten}; muestras de audio {diag.AudioSamplesWritten}");
            Console.WriteLine($"  Audio: {string.Join(", ", diag.AudioSources)}");
        }
        Console.WriteLine($"  Repintados de la animación: {animPaints}; eventos de nivel: {levelEvents}");
        lock (levelMax) foreach (var (k, v) in levelMax) Console.WriteLine($"  Medidor {k}: pico máx {v:0.000}");
        if (result.Success && result.FilePath != null)
        {
            var report = MediaInspector.Inspect(result.FilePath);
            PrintReport(report, result.Duration.TotalSeconds);
            if (options.SystemAudio && anim && diag != null)
            {
                // Destello blanco de la animación vs. comienzo del tono: sincronía audio/video.
                var origin = options.Target switch
                {
                    CaptureTargetKind.Display => (displays.First(d => d.Id == options.DisplayId).X, displays.First(d => d.Id == options.DisplayId).Y),
                    CaptureTargetKind.Region => (options.Region!.Value.X, options.Region!.Value.Y),
                    _ => (displays.Min(d => d.X), displays.Min(d => d.Y)),
                };
                double scale = diag.OutputWidth / (double)diag.CanvasWidth;
                (int, int) P(int x, int y) => ((int)((x - origin.Item1) * scale), (int)((y - origin.Item2) * scale));
                var pts = new[]
                {
                    P(animBounds.Right - 6, animBounds.Top + 6), P(animBounds.Left + 6, animBounds.Bottom - 6),
                    P(animBounds.Left + animBounds.Width / 2, animBounds.Top + animBounds.Height / 2),
                };
                var flash = MediaInspector.FindWhiteFrame(result.FilePath, pts);
                Console.WriteLine($"Sincronía: destello en el video a {(flash?.TotalMilliseconds.ToString("0") ?? "?")} ms, " +
                                  $"comienzo del sonido a {(report.SoundOnset?.TotalMilliseconds.ToString("0") ?? "?")} ms" +
                                  (flash != null && report.SoundOnset != null
                                      ? $" → audio {(report.SoundOnset.Value - flash.Value).TotalMilliseconds:+0;-0} ms respecto del video"
                                      : ""));
            }
        }
        DumpLog(log);
        return result.Success ? 0 : 1;
    }

    private static void DumpLog(List<string> log)
    {
        Console.WriteLine("Log del motor:");
        lock (log) foreach (var l in log) Console.WriteLine("  " + l);
    }

    private static void PrintReport(MediaReport r, double recordedSeconds)
    {
        Console.WriteLine("Informe del MP4:");
        Console.WriteLine($"  Duración {r.Duration.TotalSeconds:0.000} s, {r.FileBytes / 1024.0 / 1024.0:0.00} MB" +
                          (r.Duration.TotalSeconds > 0 ? $" ({r.FileBytes * 8 / r.Duration.TotalSeconds / 1e6:0.0} Mbps reales)" : ""));
        Console.WriteLine($"  Video {r.VideoSubtype} {r.Width}×{r.Height}, declara {r.DeclaredFps:0.##} fps; {r.VideoFrames} cuadros " +
                          $"(primero {r.FirstVideo.TotalMilliseconds:0} ms, último {r.LastVideo.TotalSeconds:0.000} s, hueco máx {r.MaxGapMs:0} ms); " +
                          $"promedio {(r.LastVideo > r.FirstVideo ? (r.VideoFrames - 1) / (r.LastVideo - r.FirstVideo).TotalSeconds : 0):0.0} fps");
        if (!r.HasAudio) { Console.WriteLine("  Sin pista de audio"); return; }
        Console.WriteLine($"  Audio {r.AudioRate} Hz {r.AudioChannels} canales, {r.AudioDuration.TotalSeconds:0.000} s; pico {r.AudioPeak:0.000}, RMS {r.AudioRms:0.0000}");
        Console.WriteLine($"  Ventana más fuerte: RMS {r.LoudestWindowRms:0.000} a los {r.LoudestWindowAt.TotalSeconds:0.0} s, ≈{r.LoudestWindowHz:0} Hz; " +
                          $"tono de 1 kHz detectado durante {r.ToneSeconds:0.0} s");
    }

    /// <summary>Tono de 1 kHz por la salida predeterminada (para probar el loopback).</summary>
    private static void PlayTone(TimeSpan delay, TimeSpan length)
    {
        var t = new Thread(() =>
        {
            try
            {
                Thread.Sleep(delay);
                var gen = new SignalGenerator(48000, 2) { Frequency = 1000, Gain = 0.25, Type = SignalGeneratorType.Sin };
                using var output = new WasapiOut(NAudio.CoreAudioApi.AudioClientShareMode.Shared, 50);
                output.Init(gen.Take(length));
                output.Play();
                long t0 = Stopwatch.GetTimestamp();
                Interlocked.Exchange(ref AnimationWindow.FlashUntil, t0 + (long)(length.TotalSeconds * Stopwatch.Frequency));
                Interlocked.Exchange(ref AnimationWindow.FlashFrom, t0);
                while (output.PlaybackState == PlaybackState.Playing) Thread.Sleep(20);
            }
            catch (Exception ex) { Console.WriteLine($"  (no se pudo reproducir el tono: {ex.Message})"); }
        }) { IsBackground = true };
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
    }
}
