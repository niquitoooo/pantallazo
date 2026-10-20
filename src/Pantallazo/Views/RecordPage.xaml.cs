using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Microsoft.Win32;
using Pantallazo.Core;
using Pantallazo.Logging;
using Pantallazo.Recording;
using Pantallazo.Services;
using Pantallazo.Theming;

namespace Pantallazo.Views;

public sealed class MicRow : INotifyPropertyChanged
{
    private bool _enabled;
    private double _volume = 1;
    private double _level;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public bool IsDefault { get; init; }
    public string Detail => IsDefault ? "Micrófono predeterminado de Windows" : "Micrófono";

    public bool Enabled { get => _enabled; set { _enabled = value; On(); Changed?.Invoke(this, EventArgs.Empty); } }
    public double Volume { get => _volume; set { _volume = value; On(); On(nameof(VolumeText)); Changed?.Invoke(this, EventArgs.Empty); } }
    public string VolumeText => $"{Math.Round(_volume * 100)} %";
    public double Level { get => _level; set { _level = value; On(); } }

    public event EventHandler? Changed;
    public event PropertyChangedEventHandler? PropertyChanged;
    private void On([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
}

public sealed record DisplayChoice(DisplayInfo Display, string Label);

public partial class RecordPage : UserControl
{
    private readonly App _app = App.Instance;
    private readonly ObservableCollection<MicRow> _mics = [];
    private AudioLevelMonitor? _monitor;
    private bool _loading;

    public RecordPage()
    {
        InitializeComponent();
        MicList.ItemsSource = _mics;
        BuildFpsButtons();
        _app.Recording.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(UpdateState);
        _app.Recording.LevelsChanged += (_, levels) => ApplyLevels(levels);
        AudioDeviceCatalog.DevicesChanged += (_, _) => Dispatcher.BeginInvoke(async () => { await LoadDevicesAsync(); RestartMonitor(); });
        Loaded += async (_, _) =>
        {
            LoadFromSettings();
            await LoadDevicesAsync();
            RestartMonitor();
        };
        Unloaded += (_, _) => StopMonitor();
        IsVisibleChanged += (_, _) => { if (IsVisible) RestartMonitor(); else StopMonitor(); };
    }

    private AppSettings S => _app.Settings.Current;

    private void BuildFpsButtons()
    {
        var opts = AppSettings.FpsOptions;
        for (var i = 0; i < opts.Length; i++)
        {
            var fps = opts[i];
            var rb = new RadioButton
            {
                Style = (Style)FindResource("M3.Segment"),
                GroupName = "fps",
                Content = $"{fps} fps",
                Tag = fps,
            };
            M3.SetSegment(rb, i == 0 ? SegmentPosition.First : i == opts.Length - 1 ? SegmentPosition.Last : SegmentPosition.Middle);
            rb.Checked += (_, _) =>
            {
                if (!_loading) _app.Settings.Update(s => s.Fps = fps);
                UpdateFpsHint(fps);
            };
            FpsPanel.Children.Add(rb);
        }
    }

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            TargetAll.IsChecked = S.RecordTarget == RecordTarget.AllDisplays;
            TargetDisplay.IsChecked = S.RecordTarget == RecordTarget.Display;
            TargetRegion.IsChecked = S.RecordTarget == RecordTarget.Region;
            LoadDisplays();
            UpdateRegionText();

            AudioSystem.IsChecked = S.AudioMode == AudioMode.System;
            AudioMic.IsChecked = S.AudioMode == AudioMode.Microphones;
            AudioBoth.IsChecked = S.AudioMode == AudioMode.SystemAndMicrophones;
            AudioNone.IsChecked = S.AudioMode == AudioMode.None;
            SystemVolume.Value = S.SystemVolume;
            SystemVolumeText.Text = $"{Math.Round(S.SystemVolume * 100)} %";

            UpdateFpsOptions();
            foreach (RadioButton rb in FpsPanel.Children) rb.IsChecked = (int)rb.Tag == S.Fps;
            QLow.IsChecked = S.Quality == RecordQuality.Low;
            QBalanced.IsChecked = S.Quality == RecordQuality.Balanced;
            ResOriginal.IsChecked = S.Resolution == RecordResolution.Original;
            Res1080.IsChecked = S.Resolution == RecordResolution.P1080;
            Res720.IsChecked = S.Resolution == RecordResolution.P720;
            QHigh.IsChecked = S.Quality == RecordQuality.High;
            CursorSwitch.IsChecked = S.RecordCursor;
            FolderText.Text = _app.Recording.RecordingsFolder;
        }
        finally { _loading = false; }
        UpdateVisibility();
        UpdateState();
    }

