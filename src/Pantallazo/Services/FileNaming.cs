using Pantallazo.Config;
using Pantallazo.Core;

namespace Pantallazo.Services;

/// <summary>Nombre del próximo archivo según la plantilla de Configuración (y avanza el número de {n} si se usa).</summary>
public static class FileNaming
{
    public static string Next(SettingsStore settings, string folder, bool recording, DateTime when, ForegroundInfo.Info foreground)
    {
        var (name, next) = Resolve(settings.Current, recording ? settings.Current.RecordingNameTemplate : settings.Current.ScreenshotNameTemplate,
            folder, recording, when, foreground);
        if (next is int n) settings.Update(x => { if (recording) x.RecordingCounter = n; else x.ScreenshotCounter = n; });
        return name;
    }

    /// <summary>El nombre que saldría ahora con <paramref name="template"/>, sin gastar el número (para el ejemplo).</summary>
    public static string Preview(AppSettings s, string template, string folder, bool recording, ForegroundInfo.Info foreground) =>
        Resolve(s, template, folder, recording, DateTime.Now, foreground).Name;

    private static (string Name, int? NextCounter) Resolve(AppSettings s, string template, string folder, bool recording, DateTime when, ForegroundInfo.Info foreground)
    {
        var ext = recording ? ".mp4" : ".png";
        var counter = recording ? s.RecordingCounter : s.ScreenshotCounter;
        string Name() => NameTemplate.Expand(template, new NameContext(recording, when, foreground.App, foreground.Window, counter));
        var name = Name();
        if (!NameTemplate.UsesCounter(template)) return (name + ext, null);
        // Con {n}: si ese número ya está en la carpeta (archivos de antes, otra instalación, la carpeta cambiada), el
        // siguiente libre en vez de "Captura 001 (2)".
        for (var tries = 0; tries < 100_000 && File.Exists(Path.Combine(folder, name + ext)); tries++)
        {
            counter++;
            name = Name();
        }
        return (name + ext, counter + 1);
    }
}
