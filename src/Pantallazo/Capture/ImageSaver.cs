using System.Windows;
using System.Windows.Media.Imaging;
using Pantallazo.Core;
using Pantallazo.Logging;

namespace Pantallazo.Capture;

public static class ImageSaver
{
    /// <summary>Guarda en PNG con nombre único. Escribe a un temporal y renombra: nunca deja un PNG a medias.</summary>
    public static string SavePng(CapturedImage image, string folder, string fileName)
    {
        Directory.CreateDirectory(folder);
        var path = FileNames.MakeUnique(folder, fileName);
        var tmp = path + ".tmp";
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image.ToBitmapSource()));
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                encoder.Save(fs);
            File.Move(tmp, path);
            return path;
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            TryDelete(tmp);
            throw new IOException($"No hay espacio en el disco para guardar la captura en {folder}.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            TryDelete(tmp);
            throw new IOException($"No hay permiso para escribir en {folder}.", ex);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    /// <summary>Copia al portapapeles; reintenta si otra app lo tiene abierto.</summary>
    public static bool CopyToClipboard(CapturedImage image)
    {
        var bmp = image.ToBitmapSource();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetImage(bmp);
                return true;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(60);
            }
        }
        Log.Warn("El portapapeles estaba ocupado: la captura se guardó pero no se copió.");
        return false;
    }

    private static bool IsDiskFull(IOException ex) => (ex.HResult & 0xFFFF) is 0x70 or 0x27; // ERROR_DISK_FULL, HANDLE_DISK_FULL

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
