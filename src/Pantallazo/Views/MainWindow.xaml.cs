using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Pantallazo.Native;
using Pantallazo.Services;
using Pantallazo.Theming;

namespace Pantallazo.Views;

public enum MainPage { Home, Record, Settings }

public partial class MainWindow : Window
{
    private readonly App _app = App.Instance;
    private HomePage? _home;
    private RecordPage? _record;
    private SettingsPage? _settings;

    public MainWindow()
    {
        InitializeComponent();
        var s = _app.Settings.Current;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/pantallazo.ico"));
        TitleIcon.Source = TitleIconImage();
        SourceInitialized += (_, _) =>
        {
            _app.Theme.ApplyToWindow(this);
            HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(CaptionHook);
        };
        StateChanged += (_, _) => UpdateMaximized();
        Activated += (_, _) =>
        {
            UpdateActive();
            // Por si algo se borró sin aviso (otra unidad, red). Con prioridad baja, para que antes se procesen los
            // avisos de la carpeta pendientes: un archivo recién renombrado no tiene que salir de la lista.
            Dispatcher.BeginInvoke(_app.History.Prune, System.Windows.Threading.DispatcherPriority.Background);
        };
        Deactivated += (_, _) => UpdateActive();
        Navigate(MainPage.Home);
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
                _app.Settings.Current.WindowWidth = Width; // se guarda al cerrar
            if (WindowState == WindowState.Normal)
                _app.Settings.Current.WindowHeight = Height;
        };
    }

    public void Navigate(MainPage page)
    {
        var radio = page switch { MainPage.Record => NavRecord, MainPage.Settings => NavSettings, _ => NavHome };
        if (radio.IsChecked != true) radio.IsChecked = true; // dispara OnNav
        else Show(page);
    }

    private void OnNav(object sender, RoutedEventArgs e)
    {
        if (PageHost is null) return;
        Show(sender == NavRecord ? MainPage.Record : sender == NavSettings ? MainPage.Settings : MainPage.Home);
    }

    private void Show(MainPage page)
    {
        UserControl view = page switch
        {
            MainPage.Record => _record ??= new RecordPage(),
            MainPage.Settings => _settings ??= new SettingsPage(),
            _ => _home ??= new HomePage(),
        };
        if (PageHost.Content == view) return;
        PageHost.Content = view;
        // Transición M3 "fade through" corta.
        view.Opacity = 0;
        view.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase() });
        if (page == MainPage.Settings) _settings?.Dispatcher.BeginInvoke(() => { });
    }

    public void ShowShortcuts()
    {
        Navigate(MainPage.Settings);
        _settings?.Dispatcher.BeginInvoke(() => _settings.ScrollToShortcuts(), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    // ---------- Barra de título ----------

    /// <summary>El ícono dibujado en vectores: nítido con cualquier escala (el .ico de 16 px se veía borroso).</summary>
    private static DrawingImage TitleIconImage()
    {
        var group = new DrawingGroup();
        using (var dc = group.Open()) AppIconArt.Draw(dc, 16);
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximize(object sender, RoutedEventArgs e) => ToggleMaximized();
    private void OnCloseButton(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximized() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>
    /// Maximizada, Windows deja el marco invisible afuera de la pantalla y la ventana se pasa unos píxeles de cada
    /// lado: se compensa con un margen igual a lo que sobresale del área de trabajo.
    /// </summary>
    private void UpdateMaximized()
    {
        var maximized = WindowState == WindowState.Maximized;
        M3.SetIcon(MaxButton, maximized ? Icons.ChromeRestore : Icons.ChromeMaximize);
        System.Windows.Automation.AutomationProperties.SetName(MaxButton, maximized ? "Restaurar" : "Maximizar");
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!maximized || hwnd == 0 || !NativeMethods.GetWindowRect(hwnd, out var r))
        {
            Root.Margin = new Thickness(0);
            return;
        }
        var info = new NativeMethods.MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
        NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, 2), ref info);
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = info.rcWork;
        Root.Margin = new Thickness(
            Math.Max(0, w.Left - r.Left) / dpi.DpiScaleX, Math.Max(0, w.Top - r.Top) / dpi.DpiScaleY,
            Math.Max(0, r.Right - w.Right) / dpi.DpiScaleX, Math.Max(0, r.Bottom - w.Bottom) / dpi.DpiScaleY);
    }

    /// <summary>Como en Windows 11: sin foco, el nombre y los símbolos de los botones se apagan.</summary>
    private void UpdateActive()
    {
        TitleContent.Opacity = IsActive ? 1 : 0.55;
        foreach (var b in new[] { MinButton, MaxButton, CloseButton })
            b.SetResourceReference(ForegroundProperty, IsActive ? "M3.OnSurface" : "M3.Outline");
    }

    private const int WM_NCHITTEST = 0x84, WM_NCMOUSEMOVE = 0xA0, WM_NCLBUTTONDOWN = 0xA1, WM_NCLBUTTONUP = 0xA2,
        WM_NCLBUTTONDBLCLK = 0xA3, WM_NCMOUSELEAVE = 0x2A2, HTMAXBUTTON = 9;
    private bool _maxPressed;

    /// <summary>
    /// Para que al apoyar el mouse sobre maximizar aparezca el menú de acomodar ventanas (Snap Layouts), Windows
    /// tiene que ver ese lugar como su botón de maximizar (HTMAXBUTTON). Entonces los clics llegan como mensajes
    /// del marco y acá se hacen a mano el resaltado, el apretado y el clic.
    /// </summary>
    private nint CaptionHook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                if (IsOverMaxButton(lParam)) { handled = true; return HTMAXBUTTON; }
                break;
            case WM_NCMOUSEMOVE:
                if (wParam == HTMAXBUTTON)
                {
                    if (MaxButton.Tag is null)
                    {
                        MaxButton.Tag = "hover";
                        var tme = new NativeMethods.TRACKMOUSEEVENT
                        {
                            cbSize = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.TRACKMOUSEEVENT>(),
                            dwFlags = NativeMethods.TME_LEAVE | NativeMethods.TME_NONCLIENT, hwndTrack = hwnd,
                        };
                        NativeMethods.TrackMouseEvent(ref tme);
                    }
                }
                else ClearMaxButton();
                break;
            case WM_NCLBUTTONDOWN or WM_NCLBUTTONDBLCLK when wParam == HTMAXBUTTON:
                _maxPressed = true;
                MaxButton.Tag = "pressed";
                handled = true;
                break;
            case WM_NCLBUTTONUP when wParam == HTMAXBUTTON:
                if (_maxPressed) ToggleMaximized();
                ClearMaxButton();
                handled = true;
                break;
            case WM_NCMOUSELEAVE:
                ClearMaxButton();
                break;
        }
        return 0;
    }

    private void ClearMaxButton()
    {
        _maxPressed = false;
        MaxButton.Tag = null;
    }

    private bool IsOverMaxButton(nint lParam)
    {
        if (!MaxButton.IsVisible || PresentationSource.FromVisual(MaxButton) is null) return false;
        int x = (short)(lParam & 0xFFFF), y = (short)((lParam >> 16) & 0xFFFF);
        var a = MaxButton.PointToScreen(new Point(0, 0));
        var b = MaxButton.PointToScreen(new Point(MaxButton.ActualWidth, MaxButton.ActualHeight));
        return x >= a.X && x < b.X && y >= a.Y && y < b.Y;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _app.Settings.Save();
        if (_app.IsExiting) return;
        if (_app.Settings.Current.CloseToTray)
        {
            // Como ShareX: cerrar oculta la ventana y la app sigue en la bandeja.
            e.Cancel = true;
            Hide();
            if (!_app.Settings.Current.TrayHintShown)
            {
                _app.Settings.Update(s => s.TrayHintShown = true);
                ToastWindow.ShowInfo("Pantallazo sigue en la bandeja", "Los atajos siguen activos. Para salir, usá el menú del ícono de la bandeja.");
            }
            return;
        }
        if (!_app.ConfirmExitIfRecording())
        {
            e.Cancel = true;
            return;
        }
        _app.ExitApp();
    }
}
