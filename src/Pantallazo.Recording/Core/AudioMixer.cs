namespace Pantallazo.Recording;

/// <summary>Una entrada del mezclador.</summary>
internal sealed record MixerInput(string Id, AudioTimeline Timeline, float Gain);

/// <summary>
/// Mezcla por reloj: cada llamada produce exactamente el tramo pedido de la línea de tiempo; la fuente que no
/// tiene datos aporta silencio. Ganancia por fuente, saturación suave y picos por fuente (después de la ganancia).
/// Ids duplicados se mezclan una sola vez (gana la primera aparición).
/// </summary>
internal sealed class AudioMixer
{
    private readonly MixerInput[] _inputs;
    private readonly float[] _peaks;
    private float[] _scratch = new float[4800 * 2];
    private float[] _mix = new float[4800 * 2];

    public AudioMixer(IEnumerable<MixerInput> inputs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _inputs = inputs.Where(i => seen.Add(i.Id)).ToArray();
        _peaks = new float[_inputs.Length];
    }

    public IReadOnlyList<MixerInput> Inputs => _inputs;

    /// <summary>Próximo cuadro a mezclar (cuadros de 48 kHz desde t0).</summary>
    public long Position { get; private set; }

    public void Reset(long position = 0)
    {
        Position = position;
        Array.Clear(_peaks);
    }

    /// <summary>
    /// Mezcla <paramref name="frames"/> cuadros desde <see cref="Position"/>. Devuelve el estéreo float
    /// intercalado (válido hasta la próxima llamada) y avanza la posición.
    /// </summary>
    public ReadOnlySpan<float> Mix(int frames)
    {
        if (frames <= 0) return ReadOnlySpan<float>.Empty;
        int len = frames * 2;
        if (_mix.Length < len) { _mix = new float[len]; _scratch = new float[len]; }
        var mix = _mix.AsSpan(0, len);
        mix.Clear();
        var scratch = _scratch.AsSpan(0, len);
        for (int k = 0; k < _inputs.Length; k++)
        {
            var input = _inputs[k];
            input.Timeline.Read(Position, frames, scratch);
            float g = input.Gain;
            float peak = _peaks[k];
            for (int i = 0; i < len; i++)
            {
                float v = scratch[i] * g;
                float a = Math.Abs(v);
                if (a > peak) peak = a;
                mix[i] += v;
            }
            _peaks[k] = peak;
        }
        for (int i = 0; i < len; i++) mix[i] = SoftClip(mix[i]);
        Position += frames;
        return mix;
    }

    /// <summary>Picos por fuente desde la última llamada (0..1, acotados), y los pone en cero.</summary>
    public IReadOnlyList<AudioLevel> TakeLevels()
    {
        var list = new AudioLevel[_inputs.Length];
        for (int k = 0; k < _inputs.Length; k++)
        {
            list[k] = new AudioLevel(_inputs[k].Id, Math.Min(1f, _peaks[k]));
            _peaks[k] = 0;
        }
        return list;
    }

    public const float SoftClipThreshold = 0.8f;

    /// <summary>Identidad hasta 0,8; arriba, curva tanh que tiende a ±1 sin pasarlo nunca.</summary>
    public static float SoftClip(float x)
    {
        if (float.IsNaN(x)) return 0;
        float a = Math.Abs(x);
        if (a <= SoftClipThreshold) return x;
        const float t = SoftClipThreshold;
        float y = t + (1 - t) * MathF.Tanh((a - t) / (1 - t));
        y = Math.Min(y, 1f);
        return x < 0 ? -y : y;
    }

    /// <summary>Float [-1,1] → PCM 16 bits little-endian.</summary>
    public static void ToPcm16(ReadOnlySpan<float> samples, Span<byte> dest)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            float v = Math.Clamp(samples[i], -1f, 1f);
            short s = (short)Math.Round(v * 32767f);
            dest[2 * i] = (byte)s;
            dest[2 * i + 1] = (byte)(s >> 8);
        }
    }
}
