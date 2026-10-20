using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Pantallazo.Capture;
using Pantallazo.Native;
using Pantallazo.Services;
using Pantallazo.Theming;

namespace Pantallazo.Views;

/// <summary>
/// Aviso discreto abajo a la derecha (como el toast de ShareX: 3 s + 1 s de fundido, el mouse encima lo pausa,
/// clic abre el archivo, clic derecho cierra). Hay una sola instancia; no roba el foco y no sale en capturas.
/// </summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;

    private readonly DispatcherTimer _timer = new();
    private readonly string? _openPath;
    private bool _closing;

    public static void ShowSaved(string title, string path, BitmapSource? preview) =>
        Show(new ToastWindow(title, Path.GetFileName(path), path, preview, isError: false, seconds: 3));

    public static void ShowInfo(string title, string message, string? openPath = null) =>
        Show(new ToastWindow(title, message, openPath, null, isError: false, seconds: 4));

    public static void ShowError(string title, string message) =>
        Show(new ToastWindow(title, message, null, null, isError: true, seconds: 7));

    public static void CloseCurrent() => _current?.Close();

    private static void Show(ToastWindow w)
    {
        _current?.Close();
        _current = w;
        w.Show();
    }

    private ToastWindow(string title, string message, string? openPath, BitmapSource? preview, bool isError, double seconds)
    {
        _openPath = openPath;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "Pantallazo";
        Cursor = openPath is null ? Cursors.Arrow : Cursors.Hand;

        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12),
            Margin = new Thickness(16),
            Width = 360,
            Effect = new DropShadowEffect { BlurRadius = 16, ShadowDepth = 3, Opacity = 0.3, Direction = 270 },
        };
        card.SetResourceReference(Border.BackgroundProperty, isError ? "M3.ErrorContainer" : "M3.SurfaceContainerHigh");

        var stack = new StackPanel();
        if (preview is not null)
        {
            var img = new Image
            {
                Source = preview,
                Stretch = Stretch.Uniform,
                MaxHeight = 190,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            stack.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(8),
                ClipToBounds = true,
                Margin = new Thickness(0, 0, 0, 10),
                Child = img,
            });
        }

        var row = new DockPanel();
        var icon = new TextBlock
        {
            Text = isError ? Icons.Error : Icons.Check,
            Style = (Style)Application.Current.Resources["M3.Icon"],
            Margin = new Thickness(4, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, isError ? "M3.OnErrorContainer" : "M3.Primary");
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);

        var texts = new StackPanel();
        var t = new TextBlock { Text = title, Style = (Style)Application.Current.Resources["M3.TitleSmall"] };
        var m = new TextBlock { Text = message, Style = (Style)Application.Current.Resources["M3.BodySmall"], Margin = new Thickness(0, 2, 0, 0) };
        if (isError)
        {
            t.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnErrorContainer");
            m.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnErrorContainer");
        }
        texts.Children.Add(t);
        texts.Children.Add(m);
        row.Children.Add(texts);
        stack.Children.Add(row);

        if (openPath is not null && File.Exists(openPath))
        {
            var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var folder = new Button { Content = "Mostrar en carpeta", Style = (Style)Application.Current.Resources["M3.TextButton"] };
            folder.Click += (_, e) => { Shell.Reveal(openPath); Close(); e.Handled = true; };
            var open = new Button { Content = "Abrir", Style = (Style)Application.Current.Resources["M3.TextButton"] };
            open.Click += (_, e) => { Shell.Open(openPath); Close(); e.Handled = true; };
            actions.Children.Add(folder);
            actions.Children.Add(open);
            stack.Children.Add(actions);
        }

        card.Child = stack;
        Content = card;

        _timer.Interval = TimeSpan.FromSeconds(seconds);
        _timer.Tick += (_, _) => FadeOut();
        MouseEnter += (_, _) => { _timer.Stop(); BeginAnimation(OpacityProperty, null); Opacity = 1; };
        MouseLeave += (_, _) => { if (!_closing) _timer.Start(); };
        MouseLeftButtonUp += (_, e) =>
        {
            if (e.Handled) return;
            if (_openPath is not null) Shell.Open(_openPath);
            Close();
        };
        MouseRightButtonUp += (_, _) => Close();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var ex = (long)NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (nint)(ex | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
            NativeMethods.ExcludeFromCapture(hwnd);
        };
        Loaded += (_, _) =>
        {
            PlaceBottomRight();
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
            _timer.Start();
        };
        Closed += (_, _) => { _timer.Stop(); if (_current == this) _current = null; };
    }

    private void PlaceBottomRight()
    {
        NativeMethods.GetCursorPos(out var cp);
        var screen = Screens.FromPoint(cp.X, cp.Y);
        if (screen is null) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var w = ActualWidth * dpi.DpiScaleX;
        var h = ActualHeight * dpi.DpiScaleY;
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST,
            (int)(screen.WorkArea.Right - w), (int)(screen.WorkArea.Bottom - h), 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    private void FadeOut()
    {
        _timer.Stop();
        var a = new DoubleAnimation(0, TimeSpan.FromSeconds(1));
        a.Completed += (_, _) => { if (Opacity <= 0.01) { _closing = true; Close(); } };
        BeginAnimation(OpacityProperty, a);
    }
}
