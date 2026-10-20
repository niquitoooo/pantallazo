using System.Globalization;

namespace Pantallazo.Core;

public static class FileNames
{
    public static string Screenshot(DateTime now) =>
        $"Captura_{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.png";

    public static string Recording(DateTime now) =>
        $"Grabacion_{now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}.mp4";

    private static readonly string[] Reserved =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// Revisa el nombre nuevo (sin extensión) que escribió el usuario al renombrar. null si sirve; si no, el motivo
    /// en castellano para mostrar debajo del campo.
    /// </summary>
    public static string? ValidateNewName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Escribí un nombre.";
        if (name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl))
            return "El nombre no puede tener \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "El nombre no puede terminar en punto ni en espacio.";
        if (Reserved.Contains(name.Trim().ToUpperInvariant())) return "Windows reserva ese nombre; elegí otro.";
        if (name.Length > 200) return "El nombre es demasiado largo.";
        return null;
    }

    /// <summary>
    /// Ruta dentro de <paramref name="folder"/> que no pisa nada: si el nombre existe agrega " (2)", " (3)"…
    /// </summary>
    public static string MakeUnique(string folder, string fileName, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var path = Path.Combine(folder, fileName);
        if (!exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            path = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!exists(path)) return path;
        }
    }
}
