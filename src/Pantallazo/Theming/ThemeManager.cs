using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Pantallazo.Core;
using Pantallazo.Logging;
using Pantallazo.Native;

namespace Pantallazo.Theming;

/// <summary>
/// Publica los colores del esquema M3 como recursos dinámicos "M3.{Rol}" (pincel) y "M3.{Rol}.Color" (color),
/// y sigue los cambios del tema y del color de acento de Windows.
/// </summary>
public sealed class ThemeManager
{
    private ThemePreference _preference = ThemePreference.System;
    private string? _seedHex;

    public bool IsDark { get; private set; }
    public Rgb Seed { get; private set; }

    public event EventHandler? Applied;

    public ThemeManager()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color or UserPreferenceCategory.VisualStyle)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
    }

    public void Configure(ThemePreference preference, string? seedHex)
    {
        _preference = preference;
        _seedHex = seedHex;
        Apply();
    }

    public void Apply()
    {
        IsDark = _preference switch
        {
            ThemePreference.Dark => true,
            ThemePreference.Light => false,
            _ => SystemUsesDarkTheme(),
        };
        Seed = ColorMath.TryParseHex(_seedHex, out var s) ? s : WindowsAccent() ?? Parse(MaterialScheme.DefaultSeed);

        var res = Application.Current.Resources;
        foreach (var (role, rgb) in MaterialScheme.Create(Seed, IsDark))
        {
            var color = Color.FromRgb(rgb.R, rgb.G, rgb.B);
            res[$"M3.{role}.Color"] = color;
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[$"M3.{role}"] = brush;
        }
        // Capas de estado M3: 8 % hover, 10 % foco/presionado, sobre el color del contenido.
        res["M3.IsDark"] = IsDark;

        foreach (Window w in Application.Current.Windows) ApplyToWindow(w);
        Applied?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Barra de título del sistema con el color de la superficie y modo oscuro coherente.</summary>
    public void ApplyToWindow(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == 0) return;
        var dark = IsDark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        if (Application.Current.Resources["M3.Surface.Color"] is Color surface &&
            Application.Current.Resources["M3.OnSurface.Color"] is Color text)
        {
            var caption = surface.R | surface.G << 8 | surface.B << 16; // COLORREF = 0x00BBGGRR
            var fg = text.R | text.G << 8 | text.B << 16;
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_BORDER_COLOR, ref caption, sizeof(int));
            NativeMethods.DwmSetWindowAttribute(hwnd, NativeMethods.DWMWA_TEXT_COLOR, ref fg, sizeof(int));
        }
    }

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch { return false; }
    }

    public static Rgb? WindowsAccent()
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
            return new Rgb(c.R, c.G, c.B);
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudo leer el color de acento de Windows: {ex.Message}");
            return null;
        }
    }

    private static Rgb Parse(string hex) => ColorMath.TryParseHex(hex, out var c) ? c : new Rgb(0x4F, 0x6C, 0xB8);
}
