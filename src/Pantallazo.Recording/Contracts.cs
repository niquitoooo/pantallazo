namespace Pantallazo.Recording;

// Contrato público que usa la interfaz (src/Pantallazo). Cambiarlo solo de común acuerdo:
// la app ya está escrita contra estas firmas.

/// <summary>Un monitor, en píxeles físicos y coordenadas del escritorio virtual.</summary>
public sealed record DisplayInfo(
    string Id,              // nombre de dispositivo GDI, p. ej. \\.\DISPLAY1 (estable mientras no se reconecte)
    string FriendlyName,    // nombre del monitor si se puede obtener; si no, "Pantalla 1"
    int X, int Y, int Width, int Height,
    int RefreshHz,
    bool IsPrimary,
    nint Handle);           // HMONITOR

public enum AudioDeviceKind { Output, Input }

public sealed record AudioDeviceInfo(string Id, string Name, AudioDeviceKind Kind, bool IsDefault);

public enum CaptureTargetKind { AllDisplays, Display, Region }

public enum VideoQuality { Low, Balanced, High }

public sealed record MicrophoneSelection(string DeviceId, float Gain = 1f);

public sealed record RecordingOptions
{
    public CaptureTargetKind Target { get; init; } = CaptureTargetKind.AllDisplays;
    /// <summary>Id de <see cref="DisplayInfo"/> cuando Target = Display.</summary>
    public string? DisplayId { get; init; }
    /// <summary>Región fija en píxeles físicos del escritorio virtual cuando Target = Region.</summary>
    public (int X, int Y, int Width, int Height)? Region { get; init; }
    public int Fps { get; init; } = 30;
    /// <summary>Alto máximo del video (p. ej. 1080 o 720); null = la resolución real del área. Se escala en la GPU.</summary>
    public int? MaxOutputHeight { get; init; }
    public VideoQuality Quality { get; init; } = VideoQuality.Balanced;
    public bool CaptureCursor { get; init; } = true;
    public bool SystemAudio { get; init; } = true;
    public float SystemAudioGain { get; init; } = 1f;
    public IReadOnlyList<MicrophoneSelection> Microphones { get; init; } = [];
    /// <summary>Ruta final del .mp4. Se graba en un temporal al lado y se renombra al cerrar bien.</summary>
    public required string OutputPath { get; init; }
}

public enum RecorderState { Idle, Starting, Recording, Stopping, Paused }

public sealed record RecordingResult(
    bool Success,
    string? FilePath,           // archivo final (o temporal que quedó, si no se pudo cerrar)
    string? Error,              // mensaje para el usuario, en castellano
    IReadOnlyList<string> Warnings,
    TimeSpan Duration,
    int Width, int Height);

/// <summary>Nivel pico (0..1) de una fuente de audio, para los medidores.</summary>
public sealed record AudioLevel(string SourceId, float Peak);

/// <summary>Id que usan los medidores y las selecciones para "audio del sistema".</summary>
public static class AudioSourceIds
{
    public const string System = "system";
}