    private void LoadDisplays()
    {
        try
        {
            var displays = DisplayCatalog.GetDisplays();
            var items = displays.Select((d, i) => new DisplayChoice(d,
                $"{d.FriendlyName} · {d.Width}×{d.Height} · {d.RefreshHz} Hz{(d.IsPrimary ? " · principal" : "")}")).ToList();
            var wasLoading = _loading;
            _loading = true;
            DisplayCombo.ItemsSource = items;
            DisplayCombo.SelectedItem = items.FirstOrDefault(c => c.Display.Id == S.RecordDisplayId) ?? items.FirstOrDefault(c => c.Display.IsPrimary);
            _loading = wasLoading;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudieron listar las pantallas: {ex.Message}");
        }
    }

    /// <summary>Enumerar dispositivos tarda ~0,5 s: se hace fuera del hilo de la interfaz.</summary>
    private async Task LoadDevicesAsync()
    {
        try
        {
            var (output, inputs) = await Task.Run(() => (AudioDeviceCatalog.GetDefaultOutput(), AudioDeviceCatalog.GetInputDevices()));
            OutputName.Text = output?.Name ?? "No hay ningún dispositivo de salida activo";
            _loading = true;
            var saved = S.Microphones.ToDictionary(m => m.Id);
            _mics.Clear();
            foreach (var d in inputs)
            {
                saved.TryGetValue(d.Id, out var st);
                var row = new MicRow
                {
                    Id = d.Id,
                    Name = d.Name,
                    IsDefault = d.IsDefault,
                    Enabled = st?.Enabled ?? (d.IsDefault && S.Microphones.Count == 0),
                    Volume = st?.Volume ?? 1,
                };
                row.Changed += (_, _) => SaveMics();
                _mics.Add(row);
            }
            NoMicsText.Visibility = _mics.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // Micrófonos guardados que ya no están conectados.
            var missing = S.Microphones.Where(m => m.Enabled && inputs.All(i => i.Id != m.Id)).Select(m => m.Name).ToList();
            AudioNote.Text = missing.Count > 0 ? $"No conectado: {string.Join(", ", missing)}. Se va a ignorar al grabar." : "";
            AudioNote.Visibility = missing.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudieron listar los dispositivos de audio: {ex.Message}");
            OutputName.Text = "No se pudieron leer los dispositivos de audio.";
        }
        finally { _loading = false; }
        UpdateVisibility();
    }

    private void SaveMics()
    {
        if (_loading) return;
        _app.Settings.Update(s =>
        {
            var keep = s.Microphones.Where(m => _mics.All(r => r.Id != m.Id)).ToList(); // desconectados: se recuerdan
            keep.AddRange(_mics.Select(r => new MicrophoneSetting { Id = r.Id, Name = r.Name, Enabled = r.Enabled, Volume = r.Volume }));
            s.Microphones = keep;
        });
        RestartMonitor();
    }

    private void UpdateVisibility()
    {
        UpdateResolutionHint();
        UpdateFpsHint(S.Fps);
        DisplayCombo.Visibility = S.RecordTarget == RecordTarget.Display ? Visibility.Visible : Visibility.Collapsed;
        RegionRow.Visibility = S.RecordTarget == RecordTarget.Region ? Visibility.Visible : Visibility.Collapsed;
        SystemRow.Visibility = S.AudioMode is AudioMode.System or AudioMode.SystemAndMicrophones ? Visibility.Visible : Visibility.Collapsed;
        MicSection.Visibility = S.AudioMode is AudioMode.Microphones or AudioMode.SystemAndMicrophones ? Visibility.Visible : Visibility.Collapsed;
        if (S.AudioMode is AudioMode.Microphones or AudioMode.SystemAndMicrophones && _mics.Count > 0 && _mics.All(m => !m.Enabled))
        {
            AudioNote.Text = "Marcá al menos un micrófono; si no, el video sale sin su audio.";
            AudioNote.Visibility = Visibility.Visible;
        }
    }

