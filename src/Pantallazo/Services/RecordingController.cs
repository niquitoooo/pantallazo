using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Pantallazo.Config;
using Pantallazo.Core;
using Pantallazo.Logging;
using Pantallazo.Recording;
using Pantallazo.Views;

namespace Pantallazo.Services;

public enum RecordingUiState { Idle, Starting, Recording, Paused, Saving }

/// <summary>
/// Puente entre la interfaz y el motor de grabación (Pantallazo.Recording). Un atajo inicia, el mismo atajo
/// detiene. Publica estado y tiempo para la ventana, la bandeja y el indicador flotante.
/// </summary>
public sealed class RecordingController : INotifyPropertyChanged
{
    private readonly SettingsStore _settings;
    private readonly HistoryStore _history;
    private readonly DispatcherTimer _tick;
    private Recorder? _recorder;
    private RecordingIndicator? _indicator;
    private RecordingUiState _state;
    private string _elapsedText = "00:00";
    private string? _lastWarning;

    public RecordingController(SettingsStore settings, HistoryStore history)
    {
        _settings = settings;
        _history = history;
        _tick = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(250) };
        _tick.Tick += (_, _) => UpdateElapsed();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<IReadOnlyList<AudioLevel>>? LevelsChanged;

    public RecordingUiState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            Notify(nameof(State));
            Notify(nameof(IsBusy));
            Notify(nameof(IsRecording));
            Notify(nameof(IsPaused));
            Notify(nameof(StatusText));
        }
    }

    public bool IsBusy => State != RecordingUiState.Idle;
    /// <summary>Hay una grabación en curso (grabando o en pausa): se puede detener.</summary>
    public bool IsRecording => State is RecordingUiState.Recording or RecordingUiState.Paused;
    public bool IsPaused => State == RecordingUiState.Paused;

    public string ElapsedText
    {
        get => _elapsedText;
        private set { _elapsedText = value; Notify(nameof(ElapsedText)); }
    }

    public string StatusText => State switch
    {
        RecordingUiState.Starting => "Preparando la grabación…",
        RecordingUiState.Recording => "Grabando",
        RecordingUiState.Paused => "En pausa",
        RecordingUiState.Saving => "Guardando el video…",
        _ => "Lista para grabar",
    };

    public string? LastWarning
    {
        get => _lastWarning;
        private set { _lastWarning = value; Notify(nameof(LastWarning)); }
    }

    public string RecordingsFolder => _settings.Current.RecordingsFolder ?? AppPaths.DefaultRecordingsFolder;

    public Task ToggleAsync() => State switch
    {
        RecordingUiState.Idle => StartAsync(),
        RecordingUiState.Recording or RecordingUiState.Paused => StopAsync(),
        _ => Task.CompletedTask, // iniciando o guardando: se ignora el atajo
    };

    public async Task StartAsync()
    {
        if (State != RecordingUiState.Idle) return;
        State = RecordingUiState.Starting;
        // El tono va antes de arrancar el motor: dura 0,12 s y el arranque ~0,45–0,7 s, y el motor descarta todo el
        // audio anterior al primer cuadro. Si sonara después, el audio del sistema lo grababa.
        if (_settings.Current.PlaySound) Sounds.PlayRecordStart();
        LastWarning = null;
        var options = BuildOptions(out var region);
        Log.Info($"Grabación: inicio {options.Target} {options.Fps} fps {options.Quality}, sistema={options.SystemAudio}, mics={options.Microphones.Count}");

        var recorder = new Recorder();
        recorder.Warning += (_, msg) => OnUi(() =>
        {
            LastWarning = msg;
            Log.Warn($"Grabación: {msg}");
            ToastWindow.ShowInfo("Grabación", msg);
        });
        recorder.LevelsChanged += (_, levels) => OnUi(() => LevelsChanged?.Invoke(this, levels));
        recorder.Finished += (_, result) => OnUi(() => OnFinished(result));
        _recorder = recorder;

        try
        {
            Directory.CreateDirectory(RecordingsFolder);
            // Primero se lanza el motor (trabaja en otro hilo) y mientras arranca se arma el indicador con
            // "Iniciando…": crear la primera ventana de WPF tarda ~0,3 s y no tiene que demorar la grabación.
            var start = recorder.StartAsync(options);
            _indicator = new RecordingIndicator(this, _settings, region);
            _indicator.SetStarting(true);
            _indicator.Show();
            await start;
        }
        catch (Exception ex)
        {
            _indicator?.Close();
            _indicator = null;
            Log.Error("No se pudo iniciar la grabación", ex);
            recorder.Dispose();
            _recorder = null;
            State = RecordingUiState.Idle;
            ToastWindow.ShowError("No se pudo iniciar la grabación", ex.Message);
            return;
        }

        State = RecordingUiState.Recording;
        _indicator?.SetStarting(false);
        _tick.Start();
        UpdateElapsed();
    }

    /// <summary>Pausa o reanuda. El archivo final no tiene hueco: sigue desde donde se pausó.</summary>
    public void TogglePause()
    {
        if (_recorder is null) return;
        if (State == RecordingUiState.Recording && _recorder.Pause())
        {
            State = RecordingUiState.Paused;
            Log.Info($"Grabación: pausa en {Format(_recorder.Elapsed)}");
        }
        else if (State == RecordingUiState.Paused && _recorder.Resume())
        {
            State = RecordingUiState.Recording;
            Log.Info($"Grabación: reanuda en {Format(_recorder.Elapsed)}");
        }
        else return;
        _indicator?.SetPaused(IsPaused);
        UpdateElapsed();
    }

    public async Task StopAsync()
    {
        if (!IsRecording || _recorder is null) return;
        State = RecordingUiState.Saving;
        var recorder = _recorder;
        RecordingResult result;
        try
        {
            result = await recorder.StopAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Error al detener la grabación", ex);
            result = new RecordingResult(false, null, ex.Message, [], TimeSpan.Zero, 0, 0);
        }
        OnFinished(result);
    }

    private bool _finishing;

    private void OnFinished(RecordingResult result)
    {
        if (_recorder is null || _finishing) return;
        _finishing = true;
        try
        {
            _tick.Stop();
            _indicator?.Close();
            _indicator = null;
            var recorder = _recorder;
            _recorder = null;
            try { recorder.Dispose(); } catch (Exception ex) { Log.Warn($"Al liberar el grabador: {ex.Message}"); }
            State = RecordingUiState.Idle;
            ElapsedText = "00:00";
            if (_settings.Current.PlaySound) Sounds.PlayRecordStop();

            if (result.Success && result.FilePath is not null)
            {
                Log.Info($"Grabación guardada: {result.Width}×{result.Height}, {result.Duration} → {result.FilePath}");
                _history.Add(new HistoryItem(result.FilePath, HistoryKind.Recording, DateTime.Now));
                var extra = result.Warnings.Count > 0 ? " · " + string.Join(" ", result.Warnings) : "";
                ToastWindow.ShowInfo("Grabación guardada", $"{Path.GetFileName(result.FilePath)} · {Format(result.Duration)}{extra}", result.FilePath);
            }
            else
            {
                var msg = result.Error ?? "La grabación no se pudo guardar.";
                if (result.FilePath is not null) msg += $" Archivo: {result.FilePath}";
                Log.Warn($"Grabación fallida: {msg}");
                ToastWindow.ShowError("La grabación no se guardó", msg);
            }
        }
        finally { _finishing = false; }
    }

    private RecordingOptions BuildOptions(out PixelRect? region)
    {
        var s = _settings.Current;
        region = null;
        var target = s.RecordTarget switch
        {
            RecordTarget.Display => CaptureTargetKind.Display,
            RecordTarget.Region when s.RecordRegion is { IsEmpty: false } => CaptureTargetKind.Region,
            _ => CaptureTargetKind.AllDisplays,
        };
        if (target == CaptureTargetKind.Region) region = s.RecordRegion;
        if (target == CaptureTargetKind.Display)
        {
            var d = DisplayCatalog.GetDisplays().FirstOrDefault(x => x.Id == s.RecordDisplayId)
                    ?? DisplayCatalog.GetDisplays().FirstOrDefault(x => x.IsPrimary);
            if (d is not null) region = new PixelRect(d.X, d.Y, d.Width, d.Height);
        }

        var system = s.AudioMode is AudioMode.System or AudioMode.SystemAndMicrophones;
        var mics = s.AudioMode is AudioMode.Microphones or AudioMode.SystemAndMicrophones
            ? s.Microphones.Where(m => m.Enabled).Select(m => new MicrophoneSelection(m.Id, (float)m.Volume)).ToList()
            : [];
        var folder = RecordingsFolder;
        return new RecordingOptions
        {
            Target = target,
            DisplayId = target == CaptureTargetKind.Display ? s.RecordDisplayId : null,
            Region = region is { } r && target == CaptureTargetKind.Region ? (r.X, r.Y, r.Width, r.Height) : null,
            Fps = s.Fps,
            MaxOutputHeight = s.Resolution switch { RecordResolution.P1080 => 1080, RecordResolution.P720 => 720, _ => null },
            Quality = s.Quality switch { RecordQuality.Low => VideoQuality.Low, RecordQuality.High => VideoQuality.High, _ => VideoQuality.Balanced },
            CaptureCursor = s.RecordCursor,
            SystemAudio = system,
            SystemAudioGain = (float)s.SystemVolume,
            Microphones = mics,
            OutputPath = FileNames.MakeUnique(folder, FileNaming.Next(_settings, folder, recording: true, DateTime.Now, ForegroundInfo.Get())),
        };
    }

    private void UpdateElapsed()
    {
        if (_recorder is not null) ElapsedText = Format(_recorder.Elapsed);
        _indicator?.SetElapsed(ElapsedText);
    }

    public static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) a(); else d.BeginInvoke(a);
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
