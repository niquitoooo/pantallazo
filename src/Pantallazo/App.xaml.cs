using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Pantallazo.Config;
using Pantallazo.Core;
using Pantallazo.Hotkeys;
using Pantallazo.Logging;
using Pantallazo.Native;
using Pantallazo.Services;
using Pantallazo.Theming;
using Pantallazo.Views;

namespace Pantallazo;

public partial class App : Application
{
    public static App Instance => (App)Current;

    public SettingsStore Settings { get; } = new();
    public ThemeManager Theme { get; private set; } = null!;
    public HotkeyService Hotkeys { get; private set; } = null!;
    public HistoryStore History { get; } = new();
    public CaptureController Capture { get; private set; } = null!;
    public RecordingController Recording { get; private set; } = null!;
    public TrayIcon? Tray { get; private set; }
    public MainWindow? Main { get; private set; }
    public bool IsExiting { get; private set; }

    /// <summary>
    /// Autoprueba visual: renderiza cada sección en claro y oscuro a PNG en <paramref name="folder"/>,
    /// sin atajos globales ni bandeja (no toca el resto del escritorio).
    /// </summary>
    public async void RenderUi(string folder, string? seed = null)
    {
        string? savedSeed = null;
        try
        {
            Directory.CreateDirectory(folder);
            Settings.Load();
            // Color de prueba solo en memoria: se restaura antes de cerrar porque la ventana guarda la configuración.
            savedSeed = Settings.Current.SeedColor;
            if (seed is not null) Settings.Current.SeedColor = seed;
            History.Load(Settings);
            Theme = new ThemeManager();
            Capture = new CaptureController(Settings, History);
            Recording = new RecordingController(Settings, History);
            Hotkeys = new HotkeyService();
            Hotkeys.Apply([]); // nada registrado: solo para que las páginas tengan estados
            Main = new MainWindow { Left = -20000, Top = 0, ShowActivated = false, Width = 1100, Height = 820 };
            Main.Show();
            foreach (var dark in new[] { false, true })
            {
                Theme.Configure(dark ? ThemePreference.Dark : ThemePreference.Light, Settings.Current.SeedColor);
                foreach (var page in Enum.GetValues<MainPage>())
                {
                    try { Main.Navigate(page); }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(folder, $"error-{page}.txt"), ex.ToString()); }
                    await Task.Delay(1500);
                    var name = $"{page}-{(dark ? "oscuro" : "claro")}";
                    SaveVisual((FrameworkElement)Main.Content, Path.Combine(folder, $"{name}.png"), "M3.Surface");
                    // Y la sección completa (lo que queda abajo del scroll), con su alto real.
                    if (FindChild<System.Windows.Controls.ScrollViewer>(Main) is { Content: FrameworkElement full })
                        SaveVisual(full, Path.Combine(folder, $"{name}-completo.png"), "M3.SurfaceContainerLow");
                }
            }
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString());
        }
        Settings.Current.SeedColor = savedSeed;
        IsExiting = true;
        Shutdown();
    }

    private void SaveVisual(FrameworkElement element, string path, string background)
    {
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)(element.ActualWidth * 1.5), (int)(element.ActualHeight * 1.5), 144, 144, System.Windows.Media.PixelFormats.Pbgra32);
        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var area = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            dc.DrawRectangle((System.Windows.Media.Brush)Resources[background], null, area);
            dc.DrawRectangle(new System.Windows.Media.VisualBrush(element), null, area);
        }
        rtb.Render(dv);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static T? FindChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Arranca todo. <paramref name="silent"/> = inicio con Windows (sin abrir la ventana).</summary>
    public void Start(bool silent)
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log.Error("Error no controlado en la interfaz", e.Exception);
            ToastWindow.ShowError("Algo salió mal", e.Exception.Message);
            e.Handled = true;
        };
        KeyNames.SystemKeyName = NativeMethods.KeyNameFromVirtualKey;

        Settings.Load();
        History.Load(Settings);
        WheelScroll.Register();
        Theme = new ThemeManager();
        Theme.Configure(Settings.Current.Theme, Settings.Current.SeedColor);

        Capture = new CaptureController(Settings, History);
        Recording = new RecordingController(Settings, History);
        Capture.BeforeCaptureFromUi = HideMainForCaptureAsync;
        Capture.AfterCaptureFromUi = RestoreMainAfterCapture;

        Hotkeys = new HotkeyService();
        Hotkeys.Pressed += async (_, action) => await RunAsync(action);
        Hotkeys.StatusChanged += (_, _) => UpdateTrayTip();
        ApplyHotkeys();

        Tray = new TrayIcon(BuildTrayMenu);
        Tray.Clicked += async (_, _) =>
        {
            if (Recording.IsRecording) await Recording.StopAsync();
            else await Capture.CaptureRegionAsync();
        };
        Tray.DoubleClicked += (_, _) => ShowMainWindow();
        Recording.PropertyChanged += (_, _) => Dispatcher.BeginInvoke(UpdateTrayTip);
        UpdateTrayTip();

        // Reflejar en la configuración si el usuario quitó el inicio con Windows desde afuera.
        if (Settings.Current.StartWithWindows != StartupManager.IsEnabled())
            Settings.Update(s => s.StartWithWindows = StartupManager.IsEnabled());

        // El primer sondeo del encoder tarda ~1,7 s: se hace ahora para que el primer Alt+Impr Pant arranque antes.
        _ = Pantallazo.Recording.EncoderProbe.PrepareAsync();

        Log.Info($"Pantallazo iniciado (silencioso={silent}). Capturas: {Capture.ScreenshotsFolder} · Grabaciones: {Recording.RecordingsFolder}");
        if (!silent || !Settings.Current.StartMinimized) ShowMainWindow();
    }

    public void ApplyHotkeys() => Hotkeys.Apply(Settings.Current.Hotkeys, Settings.Current.PrintScreenPaused);

    /// <summary>Configuración → "Pausar Impr Pant": en pausa, Windows usa la tecla como si Pantallazo no estuviera.</summary>
    public void SetPrintScreenPaused(bool paused)
    {
        if (Settings.Current.PrintScreenPaused == paused) return;
        Settings.Update(s =>
        {
            if (paused) PrintScreenKey.Windows.Pause(s);
            else PrintScreenKey.Windows.Resume(s);
        });
        ApplyHotkeys();
    }

    private async Task RunAsync(HotkeyAction action)
    {
        try
        {
            switch (action)
            {
                case HotkeyAction.FullScreen: await Capture.CaptureFullScreenAsync(); break;
                case HotkeyAction.Region: await Capture.CaptureRegionAsync(); break;
                case HotkeyAction.Record: await Recording.ToggleAsync(); break;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Error en la acción {action}", ex);
            ToastWindow.ShowError("Algo salió mal", ex.Message);
        }
    }

    // ---------- Ventana principal ----------

    public void ShowMainWindow(MainPage? page = null)
    {
        Main ??= new MainWindow();
        if (page == MainPage.Settings) Main.ShowShortcuts();
        else if (page is { } p) Main.Navigate(p);
        if (!Main.IsVisible) Main.Show();
        if (Main.WindowState == WindowState.Minimized) Main.WindowState = WindowState.Normal;
        Main.Activate();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(Main).Handle;
        NativeMethods.SetForegroundWindow(hwnd);
    }

    private bool _hiddenForCapture;

    /// <summary>Si la captura se pidió desde la ventana, se oculta y se esperan 250 ms (como ShareX).</summary>
    private async Task HideMainForCaptureAsync()
    {
        if (Main is { IsVisible: true })
        {
            Main.Hide();
            _hiddenForCapture = true;
            await Task.Delay(250);
        }
    }

    private void RestoreMainAfterCapture()
    {
        if (!_hiddenForCapture || Main is null) return;
        _hiddenForCapture = false;
        Main.Show();
        Main.Activate();
    }

    // ---------- Bandeja ----------

    private ContextMenu BuildTrayMenu()
    {
        var menu = new ContextMenu { Style = (Style)Resources["M3.Menu"] };
        MenuItem Item(string header, string icon, string? gesture, Action onClick, bool enabled = true)
        {
            var mi = new MenuItem { Header = header, Style = (Style)Resources["M3.MenuItem"], InputGestureText = gesture ?? "", IsEnabled = enabled };
            M3.SetIcon(mi, icon);
            mi.Click += (_, _) => onClick();
            return mi;
        }
        string? Key(HotkeyAction a) => Settings.Current.Hotkeys.FirstOrDefault(h => h.Action == a && h.Gesture.IsValid &&
            !(Settings.Current.PrintScreenPaused && h.Gesture.UsesPrintScreen))?.Gesture.ToString();
        Separator Sep() => new() { Style = (Style)Resources["M3.MenuSeparator"] };

        menu.Items.Add(Item("Capturar pantalla completa", Icons.FullScreen, Key(HotkeyAction.FullScreen), async () =>
        {
            await Task.Delay(200); // que el menú termine de cerrarse
            await Capture.CaptureFullScreenAsync();
        }));
        menu.Items.Add(Item("Seleccionar región", Icons.Region, Key(HotkeyAction.Region), async () =>
        {
            await Task.Delay(200);
            await Capture.CaptureRegionAsync();
        }));
        menu.Items.Add(Recording.IsRecording
            ? Item($"Detener grabación ({Recording.ElapsedText})", Icons.Stop, Key(HotkeyAction.Record), async () => await Recording.StopAsync())
            : Item("Iniciar grabación", Icons.Record, Key(HotkeyAction.Record), async () => await Recording.StartAsync(), !Recording.IsBusy));
        if (Recording.IsRecording)
            menu.Items.Add(Recording.IsPaused
                ? Item("Reanudar grabación", Icons.Play, null, Recording.TogglePause)
                : Item("Pausar grabación", Icons.Pause, null, Recording.TogglePause));
        menu.Items.Add(Sep());
        menu.Items.Add(Item("Carpeta de capturas", Icons.Folder, null, () => Shell.OpenFolder(Capture.ScreenshotsFolder)));
        menu.Items.Add(Item("Carpeta de grabaciones", Icons.Folder, null, () => Shell.OpenFolder(Recording.RecordingsFolder)));
        menu.Items.Add(Sep());
        menu.Items.Add(Item("Abrir Pantallazo", Icons.Home, null, () => ShowMainWindow()));
        menu.Items.Add(Item("Salir", Icons.Power, null, () =>
        {
            if (ConfirmExitIfRecording()) ExitApp();
        }));
        return menu;
    }

    private void UpdateTrayTip()
    {
        if (Tray is null) return;
        if (Recording.IsRecording)
        {
            Tray.SetRecording(true, Recording.IsPaused ? "Pantallazo - en pausa. Clic para detener." : "Pantallazo - grabando. Clic para detener.");
            return;
        }
        var failed = Hotkeys.Failed.Count();
        Tray.SetRecording(false, failed > 0 ? $"Pantallazo - {failed} atajo(s) sin activar"
            : Settings.Current.PrintScreenPaused ? "Pantallazo - Impr Pant en pausa" : "Pantallazo");
    }

    // ---------- Salida ----------

    /// <summary>Con una grabación en curso, pregunta antes de salir. true = se puede salir.</summary>
    public bool ConfirmExitIfRecording()
    {
        if (!Recording.IsBusy) return true;
        var r = MessageBox.Show(
            "Hay una grabación en curso. Si salís ahora se detiene y se guarda lo grabado hasta acá.\n\n¿Salir igual?",
            "Pantallazo", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        return r == MessageBoxResult.Yes;
    }

    public async void ExitApp()
    {
        if (IsExiting) return;
        IsExiting = true;
        try
        {
            if (Recording.IsRecording) await Recording.StopAsync();
        }
        catch (Exception ex) { Log.Error("Al detener la grabación para salir", ex); }
        Settings.Save();
        Hotkeys.Dispose();
        Tray?.Dispose();
        Main?.Close();
        Log.Info("Pantallazo cerrado.");
        Shutdown();
    }
}
