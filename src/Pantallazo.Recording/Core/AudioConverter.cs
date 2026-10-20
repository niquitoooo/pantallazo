namespace Pantallazo.Recording;

internal enum SampleKind { Float32, Int16, Int24, Int32 }

internal readonly record struct SourceFormat(int SampleRate, int Channels, SampleKind Kind)
{
    public int BytesPerSample => Kind switch { SampleKind.Int16 => 2, SampleKind.Int24 => 3, _ => 4 };
    public int BlockAlign => BytesPerSample * Channels;
}

/// <summary>
/// Convierte audio de un dispositivo (cualquier tasa, canales y formato) a 48 kHz estéreo float intercalado.
/// Remuestreo lineal con estado entre llamadas. No es thread-safe: una instancia por fuente.
/// </summary>
internal sealed class AudioConverter
{
    public const int OutputRate = 48_000;
    private const float Minus3dB = 0.70710678f;

    private readonly SourceFormat _format;
    private readonly double _step;          // cuadros de entrada por cuadro de salida
    private float[] _stereo = new float[2048];
    private float[] _output = new float[2048];
    private bool _hasPrev;
    private float _prevL, _prevR;
    private double _phase;                  // posición del próximo cuadro de salida, relativa al cuadro "prev"

    public AudioConverter(SourceFormat format)
    {
        if (format.SampleRate <= 0 || format.Channels <= 0) throw new ArgumentException("Formato de audio inválido.");
        _format = format;
        _step = (double)format.SampleRate / OutputRate;
    }

    public SourceFormat Format => _format;

    /// <summary>Convierte un bloque. El resultado es válido hasta la próxima llamada.</summary>
    public ReadOnlySpan<float> Convert(ReadOnlySpan<byte> input)
    {
        int frames = input.Length / _format.BlockAlign;
        if (frames == 0) return ReadOnlySpan<float>.Empty;
        EnsureCapacity(ref _stereo, frames * 2);
        Decode(input, frames, _stereo);
        if (_format.SampleRate == OutputRate) return _stereo.AsSpan(0, frames * 2);
        return Resample(_stereo.AsSpan(0, frames * 2), frames);
    }

    private void Decode(ReadOnlySpan<byte> input, int frames, float[] dest)
    {
        int ch = _format.Channels;
        int bps = _format.BytesPerSample;
        Span<float> frame = stackalloc float[Math.Min(ch, 32)];
        for (int f = 0; f < frames; f++)
        {
            int baseOffset = f * _format.BlockAlign;
            int n = Math.Min(ch, frame.Length);
            for (int c = 0; c < n; c++) frame[c] = ReadSample(input.Slice(baseOffset + c * bps, bps));
            float l, r;
            if (ch == 1) { l = r = frame[0]; }
            else if (ch == 6 || ch == 8)
            {
                // Orden estándar: FL FR FC LFE BL BR [SL SR]. Bajada a estéreo sin LFE.
                float c3 = frame[2] * Minus3dB;
                float bl = frame[4], br = frame[5];
                if (ch == 8) { bl += frame[6]; br += frame[7]; }
                l = frame[0] + c3 + bl * Minus3dB;
                r = frame[1] + c3 + br * Minus3dB;
            }
            else { l = frame[0]; r = frame[1]; }
            dest[2 * f] = l;
            dest[2 * f + 1] = r;
        }
    }

    private float ReadSample(ReadOnlySpan<byte> s) => _format.Kind switch
    {
        SampleKind.Float32 => BitConverter.ToSingle(s),
        SampleKind.Int16 => BitConverter.ToInt16(s) / 32768f,
        SampleKind.Int24 => ((s[0] << 8) | (s[1] << 16) | (s[2] << 24)) / 2147483648f,
        SampleKind.Int32 => BitConverter.ToInt32(s) / 2147483648f,
        _ => 0f,
    };

    private ReadOnlySpan<float> Resample(ReadOnlySpan<float> input, int frames)
    {
        // Secuencia extendida: índice 0 = cuadro previo (si hay), 1..frames = cuadros nuevos.
        int offset = _hasPrev ? 1 : 0;
        int last = frames - 1 + offset;     // índice del último cuadro disponible
        int maxOut = (int)Math.Ceiling((last + 1) / _step) + 2;
        EnsureCapacity(ref _output, maxOut * 2);
        int o = 0;
        double p = _phase;
        // Se interpola entre i e i+1 mientras p < last; el resto queda para la próxima llamada.
        while (p < last)
        {
            int i = (int)Math.Floor(p);
            float frac = (float)(p - i);
            GetFrame(input, i, offset, out float l0, out float r0);
            GetFrame(input, i + 1, offset, out float l1, out float r1);
            _output[2 * o] = l0 + (l1 - l0) * frac;
            _output[2 * o + 1] = r0 + (r1 - r0) * frac;
            o++;
            p += _step;
        }
        _phase = p - last;                  // relativo al nuevo "prev" (= último cuadro)
        _prevL = input[2 * (frames - 1)];
        _prevR = input[2 * (frames - 1) + 1];
        _hasPrev = true;
        return _output.AsSpan(0, o * 2);
    }

    private void GetFrame(ReadOnlySpan<float> input, int extendedIndex, int offset, out float l, out float r)
    {
        if (offset == 1 && extendedIndex == 0) { l = _prevL; r = _prevR; return; }
        int i = extendedIndex - offset;
        l = input[2 * i];
        r = input[2 * i + 1];
    }

    private static void EnsureCapacity(ref float[] buffer, int length)
    {
        if (buffer.Length < length) buffer = new float[Math.Max(length, buffer.Length * 2)];
    }
}
