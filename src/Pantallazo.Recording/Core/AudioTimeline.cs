namespace Pantallazo.Recording;

/// <summary>
/// Buffer de una fuente de audio ubicado en la línea de tiempo de la grabación (cuadros de 48 kHz desde t0).
/// La fuente escribe con la hora de llegada (reloj QPC); el mezclador lee por posición.
/// <list type="bullet">
/// <item>Si la fuente se atrasa respecto del reloj más de la tolerancia (loopback que no manda nada mientras no
/// suena nada, reloj del dispositivo más lento), se rellena con silencio hasta donde corresponde.</item>
/// <item>Si se adelanta más de la tolerancia (reloj más rápido), se descarta el exceso.</item>
/// <item>Lo que llega tarde para una zona ya mezclada se descarta.</item>
/// </list>
/// Thread-safe: escribe el hilo de captura y lee el del mezclador.
/// </summary>
internal sealed class AudioTimeline
{
    public const int Rate = AudioConverter.OutputRate;

    private readonly object _lock = new();
    private readonly bool _buffering;
    private readonly int _tolerance;
    private float[] _ring;          // estéreo intercalado
    private int _head;              // índice (en cuadros) del cuadro en ReadPos
    private int _count;             // cuadros guardados
    private long _readPos;          // primer cuadro guardado (posición en la línea de tiempo)
    private float _peak;

    /// <param name="buffering">false = solo mide picos (medidores sin grabar).</param>
    /// <param name="capacityFrames">Máximo guardado; si se supera se tiran los más viejos.</param>
    /// <param name="toleranceFrames">Desvío admitido entre la llegada y la posición antes de corregir.</param>
    public AudioTimeline(bool buffering = true, int capacityFrames = Rate * 2, int toleranceFrames = Rate * 60 / 1000)
    {
        _buffering = buffering;
        _tolerance = toleranceFrames;
        _ring = new float[(buffering ? capacityFrames : 1) * 2];
    }

    public long ReadPos { get { lock (_lock) return _readPos; } }
    public long WritePos { get { lock (_lock) return _readPos + _count; } }
    public int Buffered { get { lock (_lock) return _count; } }
    private int Capacity => _ring.Length / 2;

    /// <summary>Vacía el buffer y ubica la posición de escritura en <paramref name="position"/>.</summary>
    public void Reset(long position = 0)
    {
        lock (_lock) { _head = 0; _count = 0; _readPos = position; _peak = 0; }
    }

    /// <summary>Pico (valor absoluto) de lo escrito desde la última llamada; lo pone en cero.</summary>
    public float TakePeak()
    {
        lock (_lock) { float p = _peak; _peak = 0; return p; }
    }

    /// <summary>
    /// Agrega cuadros estéreo. <paramref name="arrivalFrame"/> = momento de llegada en cuadros desde t0;
    /// se asume que el último cuadro del bloque corresponde a ese momento.
    /// </summary>
    public void Write(ReadOnlySpan<float> stereo, long arrivalFrame)
    {
        int n = stereo.Length / 2;
        if (n == 0) return;
        float peak = 0;
        foreach (var s in stereo) { float a = Math.Abs(s); if (a > peak) peak = a; }

        lock (_lock)
        {
            if (peak > _peak) _peak = peak;
            if (!_buffering) return;

            long writePos = _readPos + _count;
            long expectedStart = arrivalFrame - n;
            int skip = 0;
            if (expectedStart - writePos > _tolerance)
            {
                // Atrasada (o sin datos un rato): silencio hasta donde corresponde.
                long gap = expectedStart - writePos;
                AppendSilence(gap);
            }
            else if (writePos - expectedStart > _tolerance)
            {
                // Adelantada: se descarta el exceso del bloque nuevo.
                skip = (int)Math.Min(n, writePos - expectedStart);
            }
            if (skip < n) Append(stereo.Slice(skip * 2));
        }
    }

    /// <summary>
    /// Lee el tramo [from, from+frames) de la línea de tiempo en <paramref name="dest"/> (estéreo):
    /// lo que no hay se completa con silencio. Avanza la lectura a from+frames.
    /// Devuelve cuántos cuadros eran datos reales.
    /// </summary>
    public int Read(long from, int frames, Span<float> dest)
    {
        dest = dest.Slice(0, frames * 2);
        dest.Clear();
        lock (_lock)
        {
            long end = from + frames;
            // Lo que está antes de 'from' ya no sirve.
            if (_readPos < from) Discard((int)Math.Min(_count, from - _readPos), advanceTo: from);
            int real = 0;
            if (_count > 0 && _readPos < end)
            {
                int destOffset = (int)(_readPos - from);   // >= 0
                int take = (int)Math.Min(_count, end - _readPos);
                CopyOut(dest.Slice(destOffset * 2, take * 2), take);
                real = take;
                Discard(take, advanceTo: _readPos + take);
            }
            if (_readPos < end && _count == 0) _readPos = end;
            return real;
        }
    }

    private void AppendSilence(long frames)
    {
        while (frames > 0)
        {
            int chunk = (int)Math.Min(frames, Capacity);
            MakeRoom(chunk);
            for (int i = 0; i < chunk; i++)
            {
                int idx = (_head + _count) % Capacity;
                _ring[2 * idx] = 0; _ring[2 * idx + 1] = 0;
                _count++;
            }
            frames -= chunk;
        }
    }

    private void Append(ReadOnlySpan<float> stereo)
    {
        int n = stereo.Length / 2;
        if (n > Capacity) { stereo = stereo.Slice((n - Capacity) * 2); Discard(_count, _readPos + _count + (n - Capacity)); n = Capacity; }
        MakeRoom(n);
        for (int i = 0; i < n; i++)
        {
            int idx = (_head + _count) % Capacity;
            _ring[2 * idx] = stereo[2 * i];
            _ring[2 * idx + 1] = stereo[2 * i + 1];
            _count++;
        }
    }

    private void MakeRoom(int frames)
    {
        int over = _count + frames - Capacity;
        if (over > 0) Discard(over, _readPos + over);
    }

    private void Discard(int frames, long advanceTo)
    {
        frames = Math.Min(frames, _count);
        _head = (_head + frames) % Capacity;
        _count -= frames;
        _readPos = advanceTo;
    }

    private void CopyOut(Span<float> dest, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            int idx = (_head + i) % Capacity;
            dest[2 * i] = _ring[2 * idx];
            dest[2 * i + 1] = _ring[2 * idx + 1];
        }
    }
}
