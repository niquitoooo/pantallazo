namespace Pantallazo.Recording;

internal static class FileNaming
{
    /// <summary><c>C:\x\Grabación.mp4</c> → <c>C:\x\Grabación.grabando.mp4</c>.</summary>
    public static string TempPathFor(string finalPath)
    {
        string dir = Path.GetDirectoryName(Path.GetFullPath(finalPath)) ?? ".";
        return Path.Combine(dir, Path.GetFileNameWithoutExtension(finalPath) + ".grabando.mp4");
    }

    /// <summary>Si <paramref name="desired"/> existe, prueba "nombre (2).mp4", "nombre (3).mp4"…</summary>
    public static string UniquePath(string desired, Func<string, bool> exists)
    {
        if (!exists(desired)) return desired;
        string dir = Path.GetDirectoryName(desired) ?? "";
        string name = Path.GetFileNameWithoutExtension(desired);
        string ext = Path.GetExtension(desired);
        for (int i = 2; i < 100_000; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!exists(candidate)) return candidate;
        }
        throw new IOException("No se encontró un nombre libre para el archivo.");
    }
}
