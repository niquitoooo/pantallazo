using Microsoft.Win32;
using Pantallazo.Logging;

namespace Pantallazo.Services;

/// <summary>Inicio con Windows por la clave Run del usuario (sin permisos de administrador).</summary>
public static class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "Pantallazo";
    public const string SilentArg = "--minimizado";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(Name) is string s && s.Contains(Environment.ProcessPath ?? "\u0000", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(Name, $"\"{Environment.ProcessPath}\" {SilentArg}");
            else key.DeleteValue(Name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cambiar el inicio con Windows", ex);
            return false;
        }
    }
}
