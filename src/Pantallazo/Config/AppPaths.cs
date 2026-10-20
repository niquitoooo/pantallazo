using System.Runtime.InteropServices;
using Pantallazo.Native;

namespace Pantallazo.Config;

/// <summary>Carpetas de la app y carpetas conocidas del usuario (respeta redirecciones a OneDrive u otra unidad).</summary>
public static class AppPaths
{
    private static readonly Guid FolderPictures = new("33E28130-4E1E-4676-835A-98395C3BC3BB");
    private static readonly Guid FolderVideos = new("18989B1D-99B5-455B-841C-AB7C74E4DDFC");

    public const string ScreenshotsSubfolder = "Capturas de pantalla";
    public const string RecordingsSubfolder = "Capturas de video";

    public static string SettingsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Pantallazo");

    public static string LocalFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pantallazo");

    public static string LogsFolder => Path.Combine(LocalFolder, "logs");

    public static string DefaultScreenshotsFolder =>
        Path.Combine(KnownFolder(FolderPictures, Environment.SpecialFolder.MyPictures), ScreenshotsSubfolder);

    public static string DefaultRecordingsFolder =>
        Path.Combine(KnownFolder(FolderVideos, Environment.SpecialFolder.MyVideos), RecordingsSubfolder);

    private static string KnownFolder(Guid id, Environment.SpecialFolder fallback)
    {
        // KF_FLAG_DONT_VERIFY = 0x4000: devuelve la ruta configurada aunque todavía no exista.
        if (NativeMethods.SHGetKnownFolderPath(id, 0x4000, 0, out var p) == 0)
        {
            try
            {
                var s = Marshal.PtrToStringUni(p);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        return Environment.GetFolderPath(fallback);
    }
}
