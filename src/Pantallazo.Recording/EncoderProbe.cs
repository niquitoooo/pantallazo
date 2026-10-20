using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.MediaFoundation;

namespace Pantallazo.Recording;

/// <summary>Qué encoder usar para un adaptador.</summary>
internal sealed record EncoderDecision(bool UseHardware, string? EncoderName, string Reason, bool FromCache);

/// <summary>
/// Sondeo del encoder H.264 por hardware en un proceso aparte. Con algunas GPU NVIDIA sin monitor conectado, Windows
/// elige "Microsoft AVC DX12 Encoder HMFT", que tira abajo el proceso o falla por memoria; por eso se prueba en un
/// hijo y, si toca ese encoder o ninguno, se graba por software.
/// El resultado se guarda por adaptador + versión de driver en %LOCALAPPDATA%\Pantallazo\encoder-cache.json.
/// </summary>
public static class EncoderProbe
{
    public const string Argument = "--probe-encoder";
    private static readonly object CacheLock = new();

    /// <summary>Ruta del exe a usar para el proceso hijo (la app la setea al arrancar: Environment.ProcessPath).</summary>
    public static string? ProbeExecutablePath { get; set; }

    /// <summary>
    /// La app llama esto desde Main cuando recibe "--probe-encoder &lt;LUID&gt;" y devuelve el exit code
    /// (0 = el encoder por hardware funcionó; la salida estándar trae ENCODER=… y OK o ERROR=…).
    /// </summary>
    public static int RunChild(string[] args)
    {
        int i = Array.IndexOf(args, Argument);
        if (i < 0 || i + 1 >= args.Length || !long.TryParse(args[i + 1], System.Globalization.NumberStyles.HexNumber, null, out long luid))
        {
            Console.WriteLine("ERROR=argumentos inválidos");
            return 2;
        }
        string path = Path.Combine(Path.GetTempPath(), $"pantallazo-probe-{Environment.ProcessId}.mp4");
        try
        {
            using var gpu = GpuDevice.CreateForLuid(luid);
            Console.WriteLine($"ADAPTER={gpu.AdapterName}");
            const int w = 1280, h = 720, fps = 30;
            var cfg = new VideoStreamConfig(w, h, w, h, fps, 4_000_000, Software: false);
            using var writer = new Mp4Writer(path, cfg, gpu, withAudio: false);
            Console.WriteLine($"ENCODER={writer.EncoderName}");
            Console.Out.Flush();
            for (int f = 0; f < 30; f++)
            {
                using var tex = gpu.Device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, w, h, 1, 1,
                    BindFlags.RenderTarget | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
                using (var rtv = gpu.Device.CreateRenderTargetView(tex))
                    gpu.Context.ClearRenderTargetView(rtv, new Vortice.Mathematics.Color4(f / 30f, 0.2f, 1 - f / 30f, 1));
                var buffer = MediaFactory.MFCreateDXGISurfaceBuffer(typeof(ID3D11Texture2D).GUID, tex, 0, false);
                using (var b2 = buffer.QueryInterface<IMF2DBuffer>()) buffer.CurrentLength = b2.ContiguousLength;
                var sample = MediaFactory.MFCreateSample();
                sample.AddBuffer(buffer);
                buffer.Dispose();
                sample.SampleTime = f * 10_000_000L / fps;
                sample.SampleDuration = 10_000_000L / fps;
                writer.WriteVideo(sample);
            }
            if (!writer.Finish(TimeSpan.FromSeconds(10), out var error))
            {
                Console.WriteLine("ERROR=el encoder no cerró a tiempo");
                return 1;
            }
            if (error != null) { Console.WriteLine($"ERROR={error.Message}"); return 1; }
            long size = new FileInfo(path).Length;
            if (size <= 0) { Console.WriteLine("ERROR=archivo vacío"); return 1; }
            Console.WriteLine($"BYTES={size}");
            Console.WriteLine("OK");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR={ex.Message.ReplaceLineEndings(" ")}");
            return 1;
        }
        finally
        {
            Mp4Writer.TryDeleteFile(path);
        }
    }

