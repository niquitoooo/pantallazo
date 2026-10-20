using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Pantallazo.Theming;

/// <summary>
/// Scroll proporcional a la rueda. El ScrollViewer de WPF mueve siempre 3 líneas (48 px) por cada mensaje de rueda,
/// sin mirar cuánto vale: con un trackpad de precisión, que manda muchos mensajes chicos (delta 5–30 en vez de 120),
/// un deslizamiento corto recorría la página entera. Acá se mueve en proporción al delta, como Edge o el Explorador:
/// un clic de rueda (120) sigue siendo lo mismo que antes.
/// </summary>
public static class WheelScroll
{
    private sealed class Pending { public double Target; public long At; }
    private static readonly ConditionalWeakTable<ScrollViewer, Pending> Targets = new();

    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(ScrollViewer), UIElement.MouseWheelEvent, new MouseWheelEventHandler(OnMouseWheel));

    private static void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0 || sender is not ScrollViewer sv) return;
        // Si este ScrollViewer no tiene para dónde ir (un TextBox, una lista corta), scrollea el de afuera.
        var target = Scrollable(sv);
        if (target is null) return;

        var lines = SystemParameters.WheelScrollLines;
        double perNotch = lines < 0 ? target.ViewportHeight : lines * 16.0;
        // ScrollToVerticalOffset recién se aplica en el próximo layout: los mensajes que llegan antes se suman al
        // destino pendiente en vez de partir del offset viejo (si no, se perdía parte del recorrido).
        var pending = Targets.GetOrCreateValue(target);
        long now = Environment.TickCount64;
        double from = now - pending.At < 100 ? pending.Target : target.VerticalOffset;
        pending.Target = Math.Clamp(from - e.Delta * perNotch / 120.0, 0, target.ScrollableHeight);
        pending.At = now;
        target.ScrollToVerticalOffset(pending.Target);
        e.Handled = true;
    }

    private static ScrollViewer? Scrollable(ScrollViewer sv)
    {
        for (DependencyObject? o = sv; o is not null; o = (o is Visual ? VisualTreeHelper.GetParent(o) : null) ?? LogicalTreeHelper.GetParent(o))
            if (o is ScrollViewer s && s.ScrollableHeight > 0 && s.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled)
                return s;
        return null;
    }
}