    private void UpdateState()
    {
        var r = _app.Recording;
        Elapsed.Text = r.ElapsedText;
        RecDot.Visibility = r.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        RecDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, r.IsPaused ? "M3.Outline" : "M3.Error");
        PauseButton.Visibility = r.IsRecording ? Visibility.Visible : Visibility.Collapsed;
        PauseButton.Content = r.IsPaused ? "Reanudar" : "Pausar";
        M3.SetIcon(PauseButton, r.IsPaused ? Icons.Play : Icons.Pause);
        var key = S.Hotkeys.FirstOrDefault(h => h.Action == HotkeyAction.Record && h.Gesture.IsValid)?.Gesture.ToString();
        StatusText.Text = r.IsPaused ? "En pausa. Lo que pase en pantalla no se graba hasta que reanudes."
            : r.IsRecording ? "Grabando. Los cambios de esta página se aplican a la próxima grabación." : r.StatusText;
        HintText.Text = key is null ? "Sin atajo asignado" : r.IsRecording ? $"{key} para detener" : $"{key} para empezar y detener";
        StartStopButton.IsEnabled = r.State is RecordingUiState.Idle || r.IsRecording;
        if (r.IsRecording)
        {
            StartStopButton.Content = "Detener grabación";
            StartStopButton.Style = (Style)FindResource("M3.ErrorButton");
            M3.SetIcon(StartStopButton, Icons.Stop);
            StopMonitor();
        }
        else
        {
            StartStopButton.Content = r.State == RecordingUiState.Idle ? "Iniciar grabación" : r.StatusText;
            StartStopButton.Style = (Style)FindResource("M3.FilledButton");
            M3.SetIcon(StartStopButton, Icons.Record);
            if (r.State == RecordingUiState.Idle && IsVisible && _monitor is null) RestartMonitor();
        }
    }

    // ---------- Medidores ----------

    private int _monitorGen;

    /// <summary>Abre los medidores en segundo plano (abrir WASAPI tarda ~0,5 s).</summary>
    private async void RestartMonitor()
    {
        StopMonitor();
        if (!IsVisible || _app.Recording.IsBusy) return;
        var gen = _monitorGen;
        var system = S.AudioMode is AudioMode.System or AudioMode.SystemAndMicrophones;
        var mics = S.AudioMode is AudioMode.Microphones or AudioMode.SystemAndMicrophones
            ? _mics.Where(m => m.Enabled).Select(m => m.Id).ToList() : [];
        if (!system && mics.Count == 0) return;
        try
        {
            var monitor = await Task.Run(() => new AudioLevelMonitor(system, mics));
            if (gen != _monitorGen || !IsVisible || _app.Recording.IsBusy)
            {
                _ = Task.Run(monitor.Dispose);
                return;
            }
            _monitor = monitor;
            _monitor.LevelsChanged += (_, levels) => ApplyLevels(levels);
            _monitor.SourceFailed += (_, id) => Dispatcher.BeginInvoke(() =>
            {
                var name = id == AudioSourceIds.System ? "el audio del sistema" : _mics.FirstOrDefault(m => m.Id == id)?.Name ?? "un micrófono";
                AudioNote.Text = $"No se pudo abrir {name}.";
                AudioNote.Visibility = Visibility.Visible;
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"Medidores de audio: {ex.Message}");
        }
    }

    private void StopMonitor()
    {
        _monitorGen++;
        var old = _monitor;
        _monitor = null;
        if (old is not null) _ = Task.Run(old.Dispose);
        SystemMeter.Value = 0;
        foreach (var m in _mics) m.Level = 0;
    }

    private void ApplyLevels(IReadOnlyList<AudioLevel> levels) => Dispatcher.BeginInvoke(() =>
    {
        foreach (var l in levels)
        {
            if (l.SourceId == AudioSourceIds.System) SystemMeter.Value = Smooth(SystemMeter.Value, l.Peak);
            else if (_mics.FirstOrDefault(m => m.Id == l.SourceId) is { } row) row.Level = Smooth(row.Level, l.Peak);
        }
    });

    /// <summary>Sube rápido y baja despacio, como un vúmetro; escala perceptiva (raíz) para que se vea la voz.</summary>
    private static double Smooth(double current, float peak)
    {
        var target = Math.Sqrt(Math.Clamp(peak, 0, 1));
        return target > current ? target : current * 0.82 + target * 0.18;
    }

    // ---------- Eventos ----------

    private async void OnStartStop(object sender, RoutedEventArgs e)
    {
        StopMonitor();
        await _app.Recording.ToggleAsync();
    }

    private void OnPause(object sender, RoutedEventArgs e) => _app.Recording.TogglePause();

    private void OnTarget(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var t = TargetDisplay.IsChecked == true ? RecordTarget.Display : TargetRegion.IsChecked == true ? RecordTarget.Region : RecordTarget.AllDisplays;
        _app.Settings.Update(s => s.RecordTarget = t);
        UpdateVisibility();
        if (t == RecordTarget.Region && S.RecordRegion is null) OnPickRegion(sender, e);
    }

    private void OnDisplayChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DisplayCombo.SelectedItem is not DisplayChoice c) return;
        _app.Settings.Update(s => s.RecordDisplayId = c.Display.Id);
        UpdateResolutionHint();
        UpdateFpsHint(S.Fps);
    }

    private void OnPickRegion(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        window?.Hide();
        try
        {
            Thread.Sleep(250); // como ShareX: que la ventana termine de ocultarse antes de congelar la pantalla
            var r = _app.Capture.PickRegion();
            if (r is { } rect)
            {
                // Los encoders H.264 necesitan medidas pares.
                var even = rect with { Width = Math.Max(2, rect.Width & ~1), Height = Math.Max(2, rect.Height & ~1) };
                _app.Settings.Update(s => s.RecordRegion = even);
            }
        }
        catch (Exception ex)
        {
            ToastWindow.ShowError("No se pudo elegir la región", ex.Message);
        }
        finally
        {
            window?.Show();
            window?.Activate();
        }
        UpdateRegionText();
    }

    /// <summary>Muestra a qué tamaño queda el video con la resolución elegida (mismo cálculo que el motor).</summary>
    private void UpdateResolutionHint()
    {
        if (ResolutionHint is null) return;
        var (w, h) = S.RecordTarget switch
        {
            RecordTarget.Display when DisplayCombo.SelectedItem is DisplayChoice c => (c.Display.Width, c.Display.Height),
            RecordTarget.Region when S.RecordRegion is { } r => (r.Width, r.Height),
            _ => (Capture.Screens.VirtualScreen().Width, Capture.Screens.VirtualScreen().Height),
        };
        int? max = S.Resolution switch { RecordResolution.P1080 => 1080, RecordResolution.P720 => 720, _ => null };
        (w, h) = (w & ~1, h & ~1);
        var (ow, oh) = max is int m && h > m ? ((int)(w * (double)m / h) & ~1, m) : (w, h);
        ResolutionHint.Text = (ow, oh) == (w, h)
            ? $"Se graba a {ow}×{oh}, la resolución real."
            : $"Se graba a {ow}×{oh} (la pantalla es de {w}×{h}): archivos más livianos, texto chico menos nítido.";
    }

    private void OnResolution(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var r = Res1080.IsChecked == true ? RecordResolution.P1080 : Res720.IsChecked == true ? RecordResolution.P720 : RecordResolution.Original;
        _app.Settings.Update(s => s.Resolution = r);
        UpdateResolutionHint();
    }

    private int _maxRefreshHz;

    /// <summary>
    /// Muestra 144 fps solo si hay un monitor que llegue (o si ya estaba elegido, para que se vea qué está puesto) y
    /// acomoda las puntas redondeadas del grupo.
    /// </summary>
    private void UpdateFpsOptions()
    {
        try { _maxRefreshHz = DisplayCatalog.GetDisplays().Select(d => d.RefreshHz).DefaultIfEmpty(60).Max(); }
        catch (Exception ex) { Log.Warn($"No se pudo leer la frecuencia de las pantallas: {ex.Message}"); }
        var shown = FpsPanel.Children.Cast<RadioButton>()
            .Where(rb => AppSettings.IsFpsOffered((int)rb.Tag, _maxRefreshHz) || (int)rb.Tag == S.Fps).ToList();
        foreach (RadioButton rb in FpsPanel.Children)
        {
            var i = shown.IndexOf(rb);
            rb.Visibility = i >= 0 ? Visibility.Visible : Visibility.Collapsed;
            if (i >= 0) M3.SetSegment(rb, i == 0 ? SegmentPosition.First : i == shown.Count - 1 ? SegmentPosition.Last : SegmentPosition.Middle);
        }
    }

    /// <summary>Frecuencia de lo que se va a grabar: la pantalla elegida, la que tiene la región o la más rápida.</summary>
    private int SelectedRefreshHz()
    {
        try
        {
            var displays = DisplayCatalog.GetDisplays();
            DisplayInfo? d = S.RecordTarget switch
            {
                RecordTarget.Display => (DisplayCombo.SelectedItem as DisplayChoice)?.Display,
                RecordTarget.Region when S.RecordRegion is { } r => displays.FirstOrDefault(x =>
                    r.X + r.Width / 2 >= x.X && r.X + r.Width / 2 < x.X + x.Width && r.Y + r.Height / 2 >= x.Y && r.Y + r.Height / 2 < x.Y + x.Height),
                _ => null,
            };
            return d?.RefreshHz ?? displays.Select(x => x.RefreshHz).DefaultIfEmpty(0).Max();
        }
        catch { return 0; }
    }

    private void UpdateFpsHint(int fps)
    {
        if (FpsHint is null) return;
        var hz = SelectedRefreshHz();
        if (hz > 0 && fps > hz)
        {
            FpsHint.Text = $"La pantalla que grabás va a {hz} Hz: de cada {fps} cuadros, como mucho {hz} son distintos y " +
                           "el resto se repite (el movimiento puede verse a saltos). Elegí hasta " +
                           $"{AppSettings.FpsOptions.Where(f => f <= hz).DefaultIfEmpty(30).Max()} fps.";
            return;
        }
        FpsHint.Text = fps > 60
            ? $"El video sale siempre a {fps} fps. Cuántos de esos cuadros son distintos depende de la placa: si no llega, " +
              "se repite el anterior. Rinde más a 1080p y con el modo de energía en \"Mejor rendimiento\"."
            : $"El video sale siempre a {fps} fps (si la pantalla está quieta, se repite el último cuadro).";
    }

    private void UpdateRegionText()
    {
        RegionText.Text = S.RecordRegion is { } r ? $"{r.Width} × {r.Height} en ({r.X}, {r.Y})" : "Todavía no elegiste una región.";
        UpdateResolutionHint();
    }

    private void OnAudioMode(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var m = AudioMic.IsChecked == true ? AudioMode.Microphones : AudioBoth.IsChecked == true ? AudioMode.SystemAndMicrophones
            : AudioNone.IsChecked == true ? AudioMode.None : AudioMode.System;
        _app.Settings.Update(s => s.AudioMode = m);
        if (m is AudioMode.Microphones or AudioMode.SystemAndMicrophones && _mics.Count > 0 && _mics.All(x => !x.Enabled))
            (_mics.FirstOrDefault(x => x.IsDefault) ?? _mics[0]).Enabled = true;
        UpdateVisibility();
        RestartMonitor();
    }

    private void OnSystemVolume(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (SystemVolumeText is null) return;
        SystemVolumeText.Text = $"{Math.Round(e.NewValue * 100)} %";
        if (!_loading) _app.Settings.Update(s => s.SystemVolume = e.NewValue);
    }

    private async void OnRefreshDevices(object sender, RoutedEventArgs e)
    {
        LoadDisplays();
        await LoadDevicesAsync();
        RestartMonitor();
    }

    private void OnQuality(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var q = QLow.IsChecked == true ? RecordQuality.Low : QHigh.IsChecked == true ? RecordQuality.High : RecordQuality.Balanced;
        _app.Settings.Update(s => s.Quality = q);
    }

    private void OnCursorSwitch(object sender, RoutedEventArgs e) =>
        _app.Settings.Update(s => s.RecordCursor = CursorSwitch.IsChecked == true);

    private void OnChangeFolder(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Carpeta para las grabaciones", InitialDirectory = _app.Recording.RecordingsFolder };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        _app.Settings.Update(s => s.RecordingsFolder = dlg.FolderName);
        FolderText.Text = _app.Recording.RecordingsFolder;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e) => Shell.OpenFolder(_app.Recording.RecordingsFolder);
}
