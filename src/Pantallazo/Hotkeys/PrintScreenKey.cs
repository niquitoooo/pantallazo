using Microsoft.Win32;
using Pantallazo.Logging;

namespace Pantallazo.Hotkeys;

/// <summary>
/// Windows 11 (y Windows 10 con la opción activada) le da Impr Pant a la Herramienta Recortes.
/// Mientras eso esté activo, Impr Pant sola puede no llegar a la app aunque RegisterHotKey funcione.
/// ShareX lo resuelve desde su instalador escribiendo este mismo valor; acá se ofrece un botón reversible.
/// </summary>
public static class PrintScreenKey
{
    private const string KeyPath = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingEnabled";

    /// <summary>true si Impr Pant abre Recortes. Si el valor no existe, en Windows 11 viene activado.</summary>
    public static bool IsUsedBySnippingTool()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) switch
            {
                int v => v != 0,
                _ => Environment.OSVersion.Version.Build >= 22000,
            };
        }
        catch { return false; }
    }

    /// <summary>Libera (false) o devuelve (true) Impr Pant a Recortes, solo para el usuario actual.</summary>
    public static bool SetUsedBySnippingTool(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, enabled ? 1 : 0, RegistryValueKind.DWord);
            Log.Info($"{ValueName} = {(enabled ? 1 : 0)}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo cambiar la tecla Impr Pant", ex);
            return false;
        }
    }
}
