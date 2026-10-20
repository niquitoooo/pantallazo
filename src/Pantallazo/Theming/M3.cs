using System.Windows;

namespace Pantallazo.Theming;

public enum SegmentPosition { Only, First, Middle, Last }

/// <summary>Propiedades adjuntas que usan las plantillas Material 3 (ícono, posición en botones segmentados).</summary>
public static class M3
{
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(M3), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    public static string? GetIcon(DependencyObject o) => (string?)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, string? value) => o.SetValue(IconProperty, value);

    public static readonly DependencyProperty SegmentProperty = DependencyProperty.RegisterAttached(
        "Segment", typeof(SegmentPosition), typeof(M3), new PropertyMetadata(SegmentPosition.Middle));

    public static SegmentPosition GetSegment(DependencyObject o) => (SegmentPosition)o.GetValue(SegmentProperty);
    public static void SetSegment(DependencyObject o, SegmentPosition value) => o.SetValue(SegmentProperty, value);

    /// <summary>Texto de ayuda debajo de un campo (supporting text).</summary>
    public static readonly DependencyProperty HintProperty = DependencyProperty.RegisterAttached(
        "Hint", typeof(string), typeof(M3), new PropertyMetadata(null));

    public static string? GetHint(DependencyObject o) => (string?)o.GetValue(HintProperty);
    public static void SetHint(DependencyObject o, string? value) => o.SetValue(HintProperty, value);
}

/// <summary>Glifos de Segoe Fluent Icons (Windows 11) / Segoe MDL2 Assets (Windows 10).</summary>
public static class Icons
{
    public const string Home = "";
    public const string Camera = "";
    public const string Region = "";
    public const string FullScreen = "";
    public const string Video = "";
    public const string Record = "";
    public const string Stop = ""; // cuadrado relleno (E71A es solo el contorno)
    public const string Rename = "";
    public const string ChromeMinimize = "";
    public const string ChromeMaximize = "";
    public const string ChromeRestore = "";
    public const string ChromeClose = "";
    public const string Pause = "";
    public const string Play = "";
    public const string Settings = "";
    public const string Folder = "";
    public const string Mic = "";
    public const string Speaker = "";
    public const string Keyboard = "";
    public const string Palette = "";
    public const string Power = "";
    public const string Delete = "";
    public const string Add = "";
    public const string Check = "";
    public const string Error = "";
    public const string Warning = "";
    public const string Info = "";
    public const string Open = "";
    public const string Image = "";
    public const string Clock = "";
    public const string Close = "";
    public const string Refresh = "";
    public const string Undo = "";
    public const string Display = "";
    public const string Unlock = "";
}
