namespace Pantallazo.Core;

public enum ThemePreference { System, Light, Dark }

public enum RecordTarget { AllDisplays, Display, Region }

public enum AudioMode { System, Microphones, SystemAndMicrophones, None }

public enum RecordQuality { Low, Balanced, High }

/// <summary>Alto máximo del video. Original = la resolución real de lo que se graba.</summary>
public enum RecordResolution { Original, P1080, P720 }

public sealed class MicrophoneSetting
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public double Volume { get; set; } = 1.0;
}

/// <summary>Configuración persistente. Valores por defecto = los del pedido (y de ShareX donde el pedido no dice).</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 1;

    // Atajos
    public List<HotkeyBinding> Hotkeys { get; set; } = [.. HotkeyDefaults.Create()];

    // Carpetas (null = la predeterminada dentro de Imágenes / Videos)
    public string? ScreenshotsFolder { get; set; }
    public string? RecordingsFolder { get; set; }

    // Apariencia
    public ThemePreference Theme { get; set; } = ThemePreference.System;
    /// <summary>Color semilla "#RRGGBB"; null = color de acento de Windows.</summary>
    public string? SeedColor { get; set; }

    // Captura
    public bool CaptureCursor { get; set; } = true;          // ShareX: ShowCursor = true
    public bool CopyToClipboard { get; set; } = true;        // ShareX: CopyImageToClipboard | SaveImageToFile
    public bool PlaySound { get; set; } = true;              // ShareX: PlaySoundAfterCapture = true
    public bool ShowNotification { get; set; } = true;
    public bool RegionShowMagnifier { get; set; } = true;    // ShareX: ShowMagnifier = true
    public bool RegionDetectWindows { get; set; } = true;    // ShareX: DetectWindows = true
    public bool RegionShowCrosshair { get; set; }            // ShareX: ShowScreenCrosshair = false
    public int RegionMagnifierPixels { get; set; } = 15;     // ShareX: MagnifierPixelCount = 15

    // Grabación
    public RecordTarget RecordTarget { get; set; } = RecordTarget.AllDisplays;
    public string? RecordDisplayId { get; set; }
    public PixelRect? RecordRegion { get; set; }
    public int Fps { get; set; } = 30;
    public RecordQuality Quality { get; set; } = RecordQuality.Balanced;
    public RecordResolution Resolution { get; set; } = RecordResolution.Original;
    /// <summary>Dónde dejó el usuario el indicador de grabación (píxeles físicos, esquina superior izquierda); null = arriba al centro.</summary>
    public int? IndicatorX { get; set; }
    public int? IndicatorY { get; set; }
    public bool RecordCursor { get; set; } = true;
    public AudioMode AudioMode { get; set; } = AudioMode.System;
    public double SystemVolume { get; set; } = 1.0;
    public List<MicrophoneSetting> Microphones { get; set; } = [];

    // Inicio y segundo plano
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; } = true;
    public bool TrayHintShown { get; set; }

    // Ventana
    public double WindowWidth { get; set; } = 980;
    public double WindowHeight { get; set; } = 680;

    // Nombres de archivo (plantillas con etiquetas, ver NameTemplate) y el número que sigue para {n}
    public string ScreenshotNameTemplate { get; set; } = NameTemplate.DefaultScreenshot;
    public string RecordingNameTemplate { get; set; } = NameTemplate.DefaultRecording;
    public int ScreenshotCounter { get; set; } = 1;
    public int RecordingCounter { get; set; } = 1;

    public static readonly int[] FpsOptions = [15, 24, 30, 60, 90, 120, 144];

    /// <summary>
    /// 144 fps solo se ofrece con un monitor de 144 Hz o más: con uno de 120 Hz saldrían cuadros repetidos a saltos.
    /// Windows informa 143 en algunos monitores de 143,98 Hz.
    /// </summary>
    public const int HighRefreshFps = 144;
    public static bool IsFpsOffered(int fps, int maxRefreshHz) => fps != HighRefreshFps || maxRefreshHz >= 143;

    /// <summary>Corrige valores imposibles (archivo editado a mano, versión vieja).</summary>
    public void Normalize()
    {
        Hotkeys ??= [.. HotkeyDefaults.Create()];
        foreach (var action in Enum.GetValues<HotkeyAction>())
            if (Hotkeys.All(h => h.Action != action))
                Hotkeys.Add(HotkeyDefaults.Create().First(h => h.Action == action));
        Microphones ??= [];
        if (!FpsOptions.Contains(Fps)) Fps = 30;
        if (NameTemplate.Validate(ScreenshotNameTemplate) is not null || NameTemplate.OldDefaultsScreenshot.Contains(ScreenshotNameTemplate))
            ScreenshotNameTemplate = NameTemplate.DefaultScreenshot;
        if (NameTemplate.Validate(RecordingNameTemplate) is not null || NameTemplate.OldDefaultsRecording.Contains(RecordingNameTemplate))
            RecordingNameTemplate = NameTemplate.DefaultRecording;
        ScreenshotCounter = Math.Max(1, ScreenshotCounter);
        RecordingCounter = Math.Max(1, RecordingCounter);
        SystemVolume = Math.Clamp(SystemVolume, 0, 2);
        foreach (var m in Microphones) m.Volume = Math.Clamp(m.Volume, 0, 2);
        RegionMagnifierPixels = RegionGeometry.ClampMagnifierPixels(RegionMagnifierPixels);
        WindowWidth = Math.Clamp(WindowWidth, 720, 4000);
        WindowHeight = Math.Clamp(WindowHeight, 520, 3000);
        if (SeedColor is not null && !ColorMath.TryParseHex(SeedColor, out _)) SeedColor = null;
    }
}
