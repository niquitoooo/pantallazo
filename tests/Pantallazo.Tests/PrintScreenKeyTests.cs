using Microsoft.Win32;
using Pantallazo.Core;
using Pantallazo.Hotkeys;

namespace Pantallazo.Tests;

/// <summary>
/// Con el registro de verdad, pero en una clave propia de cada prueba (HKCU\Software\Pantallazo-Tests\…)
/// que se borra al terminar: nunca se toca la tecla Impr Pant real.
/// </summary>
public sealed class PrintScreenKeyTests : IDisposable
{
    private const string TestsRoot = @"Software\Pantallazo-Tests";
    private readonly string _root = $@"{TestsRoot}\{Guid.NewGuid():N}";

    private string KeyboardPath => _root + @"\Keyboard";
    private PrintScreenKey NewKey(bool snippingByDefault = true) => new(KeyboardPath, _root + @"\Pantallazo", snippingByDefault);

    /// <summary>PrintScreenKeyForSnippingEnabled de la clave de prueba; null = el valor no existe.</summary>
    private int? Snipping
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyboardPath);
            return key?.GetValue("PrintScreenKeyForSnippingEnabled") as int?;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyboardPath);
            if (value is { } v) key.SetValue("PrintScreenKeyForSnippingEnabled", v, RegistryValueKind.DWord);
            else key.DeleteValue("PrintScreenKeyForSnippingEnabled", throwOnMissingValue: false);
        }
    }

    public void Dispose()
    {
        Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
        using var parent = Registry.CurrentUser.OpenSubKey(TestsRoot);
        if (parent is { SubKeyCount: 0, ValueCount: 0 }) Registry.CurrentUser.DeleteSubKey(TestsRoot, throwOnMissingSubKey: false);
    }

    [Fact]
    public void Sin_valor_vale_lo_predeterminado_de_Windows()
    {
        Snipping = null;
        Assert.True(NewKey(snippingByDefault: true).IsUsedBySnippingTool());
        Assert.False(NewKey(snippingByDefault: false).IsUsedBySnippingTool());
    }

    [Fact]
    public void Restaurar_vuelve_al_valor_que_tenia_Windows_antes_de_liberar()
    {
        Snipping = 1;
        var key = NewKey();
        Assert.True(key.SetUsedBySnippingTool(false));
        Assert.Equal(0, Snipping);

        Assert.True(key.RestoreOriginal());
        Assert.Equal(1, Snipping);
    }

    /// <summary>
    /// Borrar el valor no alcanza: Windows sigue con lo último que leyó (viniendo de 0, Impr Pant no abre Recortes)
    /// hasta cerrar sesión. Por eso se escribe lo predeterminado, que vale lo mismo y se toma al instante.
    /// </summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Restaurar_sin_valor_previo_escribe_lo_predeterminado_de_Windows(bool snippingByDefault, int expected)
    {
        Snipping = null;
        var key = NewKey(snippingByDefault);
        key.SetUsedBySnippingTool(false);
        Assert.Equal(0, Snipping);

        key.RestoreOriginal();
        Assert.Equal(expected, Snipping);
    }

    [Fact]
    public void Restaurar_respeta_un_cero_que_ya_estaba_antes_de_liberar()
    {
        Snipping = 0;
        var key = NewKey();
        key.SetUsedBySnippingTool(false);
        key.RestoreOriginal();
        Assert.Equal(0, Snipping);
    }

    [Fact]
    public void Volver_a_liberar_no_pisa_el_original()
    {
        Snipping = null;
        var key = NewKey(snippingByDefault: false);
        key.SetUsedBySnippingTool(false);
        key.SetUsedBySnippingTool(true);   // "Devolver Impr Pant a Recortes"
        Assert.Equal(1, Snipping);
        key.SetUsedBySnippingTool(false);

        key.RestoreOriginal();
        Assert.Equal(0, Snipping);   // lo predeterminado sin valor; si se hubiera pisado la anotación, sería 1
    }

    [Fact]
    public void Si_ya_no_esta_liberada_restaurar_no_pisa_lo_que_eligio_el_usuario()
    {
        Snipping = null;
        var key = NewKey(snippingByDefault: false);
        key.SetUsedBySnippingTool(false);
        Snipping = 1;   // después la devolvió a Recortes desde Configuración de Windows

        key.RestoreOriginal();

        Assert.Equal(1, Snipping);
    }

    [Fact]
    public void Sin_anotacion_y_liberada_restaurar_deja_lo_de_Windows()
    {
        Snipping = 0;   // liberada por la versión 1.0.0, que no anotaba el original
        NewKey(snippingByDefault: true).RestoreOriginal();
        Assert.Equal(1, Snipping);
    }

    [Fact]
    public void Sin_anotacion_y_sin_liberar_restaurar_no_toca_nada()
    {
        Snipping = 1;
        NewKey().RestoreOriginal();
        Assert.Equal(1, Snipping);
    }

    [Fact]
    public void Pausar_con_Impr_Pant_liberada_se_la_devuelve_a_Windows()
    {
        Snipping = 1;
        var key = NewKey();
        key.SetUsedBySnippingTool(false);
        var s = new AppSettings();

        key.Pause(s);

        Assert.True(s.PrintScreenPaused);
        Assert.Equal(1, Snipping);
    }

    [Fact]
    public void Reactivar_vuelve_a_liberar_Impr_Pant()
    {
        Snipping = 1;
        var key = NewKey();
        key.SetUsedBySnippingTool(false);
        var s = new AppSettings();
        key.Pause(s);

        key.Resume(s);

        Assert.False(s.PrintScreenPaused);
        Assert.Equal(0, Snipping);
    }

    [Fact]
    public void Pausar_y_reactivar_sin_haberla_liberado_la_deja_en_Recortes()
    {
        Snipping = 1;
        var key = NewKey();
        var s = new AppSettings();

        key.Pause(s);
        Assert.Equal(1, Snipping);
        key.Resume(s);
        Assert.Equal(1, Snipping);
    }

    [Fact]
    public void Pausar_dos_veces_no_olvida_que_estaba_liberada()
    {
        Snipping = 1;
        var key = NewKey();
        key.SetUsedBySnippingTool(false);
        var s = new AppSettings();

        key.Pause(s);
        key.Pause(s);
        key.Resume(s);

        Assert.Equal(0, Snipping);
    }

    [Fact]
    public void Reactivar_sin_pausa_no_toca_nada()
    {
        Snipping = 1;
        var key = NewKey();
        key.SetUsedBySnippingTool(false);
        var s = new AppSettings();
        key.Pause(s);
        key.Resume(s);
        key.SetUsedBySnippingTool(true);   // después la devolvió a Recortes a mano

        key.Resume(s);

        Assert.Equal(1, Snipping);
    }
}
