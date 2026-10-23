using Microsoft.Win32;
using Pantallazo.Core;
using Pantallazo.Logging;

namespace Pantallazo.Hotkeys;

/// <summary>
/// Windows 11 (y Windows 10 con la opción activada) le da Impr Pant a la Herramienta Recortes.
/// Mientras eso esté activo, Impr Pant sola puede no llegar a la app aunque RegisterHotKey funcione.
/// ShareX lo resuelve desde su instalador escribiendo este mismo valor; acá se ofrece un botón reversible.
/// Antes del primer cambio se anota cómo estaba, para devolverlo igual al pausar o al desinstalar.
/// </summary>
public sealed class PrintScreenKey(string keyboardKeyPath, string recordKeyPath, bool snippingByDefault)
{
    private const string ValueName = "PrintScreenKeyForSnippingEnabled";

    // Anotación (la lee también el desinstalador, installer\Pantallazo.iss): el valor que tenía Windows antes de
    // que Pantallazo tocara la tecla, 0 o 1, u OriginalMissing si no existía.
    private const string RecordName = "ImprPantOriginal";
    private const int OriginalMissing = 2;

    /// <summary>La tecla real del usuario actual. Si el valor no existe, en Windows 11 viene activado.</summary>
    public static PrintScreenKey Windows { get; } =
        new(@"Control Panel\Keyboard", @"Software\Pantallazo", Environment.OSVersion.Version.Build >= 22000);

    /// <summary>true si Impr Pant abre Recortes.</summary>
    public bool IsUsedBySnippingTool()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyboardKeyPath);
            return key?.GetValue(ValueName) switch
            {
                int v => v != 0,
                _ => snippingByDefault,
            };
        }
        catch { return false; }
    }

    /// <summary>Libera (false) o devuelve (true) Impr Pant a Recortes, solo para el usuario actual.</summary>
    public bool SetUsedBySnippingTool(bool enabled)
    {
        try
        {
            RememberOriginal();
            using var key = Registry.CurrentUser.CreateSubKey(keyboardKeyPath);
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

    /// <summary>
    /// Deja la tecla como estaba antes de que Pantallazo la tocara, solo si sigue liberada (si después alguien la
    /// devolvió a Recortes, se respeta). Sin anotación (la versión 1.0.0 no anotaba), vuelve a lo predeterminado de
    /// Windows: en Windows 11, Recortes.
    /// Si antes no había valor, se escribe lo predeterminado en vez de borrarlo: borrado, Windows sigue con lo último
    /// que leyó (el 0) hasta cerrar sesión; escrito, se toma al instante y vale lo mismo.
    /// </summary>
    public bool RestoreOriginal()
    {
        try
        {
            if (ReadValue() is not 0) return true; // no está liberada: no hay nada que devolver
            int original;
            using (var record = Registry.CurrentUser.OpenSubKey(recordKeyPath))
                original = record?.GetValue(RecordName) as int? ?? OriginalMissing;
            var value = original == OriginalMissing ? (snippingByDefault ? 1 : 0) : original == 0 ? 0 : 1;
            using var key = Registry.CurrentUser.CreateSubKey(keyboardKeyPath);
            key.SetValue(ValueName, value, RegistryValueKind.DWord);
            Log.Info($"{ValueName} = {value}: vuelve a como estaba antes de Pantallazo (anotación {original})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo devolver la tecla Impr Pant", ex);
            return false;
        }
    }

    /// <summary>
    /// Pausa: si Pantallazo había liberado Impr Pant, se la devuelve a Windows tal como estaba. Los atajos con Impr Pant
    /// los suelta HotkeyService. Queda en la configuración hasta reactivarla, aunque se reinicie.
    /// </summary>
    public void Pause(AppSettings s)
    {
        if (s.PrintScreenPaused) return;
        s.PrintScreenPaused = true;
        s.PrintScreenFreedBeforePause = ReadValue() is 0;
        if (s.PrintScreenFreedBeforePause) RestoreOriginal();
    }

    /// <summary>Reactiva: si Impr Pant estaba liberada al pausar, la vuelve a liberar.</summary>
    public void Resume(AppSettings s)
    {
        if (!s.PrintScreenPaused) return;
        s.PrintScreenPaused = false;
        if (s.PrintScreenFreedBeforePause) SetUsedBySnippingTool(false);
        s.PrintScreenFreedBeforePause = false;
    }

    /// <summary>Solo la primera vez: los cambios siguientes no pisan cómo estaba antes de Pantallazo.</summary>
    private void RememberOriginal()
    {
        using var record = Registry.CurrentUser.CreateSubKey(recordKeyPath);
        if (record.GetValue(RecordName) is int) return;
        var original = ReadValue() switch { null => OriginalMissing, 0 => 0, _ => 1 };
        record.SetValue(RecordName, original, RegistryValueKind.DWord);
    }

    private int? ReadValue()
    {
        using var key = Registry.CurrentUser.OpenSubKey(keyboardKeyPath);
        return key?.GetValue(ValueName) as int?;
    }
}
