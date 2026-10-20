using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Pantallazo.Capture;
using Pantallazo.Core;
using Pantallazo.Native;

namespace Pantallazo.Region;

public sealed record RegionSelectorOptions(
    bool ShowMagnifier = true,
    bool DetectWindows = true,
    bool ShowCursorCrosshair = false,
    int MagnifierPixels = 15);

/// <summary>
/// Selector de región igual al de ShareX (RegionCaptureWindow con QuickCapture): pantalla congelada a pantalla
/// completa sobre todo el escritorio virtual, oscurecido 20 %, detección de ventanas y controles, lupa con grilla,
/// "X: … Y: …" y "ancho × alto". Soltar el mouse captura. Estética Material 3 en los paneles.
/// Devuelve el rectángulo en coordenadas del escritorio virtual o null si se canceló.
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    private const int MagnifierSize = 150;        // ShareX: MagnifierSize = 150 (DIP)
    private const int InputDelayMs = 500;         // ShareX: InputDelay = 500 ms (solo teclado)
    private const byte DimAlpha = 51;             // ShareX: BackgroundDimStrength = 20 %

    private enum Interaction { None, PendingHover, Creating }

    private readonly CapturedImage _image;
    private readonly RegionSelectorOptions _options;
    private readonly Task<IReadOnlyList<WindowRect>> _windowsTask;
    private readonly long _openedAt = Environment.TickCount64;

    private readonly Canvas _imageLayer = new();
    private readonly RegionOverlay _overlay = new();
    private readonly Canvas _hud = new() { IsHitTestVisible = false };
    private readonly StackPanel _magPanel = new() { Visibility = Visibility.Collapsed };
    private readonly Grid _magView = new();
    private readonly Image _magImage = new() { Stretch = Stretch.Fill };
    private readonly MagnifierGrid _magGrid = new();
    private readonly TextBlock _pointerText = new();
    private readonly Border _pointerPanel;
    private readonly TextBlock _sizeText = new();
    private readonly Border _sizePanel;
    private WriteableBitmap? _magBitmap;
    private int _magPixels;

    private IReadOnlyList<WindowRect> _windows = [];
    private Interaction _interaction;
    private Point _press;
    private Point _last;
    private Point _lastCreation;
    private bool _movingDuringCreation;
    private bool _ctrlHeldDuringCreation;
    private bool _suppressRightRelease;
    private double _scale = 1;
    private PixelRect? _result;

    public RegionSelectorWindow(CapturedImage frozen, RegionSelectorOptions options)
    {
        _image = frozen;
        _options = options;
        _magPixels = options.MagnifierPixels;
        _windowsTask = options.DetectWindows
            ? Task.Run(() => WindowFinder.Snapshot([], includeControls: true, TimeSpan.FromSeconds(5)))
            : Task.FromResult<IReadOnlyList<WindowRect>>([]);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Title = "Pantallazo - Seleccionar región";
        Cursor = Cursors.Cross;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -32000; // se ubica en píxeles físicos en SourceInitialized
        Top = -32000;
        Width = 10;
        Height = 10;

        // Capa en píxeles de imagen; la escala 1/DPI se aplica cuando se conoce el DPI de la ventana.
        var img = new Image { Source = frozen.ToBitmapSource(), Width = frozen.Width, Height = frozen.Height, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        _overlay.Width = frozen.Width;
        _overlay.Height = frozen.Height;
        _overlay.SetDimAlpha(DimAlpha);
        _overlay.ShowCursorCrosshair = options.ShowCursorCrosshair;
        _imageLayer.Width = frozen.Width;
        _imageLayer.Height = frozen.Height;
        _imageLayer.Children.Add(img);
        _imageLayer.Children.Add(_overlay);
        _imageLayer.HorizontalAlignment = HorizontalAlignment.Left;
        _imageLayer.VerticalAlignment = VerticalAlignment.Top;

        // Lupa: aro blanco 2 px más grande + contenido recortado en círculo + aro negro de 1 px.
        var outer = MagnifierSize + 2;
        _magView.Width = outer;
        _magView.Height = outer;
        _magView.Children.Add(new Ellipse { Fill = Brushes.White, Width = outer, Height = outer });
        var content = new Grid
        {
            Width = MagnifierSize,
            Height = MagnifierSize,
            Clip = new EllipseGeometry(new Rect(0, 0, MagnifierSize, MagnifierSize)),
        };
        RenderOptions.SetBitmapScalingMode(_magImage, BitmapScalingMode.NearestNeighbor);
        content.Children.Add(_magImage);
        content.Children.Add(_magGrid);
        _magView.Children.Add(content);
        _magView.Children.Add(new Ellipse { Stroke = Brushes.Black, StrokeThickness = 1, Width = MagnifierSize, Height = MagnifierSize });
        _magView.Visibility = options.ShowMagnifier ? Visibility.Visible : Visibility.Collapsed;

        _pointerPanel = Chip(_pointerText);
        _pointerPanel.HorizontalAlignment = HorizontalAlignment.Center;
        _pointerPanel.Margin = new Thickness(0, 10, 0, 0); // ShareX: Spacing = 10
        _magPanel.Children.Add(_magView);
        _magPanel.Children.Add(_pointerPanel);
        _hud.Children.Add(_magPanel);

        _sizePanel = Chip(_sizeText);
        _sizePanel.Visibility = Visibility.Collapsed;
        _hud.Children.Add(_sizePanel);

        var root = new Grid();
        root.Children.Add(_imageLayer);
        root.Children.Add(_hud);
        Content = root;

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Deactivated += (_, _) => { if (IsVisible) Activate(); };
    }

    /// <summary>Muestra el selector y espera el resultado.</summary>
    public PixelRect? Select()
    {
        ShowDialog();
        return _result;
    }

    private static Border Chip(TextBlock text)
    {
        text.FontFamily = (FontFamily)Application.Current.Resources["M3.Font"];
        text.FontSize = 12;
        text.FontWeight = FontWeights.SemiBold;
        text.TextAlignment = TextAlignment.Center;
        text.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnSurface");
        var b = new Border
        {
            Padding = new Thickness(8, 4, 8, 4),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = text,
        };
        b.SetResourceReference(Border.BackgroundProperty, "M3.SurfaceContainerHigh");
        b.SetResourceReference(Border.BorderBrushProperty, "M3.OutlineVariant");
        return b;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        // La ventana nunca debe salir en otra captura (por si se graba mientras se selecciona).
        NativeMethods.ExcludeFromCapture(hwnd);
        // Posición y tamaño exactos en píxeles físicos sobre todo el escritorio virtual.
        var b = _image.Bounds;
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, NativeMethods.SWP_SHOWWINDOW);
        ApplyScale();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        const int WM_DPICHANGED = 0x02E0;
        if (msg == WM_DPICHANGED)
        {
            // Una ventana que cruza monitores no debe cambiar de tamaño: se mantiene en píxeles físicos.
            handled = true;
            var b = _image.Bounds;
            NativeMethods.SetWindowPos(hwnd, 0, b.X, b.Y, b.Width, b.Height, NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
        return 0;
    }

    private void ApplyScale()
    {
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (_scale <= 0) _scale = 1;
        _imageLayer.LayoutTransform = new ScaleTransform(1 / _scale, 1 / _scale);
        _magGrid.Scale = _scale;
        var max = (int)Math.Floor(MagnifierSize * _scale / 3); // ShareX: MagnifierPixelSizeMinimum = 3
        _magPixels = RegionGeometry.ClampMagnifierPixels(_magPixels, max);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var b = _image.Bounds;
        NativeMethods.SetWindowPos(new WindowInteropHelper(this).Handle, NativeMethods.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, NativeMethods.SWP_SHOWWINDOW);
        ApplyScale();
        var accent =Application.Current.Resources["M3.Primary.Color"] is Color c ? c : Colors.DodgerBlue;
        _overlay.SetAccent(accent);
        _magGrid.SetAccent(accent);
        Activate();
        Focus();
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetForegroundWindow(hwnd);
        Keyboard.Focus(this);

        var cursor = CursorImagePoint();
        _last = cursor;
        UpdateHud(cursor);
        try { _windows = await _windowsTask; } catch { _windows = []; }
        if (_interaction == Interaction.None) UpdateHover(_last);
    }

    // ---------- Mouse ----------

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        var p = ImagePoint(e);
        _last = p;

        if (e.ChangedButton == MouseButton.Right)
        {
            if (_interaction is Interaction.Creating or Interaction.PendingHover)
            {
                // Clic derecho mientras se crea: cancela la selección (no la captura).
                _suppressRightRelease = true;
                ReleaseMouseCapture();
                ClearSelection();
                UpdateHud(p);
            }
            e.Handled = true;
            return;
        }
        if (e.ChangedButton == MouseButton.XButton1) { CompleteFullScreen(); return; }
        if (e.ChangedButton == MouseButton.XButton2) { CompleteActiveMonitor(); return; }
        if (e.ChangedButton != MouseButton.Left) return;

        _press = p;
        if (!_overlay.Hover.IsEmpty)
        {
            _overlay.SetSelection(_overlay.Hover);
            _interaction = Interaction.PendingHover;
        }
        else
        {
            _overlay.SetSelection(default);
            _interaction = Interaction.Creating;
        }
        _lastCreation = p;
        _movingDuringCreation = false;
        _ctrlHeldDuringCreation = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        CaptureMouse();
        UpdateSizeInfo();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = ImagePoint(e);
        _last = p;
        UpdateHud(p);
        switch (_interaction)
        {
            case Interaction.None:
                UpdateHover(p);
                break;
            case Interaction.PendingHover:
                if (Distance(_press, p) >= RegionGeometry.DragThreshold)
                {
                    _interaction = Interaction.Creating;
                    _overlay.SetSelection(default);
                    _overlay.SetHover(default);
                    UpdateCreation(p);
                }
                break;
            case Interaction.Creating:
                UpdateCreation(p);
                break;
        }
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.Right)
        {
            if (_suppressRightRelease) _suppressRightRelease = false;
            else RemoveSelectionOrCancel();
            e.Handled = true;
            return;
        }
        if (e.ChangedButton != MouseButton.Left || _interaction == Interaction.None) return;

        ReleaseMouseCapture();
        _interaction = Interaction.None;
        _movingDuringCreation = false;
        _ctrlHeldDuringCreation = false;

        var sel = _overlay.Selection;
        if (!RegionGeometry.IsBigEnough(sel))
        {
            if (!_overlay.Hover.IsEmpty) _overlay.SetSelection(_overlay.Hover);
            else ClearSelection();
        }
        if (RegionGeometry.IsBigEnough(_overlay.Selection)) Complete(_overlay.Selection);
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        var max = (int)Math.Floor(MagnifierSize * _scale / 3);
        _magPixels = RegionGeometry.ClampMagnifierPixels(_magPixels + (e.Delta > 0 ? -2 : 2), max);
        _magView.Visibility = Visibility.Visible;
        UpdateHud(_last);
        e.Handled = true;
    }

    // ---------- Teclado ----------

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Environment.TickCount64 - _openedAt < InputDelayMs) { e.Handled = true; return; }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        if (_interaction == Interaction.Creating && key is Key.LeftShift or Key.RightShift)
        {
            UpdateCreation(_lastCreation);
            e.Handled = true;
            return;
        }
        switch (key)
        {
            case Key.Enter when RegionGeometry.IsBigEnough(_overlay.Selection):
                Complete(_overlay.Selection);
                break;
            case Key.Space:
                CompleteFullScreen();
                break;
            case Key.Oem3: // tecla a la izquierda del 1 (~ en inglés, º/| en español)
            case Key.Oem5:
                CompleteActiveMonitor();
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var d = shift ? 10 : 1; // ShareX: MoveSpeedMinimum / MoveSpeedMaximum
                NativeMethods.GetCursorPos(out var cp);
                NativeMethods.SetCursorPos(
                    cp.X + (key == Key.Left ? -d : key == Key.Right ? d : 0),
                    cp.Y + (key == Key.Up ? -d : key == Key.Down ? d : 0));
                break;
            case Key.Delete when RegionGeometry.IsBigEnough(_overlay.Selection):
                ClearSelection();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl)
        {
            _movingDuringCreation = false;
            _ctrlHeldDuringCreation = false;
        }
        if (_interaction == Interaction.Creating && key is Key.LeftShift or Key.RightShift)
        {
            UpdateCreation(_lastCreation);
            e.Handled = true;
            return;
        }
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None &&
            Environment.TickCount64 - _openedAt >= InputDelayMs)
        {
            e.Handled = true;
            Cancel();
        }
    }

    // ---------- Lógica (ShareX: UpdateSelectionDuringCreation, UpdateHover…) ----------

    private void UpdateCreation(Point p)
    {
        var mods = Keyboard.Modifiers;
        var ctrl = mods.HasFlag(ModifierKeys.Control);
        if (!ctrl)
        {
            _movingDuringCreation = false;
        }
        else if (!_overlay.Selection.IsEmpty && (_movingDuringCreation || !_ctrlHeldDuringCreation))
        {
            // Ctrl: mover la selección mientras se crea.
            var before = _overlay.Selection;
            var dx = (int)Math.Round(p.X - _lastCreation.X);
            var dy = (int)Math.Round(p.Y - _lastCreation.Y);
            var x = Math.Clamp(before.X + dx, 0, Math.Max(0, _image.Width - before.Width));
            var y = Math.Clamp(before.Y + dy, 0, Math.Max(0, _image.Height - before.Height));
            _overlay.SetSelection(before with { X = x, Y = y });
            _press = new Point(Math.Clamp(_press.X + x - before.X, 0, _image.Width), Math.Clamp(_press.Y + y - before.Y, 0, _image.Height));
            _movingDuringCreation = true;
            _lastCreation = p;
            _ctrlHeldDuringCreation = true;
            UpdateSizeInfo();
            return;
        }

        var square = mods.HasFlag(ModifierKeys.Shift);
        _overlay.SetSelection(RegionGeometry.FromDrag(
            (int)Math.Round(_press.X), (int)Math.Round(_press.Y), (int)Math.Round(p.X), (int)Math.Round(p.Y),
            square, _image.Width, _image.Height));
        _lastCreation = p;
        _ctrlHeldDuringCreation = ctrl;
        UpdateSizeInfo();
    }

    private void UpdateHover(Point p)
    {
        if (!_overlay.Selection.IsEmpty || _interaction != Interaction.None)
        {
            _overlay.SetHover(default);
            return;
        }
        var sx = _image.Bounds.X + (int)Math.Round(p.X);
        var sy = _image.Bounds.Y + (int)Math.Round(p.Y);
        var hit = WindowFinder.Hit(_windows, sx, sy);
        if (hit is null)
        {
            _overlay.SetHover(default);
            return;
        }
        var r = hit.Value.Rect.Offset(-_image.Bounds.X, -_image.Bounds.Y).Intersect(new PixelRect(0, 0, _image.Width, _image.Height));
        _overlay.SetHover(r);
    }

    private void ClearSelection()
    {
        _interaction = Interaction.None;
        _movingDuringCreation = false;
        _ctrlHeldDuringCreation = false;
        _overlay.SetSelection(default);
        _sizePanel.Visibility = Visibility.Collapsed;
        UpdateHover(_last);
    }

    private void RemoveSelectionOrCancel()
    {
        var s = _overlay.Selection;
        if (RegionGeometry.IsBigEnough(s) && s.Contains((int)_last.X, (int)_last.Y)) ClearSelection();
        else Cancel();
    }

    private void UpdateHud(Point p)
    {
        _overlay.SetCursor(p);
        if (_magView.Visibility == Visibility.Visible) UpdateMagnifier(p);
        var ix = Math.Clamp((int)Math.Round(p.X), 0, _image.Width - 1);
        var iy = Math.Clamp((int)Math.Round(p.Y), 0, _image.Height - 1);
        _pointerText.Text = $"X: {_image.Bounds.X + ix} Y: {_image.Bounds.Y + iy}";
        _magPanel.Visibility = Visibility.Visible;
        _magPanel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _magPanel.DesiredSize;
        var (x, y) = RegionGeometry.PanelNearPointer(p.X / _scale, p.Y / _scale, size.Width, size.Height,
            _image.Width / _scale, _image.Height / _scale);
        Canvas.SetLeft(_magPanel, Math.Round(x * _scale) / _scale);
        Canvas.SetTop(_magPanel, Math.Round(y * _scale) / _scale);
        UpdateSizeInfo();
    }

    private void UpdateMagnifier(Point p)
    {
        var count = _magPixels;
        if (_magBitmap is null || _magBitmap.PixelWidth != count)
        {
            _magBitmap = new WriteableBitmap(count, count, 96, 96, PixelFormats.Bgra32, null);
            _magImage.Source = _magBitmap;
            _magGrid.PixelCount = count;
        }
        var radius = count / 2;
        var cx = (int)Math.Round(p.X);
        var cy = (int)Math.Round(p.Y);
        var buf = new byte[count * count * 4];
        for (var y = 0; y < count; y++)
        {
            var sy = Math.Clamp(cy + y - radius, 0, _image.Height - 1);
            for (var x = 0; x < count; x++)
            {
                var sx = Math.Clamp(cx + x - radius, 0, _image.Width - 1);
                Buffer.BlockCopy(_image.Pixels, sy * _image.Stride + sx * 4, buf, (y * count + x) * 4, 4);
            }
        }
        _magBitmap.WritePixels(new Int32Rect(0, 0, count, count), buf, count * 4, 0);
        _magGrid.InvalidateVisual();
    }

    private void UpdateSizeInfo()
    {
        var s = _overlay.Selection;
        if (s.IsEmpty)
        {
            _sizePanel.Visibility = Visibility.Collapsed;
            return;
        }
        _sizeText.Text = $"{Math.Max(1, s.Width)} × {Math.Max(1, s.Height)}";
        _sizePanel.Visibility = Visibility.Visible;
        var (x, y) = RegionGeometry.SizeLabelPosition(s.X / _scale, s.Y / _scale, s.Bottom / _scale,
            _image.Width / _scale, _image.Height / _scale);
        Canvas.SetLeft(_sizePanel, x);
        Canvas.SetTop(_sizePanel, y);
    }

    // ---------- Resultado ----------

    private void Complete(PixelRect imageRect)
    {
        var r = imageRect.Intersect(new PixelRect(0, 0, _image.Width, _image.Height));
        if (r.IsEmpty) return;
        _result = r.Offset(_image.Bounds.X, _image.Bounds.Y);
        Close();
    }

    private void CompleteFullScreen() => Complete(new PixelRect(0, 0, _image.Width, _image.Height));

    private void CompleteActiveMonitor()
    {
        var sx = _image.Bounds.X + (int)Math.Round(_last.X);
        var sy = _image.Bounds.Y + (int)Math.Round(_last.Y);
        var screen = Screens.FromPoint(sx, sy);
        if (screen is null) return;
        Complete(screen.Bounds.Offset(-_image.Bounds.X, -_image.Bounds.Y));
    }

    private void Cancel()
    {
        _result = null;
        Close();
    }

    // ---------- Coordenadas ----------

    private Point ImagePoint(MouseEventArgs e)
    {
        var p = e.GetPosition(_imageLayer);
        return new Point(Math.Clamp(p.X, 0, _image.Width), Math.Clamp(p.Y, 0, _image.Height));
    }

    private Point CursorImagePoint()
    {
        NativeMethods.GetCursorPos(out var cp);
        return new Point(Math.Clamp(cp.X - _image.Bounds.X, 0, _image.Width), Math.Clamp(cp.Y - _image.Bounds.Y, 0, _image.Height));
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));
}
