using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Pantallazo.Capture;
using Pantallazo.Core;
using Pantallazo.Native;
using Pantallazo.Services;
using Pantallazo.Theming;

namespace Pantallazo.Views;

/// <summary>
/// Indicador mientras se graba: una píldora flotante arriba al centro (punto rojo que late, tiempo y botones
/// Pausa y Detener) y, como ShareX, un borde punteado rojo alrededor de lo que se graba. Nada de esto sale en el video
/// (WDA_EXCLUDEFROMCAPTURE) y el borde deja pasar los clics.
/// </summary>
public sealed class RecordingIndicator : Window
{
    private readonly RecordingController _controller;
    private readonly TextBlock _time = new();
    private readonly List<Window> _borders = [];
    private readonly System.Windows.Threading.DispatcherTimer _keepOnTop;

    private void BringAllToFront()
    {
        const uint flags = NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE;
        foreach (var w in _borders.Append(this))
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != 0) NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, flags);
        }
    }
    private readonly PixelRect _anchor;
    private readonly Button _stop;
    private readonly Button _pause;
    private readonly Ellipse _dot;
    private static readonly Brush RecordingRed = Frozen(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly DoubleAnimation Pulse = new(1, 0.25, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };

    private static Brush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    private readonly Config.SettingsStore _settings;

    public RecordingIndicator(RecordingController controller, Config.SettingsStore settings, PixelRect? recorded)
    {
        _settings = settings;
        _controller = controller;
        var screens = Screens.All();
        var rects = recorded is { } r ? [r] : screens.Select(s => s.Bounds).ToList();
        _anchor = recorded ?? (screens.FirstOrDefault(s => s.IsPrimary)?.Bounds ?? Screens.VirtualScreen());

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Pantallazo - grabando";

        var dot = _dot = new Ellipse { Width = 10, Height = 10, Fill = RecordingRed, Margin = new Thickness(4, 0, 10, 0) };
        dot.BeginAnimation(OpacityProperty, Pulse);

        _time.FontFamily = new FontFamily("Cascadia Mono, Consolas");
        _time.FontSize = 14;
        _time.FontWeight = FontWeights.SemiBold;
        _time.VerticalAlignment = VerticalAlignment.Center;
        _time.Text = "00:00";
        _time.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnSurface");

        var pause = _pause = new Button { Margin = new Thickness(12, 0, 0, 0) };
        pause.Style = (Style)Application.Current.Resources["M3.TonalIconButton"];
        pause.Click += (_, _) => _controller.TogglePause();
        SetPausedLook(false);

        var stop = _stop = new Button { Content = "Detener", Margin = new Thickness(6, 0, 0, 0) };
        M3.SetIcon(stop, Icons.Stop);
        stop.Style = (Style)Application.Current.Resources["M3.ErrorButton"];
        stop.Padding = new Thickness(16, 0, 20, 0);
        stop.Click += async (_, _) => await _controller.StopAsync();

        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(dot);
        panel.Children.Add(_time);
        panel.Children.Add(pause);
        panel.Children.Add(stop);

        var pill = new Border
        {
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(14, 6, 6, 6),
            Margin = new Thickness(12),
            Child = panel,
            Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 2, Opacity = 0.3, Direction = 270 },
            Cursor = Cursors.SizeAll,
        };
        pill.SetResourceReference(Border.BackgroundProperty, "M3.SurfaceContainerHigh");
        pill.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is Button) return;
            DragMove();   // vuelve al soltar: se recuerda la posición para las próximas grabaciones
            if (NativeMethods.GetWindowRect(new WindowInteropHelper(this).Handle, out var r))
                _settings.Update(s => { s.IndicatorX = r.Left; s.IndicatorY = r.Top; });
        };
        Content = pill;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)(ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
            NativeMethods.ExcludeFromCapture(hwnd);
        };
        Loaded += (_, _) => { if (!PlaceSaved()) PlaceTopCenter(); };

        foreach (var rect in rects) _borders.Add(new BorderWindow(rect, outside: recorded is not null && screens.All(s => s.Bounds != rect)));
        foreach (var b in _borders) b.Show();
        // La barra de tareas también es "siempre visible" y Windows la sube al frente cuando se la toca, se abre
        // Inicio o se pasa el mouse: el borde de abajo quedaba tapado. Cada medio segundo se vuelven a poner
        // adelante (sin activarlas ni moverlas).
        _keepOnTop = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _keepOnTop.Tick += (_, _) => BringAllToFront();
        _keepOnTop.Start();
        Closed += (_, _) =>
        {
            _keepOnTop.Stop();
            foreach (var b in _borders) b.Close();
        };
    }

    public void SetElapsed(string text) => _time.Text = text;

    /// <summary>Mientras el encoder arranca (2–3 s con el driver AMD) se ve "Iniciando…" y no se puede detener.</summary>
    public void SetStarting(bool starting)
    {
        _stop.IsEnabled = !starting;
        _pause.IsEnabled = !starting;
        if (starting) _time.Text = "Iniciando…";
    }

    /// <summary>En pausa el punto queda quieto y gris, y el botón pasa a Reanudar.</summary>
    public void SetPaused(bool paused) => SetPausedLook(paused);

    private void SetPausedLook(bool paused)
    {
        M3.SetIcon(_pause, paused ? Icons.Play : Icons.Pause);
        System.Windows.Automation.AutomationProperties.SetName(_pause, paused ? "Reanudar" : "Pausar");
        if (_dot is null) return;
        if (paused)
        {
            _dot.BeginAnimation(OpacityProperty, null);
            _dot.Opacity = 1;
            _dot.SetResourceReference(Shape.FillProperty, "M3.Outline");
        }
        else
        {
            _dot.Fill = RecordingRed;
            _dot.BeginAnimation(OpacityProperty, Pulse);
        }
    }

    /// <summary>
    /// Lo pone donde el usuario lo dejó la última vez, si ese lugar sigue dentro de una pantalla (se ajusta para que
    /// entre entero). false si no hay posición guardada o quedó fuera (por ejemplo, se desconectó un monitor).
    /// </summary>
    private bool PlaceSaved()
    {
        if (_settings.Current.IndicatorX is not int x || _settings.Current.IndicatorY is not int y) return false;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
        int w = r.Width, h = r.Height;
        // Se valida contra el monitor entero (incluida la barra de tareas: el indicador va por encima de ella), no
        // contra el área de trabajo: soltarlo sobre la barra o pegado a un borde lo hacía volver arriba al centro.
        var rect = new PixelRect(x, y, w, h);
        var screens = Screens.All();
        var screen = screens.FirstOrDefault(s => s.Bounds.Contains(x + w / 2, y + h / 2))
                     ?? screens.OrderByDescending(s => Area(s.Bounds.Intersect(rect))).FirstOrDefault(s => Area(s.Bounds.Intersect(rect)) > 0);
        if (screen is null) return false;
        // La ventana tiene un margen transparente alrededor de la píldora (sombra): ese margen puede quedar afuera.
        int m = (int)Math.Round(12 * VisualTreeHelper.GetDpi(this).DpiScaleX);
        var area = screen.Bounds;
        x = Math.Clamp(x, area.X - m, Math.Max(area.X - m, area.Right - w + m));
        y = Math.Clamp(y, area.Y - m, Math.Max(area.Y - m, area.Bottom - h + m));
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        return true;
    }

    private static long Area(PixelRect r) => r.IsEmpty ? 0 : (long)r.Width * r.Height;

    private void PlaceTopCenter()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = (int)(ActualWidth * dpi.DpiScaleX);
        var screen = Screens.FromPoint(_anchor.X + _anchor.Width / 2, _anchor.Y + _anchor.Height / 2);
        var area = screen?.WorkArea ?? _anchor;
        var top = Math.Max(area.Y, _anchor.Y) + 8;
        var x = Math.Clamp(_anchor.X + (_anchor.Width - w) / 2, area.X, Math.Max(area.X, area.Right - w));
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOPMOST, x, top, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>Borde punteado de 1 px (oscuro + rojo desfasado, como ShareX) que no recibe clics.</summary>
    private sealed class BorderWindow : Window
    {
        private readonly PixelRect _rect;

        public BorderWindow(PixelRect rect, bool outside)
        {
            _rect = outside ? new PixelRect(rect.X - 2, rect.Y - 2, rect.Width + 4, rect.Height + 4) : rect;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            IsHitTestVisible = false;
            Left = -32000;
            Top = -32000;
            Width = 10;
            Height = 10;

            var grid = new Grid { IsHitTestVisible = false };
            var dark = new Rectangle { Stroke = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)), StrokeThickness = 2, StrokeDashArray = [3, 3] };
            var red = new Rectangle { Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), StrokeThickness = 2, StrokeDashArray = [3, 3], StrokeDashOffset = 3 };
            grid.Children.Add(dark);
            grid.Children.Add(red);
            Content = grid;

            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
                NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
                    (nint)(ex | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
                NativeMethods.ExcludeFromCapture(hwnd);
                // El borde va justo afuera de la región grabada cuando hay lugar (si no, sobre el borde).
                var r = _rect;
                NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, r.X, r.Y, r.Width, r.Height,
                    NativeMethods.SWP_NOACTIVATE);
                HwndSource.FromHwnd(hwnd)?.AddHook((nint h, int msg, nint w, nint l, ref bool handled) =>
                {
                    if (msg == 0x02E0) { handled = true; } // WM_DPICHANGED: mantener píxeles físicos
                    return 0;
                });
            };
        }
    }
}
