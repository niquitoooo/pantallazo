namespace Pantallazo.Recording;

/// <summary>
/// Mensajes de diagnóstico del motor (encoder elegido, adaptador, cuadros, errores). La app puede volcarlos a
/// su log. Se disparan en cualquier hilo; los manejadores no deben bloquear.
/// </summary>
public static class RecordingLog
{
    public static event Action<string>? Message;

    internal static void Write(string text)
    {
        try { Message?.Invoke(text); } catch { /* un manejador roto no debe tirar la grabación */ }
    }
}
