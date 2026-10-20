using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Pantallazo.Core;
using Pantallazo.Theming;

namespace Pantallazo.Views;

/// <summary>
/// Campo para asignar un atajo (como el de ShareX): clic → "Presioná una combinación…" con los atajos globales
/// en pausa; la primera tecla que no sea modificador la fija. Esc deja el atajo sin asignar. Impr Pant se toma
/// al soltarla porque Windows no avisa cuando se aprieta. Muestra a la derecha si quedó registrado.
/// </summary>
public sealed class HotkeyBox : Button
{
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
    private readonly TextBlock _status = new() { Margin = new Thickness(10, 0, 0, 0) };
    private readonly Border _frame = new() { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), MinWidth = 220, Height = 40, Padding = new Thickness(14, 0, 12, 0) };
    private bool _capturing;
    private HotkeyGesture _gesture;
    private HotkeyStatus _hotkeyStatus;

    public event EventHandler<HotkeyGesture>? GestureChanged;
    public event EventHandler? CaptureStarted;
    public event EventHandler? CaptureEnded;

    public HotkeyBox()
    {
        Cursor = Cursors.Hand;
        Focusable = true;
        FocusVisualStyle = null;
        _status.Style = (Style)Application.Current.Resources["M3.Icon"];
        _status.FontSize = 14;
        var dock = new DockPanel();
        DockPanel.SetDock(_status, Dock.Right);
        dock.Children.Add(_status);
        dock.Children.Add(_text);
        _frame.Child = dock;
        Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) };
        Content = _frame;
        _text.FontFamily = (FontFamily)Application.Current.Resources["M3.Font"];
        Click += (_, _) => { if (_capturing) EndCapture(); else BeginCapture(); };
        LostKeyboardFocus += (_, _) => EndCapture();
        MouseEnter += (_, _) => Paint();
        MouseLeave += (_, _) => Paint();
        Paint();
    }

    public HotkeyGesture Gesture
    {
        get => _gesture;
        set { _gesture = value; Paint(); }
    }

    public void SetStatus(HotkeyStatus status, string? reason)
    {
        _hotkeyStatus = status;
        ToolTip = status switch
        {
            HotkeyStatus.Registered => "Atajo activo",
            HotkeyStatus.Failed => $"No se pudo activar: {reason ?? "está ocupado"}",
            _ => "Sin asignar",
        };
        Paint();
    }

    private void BeginCapture()
    {
        _capturing = true;
        Keyboard.Focus(this);
        CaptureStarted?.Invoke(this, EventArgs.Empty);
        Paint("Presioná una combinación…");
    }

    private void EndCapture()
    {
        if (!_capturing) return;
        _capturing = false;
        Paint();
        CaptureEnded?.Invoke(this, EventArgs.Empty);
    }

    private void Commit(HotkeyGesture g)
    {
        _gesture = g;
        _capturing = false;
        Paint();
        GestureChanged?.Invoke(this, g);
        CaptureEnded?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_capturing) { base.OnPreviewKeyDown(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = CurrentModifiers();
        if (key == Key.Escape && mods == HotkeyModifiers.None)
        {
            Commit(HotkeyGesture.None);
            return;
        }
        if (key is Key.Snapshot or Key.ImeProcessed or Key.DeadCharProcessed) return; // Impr Pant: en KeyUp
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (KeyNames.IsModifierKey(vk))
        {
            Paint(mods == HotkeyModifiers.None ? "Presioná una combinación…" : PartialText(mods));
            return;
        }
        Commit(new HotkeyGesture(mods, vk));
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (!_capturing) { base.OnPreviewKeyUp(e); return; }
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Snapshot) Commit(new HotkeyGesture(CurrentModifiers(), HotkeyGesture.VkPrintScreen));
        else if (KeyNames.IsModifierKey(KeyInterop.VirtualKeyFromKey(key)))
        {
            var mods = CurrentModifiers();
            Paint(mods == HotkeyModifiers.None ? "Presioná una combinación…" : PartialText(mods));
        }
    }

    private static string PartialText(HotkeyModifiers mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (mods.HasFlag(HotkeyModifiers.Shift)) parts.Add("Mayús");
        if (mods.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        return string.Join(" + ", parts) + " + …";
    }

    private static HotkeyModifiers CurrentModifiers()
    {
        var m = HotkeyModifiers.None;
        var k = Keyboard.Modifiers;
        if (k.HasFlag(ModifierKeys.Control)) m |= HotkeyModifiers.Control;
        if (k.HasFlag(ModifierKeys.Alt)) m |= HotkeyModifiers.Alt;
        if (k.HasFlag(ModifierKeys.Shift)) m |= HotkeyModifiers.Shift;
        if (k.HasFlag(ModifierKeys.Windows) || Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin)) m |= HotkeyModifiers.Windows;
        return m;
    }

    private void Paint(string? capturingText = null)
    {
        var res = Application.Current.Resources;
        if (_capturing)
        {
            _text.Text = capturingText ?? "Presioná una combinación…";
            _frame.SetResourceReference(Border.BackgroundProperty, "M3.SecondaryContainer");
            _frame.SetResourceReference(Border.BorderBrushProperty, "M3.Primary");
            _frame.BorderThickness = new Thickness(2);
            _text.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnSecondaryContainer");
            _status.Text = Icons.Keyboard;
            _status.SetResourceReference(TextBlock.ForegroundProperty, "M3.OnSecondaryContainer");
            return;
        }
        _text.Text = _gesture.ToString();
        _frame.Background = IsMouseOver ? (Brush)res["M3.SurfaceContainerHighest"] : Brushes.Transparent;
        _frame.SetResourceReference(Border.BorderBrushProperty, _hotkeyStatus == HotkeyStatus.Failed ? "M3.Error" : "M3.Outline");
        _frame.BorderThickness = new Thickness(1);
        _text.SetResourceReference(TextBlock.ForegroundProperty, _gesture.IsEmpty ? "M3.OnSurfaceVariant" : "M3.OnSurface");
        switch (_hotkeyStatus)
        {
            case HotkeyStatus.Registered:
                _status.Text = Icons.Check;
                _status.SetResourceReference(TextBlock.ForegroundProperty, "M3.Primary");
                break;
            case HotkeyStatus.Failed:
                _status.Text = Icons.Error;
                _status.SetResourceReference(TextBlock.ForegroundProperty, "M3.Error");
                break;
            default:
                _status.Text = "";
                break;
        }
    }
}
