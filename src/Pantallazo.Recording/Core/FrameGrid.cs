namespace Pantallazo.Recording;

/// <summary>
/// Rejilla de aceptación de cuadros. Con t0 la marca del primer cuadro aceptado:
/// ranura = floor((t − t0)·fps + 0,5); se acepta un cuadro solo si su ranura es mayor que la del último aceptado.
/// Elimina los repetidos dentro de un refresco y reduce a los fps pedidos. Tiempos en unidades de 100 ns.
/// </summary>
internal sealed class FrameGrid
{
    private const long TicksPerSecond = 10_000_000;
    private readonly int _fps;
    private long _t0;
    private long _lastSlot = -1;

    public FrameGrid(int fps)
    {
        if (fps <= 0) throw new ArgumentOutOfRangeException(nameof(fps));
        _fps = fps;
    }

    public bool HasFirst => _lastSlot >= 0;
    public long LastSlot => _lastSlot;

    /// <summary>Ranura del tiempo <paramref name="t"/> (requiere un primer cuadro aceptado).</summary>
    public long SlotOf(long t)
    {
        long dt = t - _t0;
        // floor(dt·fps/1e7 + 0,5) en enteros: floor((2·dt·fps + 1e7) / 2e7)
        long num = 2 * dt * _fps + TicksPerSecond;
        return FloorDiv(num, 2 * TicksPerSecond);
    }

    public bool WouldAccept(long t) => !HasFirst || SlotOf(t) > _lastSlot;

    public bool TryAccept(long t)
    {
        if (!HasFirst)
        {
            _t0 = t;
            _lastSlot = 0;
            return true;
        }
        long slot = SlotOf(t);
        if (slot <= _lastSlot) return false;
        _lastSlot = slot;
        return true;
    }

    private static long FloorDiv(long a, long b)
    {
        long q = a / b;
        if ((a % b != 0) && ((a < 0) != (b < 0))) q--;
        return q;
    }
}
