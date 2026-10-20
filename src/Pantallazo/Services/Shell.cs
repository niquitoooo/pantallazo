using System.Diagnostics;
using System.Runtime.InteropServices;
using Pantallazo.Logging;

namespace Pantallazo.Services;

/// <summary>Abrir archivos y carpetas con el Explorador.</summary>
public static class Shell
{
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) { Log.Warn($"No se pudo abrir {path}: {ex.Message}"); }
    }

    public static void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Log.Warn($"No se pudo abrir la carpeta {folder}: {ex.Message}"); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public nint hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public nint hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>
    /// Manda el archivo a la Papelera, como Supr en el Explorador (se puede recuperar). null si salió bien; si no,
    /// el motivo para mostrar.
    /// </summary>
    public static string? Recycle(string file)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        if (!File.Exists(file)) return null; // ya no está: nada que hacer
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = file + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI,
        };
        var result = SHFileOperation(ref op);
        if (result == 0 && op.fAnyOperationsAborted == 0 && !File.Exists(file)) return null;
        Log.Warn($"No se pudo mandar a la Papelera {file}: código {result}");
        return "Puede que esté abierto en otra aplicación. Cerralo y probá de nuevo.";
    }

    /// <summary>Abre la carpeta con el archivo seleccionado.</summary>
    public static void Reveal(string file)
    {
        try
        {
            if (File.Exists(file)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{file}\"") { UseShellExecute = true });
            else OpenFolder(Path.GetDirectoryName(file)!);
        }
        catch (Exception ex) { Log.Warn($"No se pudo mostrar {file}: {ex.Message}"); }
    }
}
