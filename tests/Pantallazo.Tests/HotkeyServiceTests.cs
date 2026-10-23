using Pantallazo.Core;
using Pantallazo.Hotkeys;

namespace Pantallazo.Tests;

public class HotkeyServiceTests
{
    /// <summary>
    /// Si la pausa no se respetara, se llamaría a RegisterHotKey con Impr Pant de verdad y el estado sería
    /// Registered (o Failed si otra copia de Pantallazo ya la tiene): nunca Paused.
    /// </summary>
    [Fact]
    public void Con_Impr_Pant_en_pausa_no_registra_ninguna_combinacion_con_Impr_Pant()
    {
        RunSta(() =>
        {
            using var service = new HotkeyService();
            var defaults = HotkeyDefaults.Create();

            service.Apply(defaults, printScreenPaused: true);

            Assert.All(defaults, b => Assert.Equal(HotkeyStatus.Paused, service.GetStatus(b)));
            Assert.Empty(service.Failed);
        });
    }

    /// <summary>HwndSource (la ventana de mensajes del servicio) necesita un hilo STA.</summary>
    private static void RunSta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(error);
    }
}