    /// <summary>
    /// Opcional: decide (y guarda en caché) el encoder de la placa del monitor principal en segundo plano, para que la
    /// primera grabación no pague el sondeo (~1,5 s). Nunca lanza.
    /// </summary>
    public static Task PrepareAsync() => Task.Run(() =>
    {
        try
        {
            MfRuntime.EnsureStarted();
            var primary = DisplayCatalog.GetDisplays().FirstOrDefault(d => d.IsPrimary);
            if (primary == null) return;
            using var gpu = GpuDevice.CreateForMonitor(primary.Handle, out _);
            var d = Decide(gpu);
            RecordingLog.Write($"Encoder preparado para «{gpu.AdapterName}»: {(d.UseHardware ? "hardware" : "software")} ({d.Reason})");
            // Solo se calienta en este proceso un encoder del fabricante que ya pasó el sondeo aislado:
            // el genérico DX12 de Windows tira abajo el proceso con algunos drivers.
            if (d.UseHardware) WarmUp(gpu, primary);
        }
        catch (Exception ex) { RecordingLog.Write($"PrepareAsync: {ex.Message}"); }
    });

    /// <summary>
    /// La primera inicialización del encoder del driver en cada proceso tarda ~1,9 s (AMD); las siguientes ~0,2 s
    /// (medido el 2026-10-17). Se crea y descarta un escritor sin cuadros para que la primera grabación arranque rápido.
    /// </summary>
    private static void WarmUp(GpuDevice gpu, DisplayInfo display)
    {
        var sw = Stopwatch.StartNew();
        string path = Path.Combine(Path.GetTempPath(), $"pantallazo-calentar-{Environment.ProcessId}.mp4");
        try
        {
            var (w, h) = VideoMath.OutputSize(display.Width, display.Height, software: false);
            var cfg = new VideoStreamConfig(w, h, w, h, 30, VideoMath.Bitrate(w, h, 30, VideoQuality.Balanced), Software: false);
            using (new Mp4Writer(path, cfg, gpu, withAudio: false)) { }
            RecordingLog.Write($"Encoder calentado en {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { RecordingLog.Write($"No se pudo calentar el encoder: {ex.Message}"); }
        finally { Mp4Writer.TryDeleteFile(path); }
    }

    /// <summary>true si el nombre es un encoder del fabricante (AMD, NVIDIA, Intel), no el genérico de Windows.</summary>
    internal static bool IsVendorEncoder(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains("DX12", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.StartsWith("H264 Encoder MFT", StringComparison.OrdinalIgnoreCase)) return false;   // encoder por software de Windows
        if (name.Contains("(software)", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    internal static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pantallazo", "encoder-cache.json");

    internal sealed class CacheEntry
    {
        public bool Hardware { get; set; }
        public string? Encoder { get; set; }
        public string? Reason { get; set; }
        public DateTime Date { get; set; }
    }

    internal static EncoderDecision Decide(GpuDevice gpu)
    {
        string key = gpu.CacheKey;
        var cache = LoadCache();
        if (cache.TryGetValue(key, out var e))
            return new EncoderDecision(e.Hardware, e.Encoder, e.Reason ?? "", FromCache: true);

        string? exe = ProbeExecutablePath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            // Sin proceso hijo: solo se mira qué encoder ofrece Windows para la placa (no se instancia). No se guarda.
            string? name = EnumerateHardwareEncoder(gpu.Luid);
            bool hw = IsVendorEncoder(name);
            return new EncoderDecision(hw, name, hw ? "enumerado (sin sondeo)" : $"sin encoder del fabricante ({name ?? "ninguno"})", false);
        }

        var (exit, output) = RunProbeProcess(exe, gpu.Luid);
        string? encoder = output.FirstOrDefault(l => l.StartsWith("ENCODER=", StringComparison.Ordinal))?[8..];
        string? err = output.FirstOrDefault(l => l.StartsWith("ERROR=", StringComparison.Ordinal))?[6..];
        bool ok = exit == 0 && output.Contains("OK");
        bool useHw = ok && IsVendorEncoder(encoder);
        string reason = useHw ? "sondeo OK"
            : !ok ? $"el sondeo falló (código {exit}{(err != null ? ": " + err : "")})"
            : $"Windows elige «{encoder ?? "ninguno"}»";
        RecordingLog.Write($"Sondeo de encoder en «{gpu.AdapterName}» ({gpu.DriverVersion}): {reason}");
        SaveEntry(key, new CacheEntry { Hardware = useHw, Encoder = encoder, Reason = reason, Date = DateTime.Now });
        return new EncoderDecision(useHw, encoder, reason, false);
    }

    /// <summary>El hardware falló de verdad grabando: la próxima vez se va directo a software (hasta cambiar de driver).</summary>
    internal static void MarkHardwareFailed(GpuDevice gpu, string? encoder, string reason)
    {
        SaveEntry(gpu.CacheKey, new CacheEntry { Hardware = false, Encoder = encoder, Reason = "falló grabando: " + reason, Date = DateTime.Now });
    }

    private static (int Exit, List<string> Output) RunProbeProcess(string exe, long luid)
    {
        var lines = new List<string>();
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(Argument);
            psi.ArgumentList.Add(GpuDevice.LuidText(luid));
            using var p = Process.Start(psi)!;
            p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data.Trim()); };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(20_000))
            {
                try { p.Kill(true); } catch { }
                return (-1, lines);
            }
            p.WaitForExit();
            lock (lines) return (p.ExitCode, [.. lines]);
        }
        catch (Exception ex)
        {
            RecordingLog.Write($"No se pudo lanzar el sondeo de encoder: {ex.Message}");
            return (-2, lines);
        }
    }

    /// <summary>
    /// Nombre de un encoder por hardware a partir de su URL (MFT_ENUM_HARDWARE_URL_Attribute): el transform creado por
    /// el sink writer no siempre trae MFT_FRIENDLY_NAME, pero su activador sí.
    /// </summary>
    internal static string? HardwareEncoderNameByUrl(string url)
    {
        try
        {
            MfRuntime.EnsureStarted();
            var output = new RegisterTypeInfo { GuidMajorType = MfGuids.MediaTypeVideo, GuidSubtype = MfGuids.VideoH264 };
            uint flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);
            MediaFactory.MFTEnum2(MfGuids.MFT_CATEGORY_VIDEO_ENCODER, flags, null, output, null!, out nint array, out uint count);
            string? found = null;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    using var act = new IMFActivate(Marshal.ReadIntPtr(array, i * IntPtr.Size));
                    if (found != null) continue;
                    try
                    {
                        if (string.Equals(act.GetAllocatedString(MfGuids.MFT_ENUM_HARDWARE_URL), url, StringComparison.OrdinalIgnoreCase))
                            found = act.GetAllocatedString(MfGuids.MFT_FRIENDLY_NAME);
                    }
                    catch { }
                }
            }
            finally { if (array != 0) Marshal.FreeCoTaskMem(array); }
            return found;
        }
        catch (Exception ex)
        {
            RecordingLog.Write($"MFTEnum2 (por URL) falló: {ex.Message}");
            return null;
        }
    }

    /// <summary>Primer encoder H.264 por hardware que Windows lista para el adaptador (MFTEnum2 + LUID).</summary>
    internal static string? EnumerateHardwareEncoder(long luid)
    {
        try
        {
            MfRuntime.EnsureStarted();
            using var attrs = MediaFactory.MFCreateAttributes(1);
            attrs.SetBlob(MfGuids.MFT_ENUM_ADAPTER_LUID, BitConverter.GetBytes(luid));
            var output = new RegisterTypeInfo { GuidMajorType = MfGuids.MediaTypeVideo, GuidSubtype = MfGuids.VideoH264 };
            uint flags = (uint)(EnumFlag.EnumFlagHardware | EnumFlag.EnumFlagSortandfilter);
            MediaFactory.MFTEnum2(MfGuids.MFT_CATEGORY_VIDEO_ENCODER, flags, null, output, attrs, out nint array, out uint count);
            string? first = null;
            try
            {
                for (int i = 0; i < count; i++)
                {
                    nint p = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                    using var act = new IMFActivate(p);
                    if (first == null)
                        try { first = act.GetAllocatedString(MfGuids.MFT_FRIENDLY_NAME); } catch { }
                }
            }
            finally { if (array != 0) Marshal.FreeCoTaskMem(array); }
            return first;
        }
        catch (Exception ex)
        {
            RecordingLog.Write($"MFTEnum2 falló: {ex.Message}");
            return null;
        }
    }

    private static Dictionary<string, CacheEntry> LoadCache()
    {
        lock (CacheLock)
        {
            try
            {
                if (File.Exists(CachePath))
                    return JsonSerializer.Deserialize<Dictionary<string, CacheEntry>>(File.ReadAllText(CachePath)) ?? [];
            }
            catch (Exception ex) { RecordingLog.Write($"encoder-cache.json ilegible, se ignora: {ex.Message}"); }
            return [];
        }
    }

    private static void SaveEntry(string key, CacheEntry entry)
    {
        lock (CacheLock)
        {
            try
            {
                var cache = LoadCache();
                cache[key] = entry;
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                string tmp = CachePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, CachePath, overwrite: true);
            }
            catch (Exception ex) { RecordingLog.Write($"No se pudo guardar encoder-cache.json: {ex.Message}"); }
        }
    }
}
