using System.Media;

namespace Pantallazo.Services;

/// <summary>Sonidos propios generados en memoria (sin archivos ni recursos de terceros).</summary>
public static class Sounds
{
    private static readonly Lazy<byte[]> Shutter = new(() => Build(ShutterSamples()));
    private static readonly Lazy<byte[]> Start = new(() => Build(Chirp(660, 990, 0.12)));
    private static readonly Lazy<byte[]> End = new(() => Build(Chirp(990, 660, 0.12)));

    public static void PlayCapture() => Play(Shutter.Value);
    public static void PlayRecordStart() => Play(Start.Value);
    public static void PlayRecordStop() => Play(End.Value);

    private static void Play(byte[] wav)
    {
        try
        {
            var p = new SoundPlayer(new MemoryStream(wav));
            p.Play(); // asíncrono
        }
        catch { /* sin dispositivo de audio: la captura sigue igual */ }
    }

    private const int Rate = 44100;

    // Dos chasquidos de ruido filtrado con caída rápida: suena a obturador.
    private static float[] ShutterSamples()
    {
        var n = (int)(Rate * 0.16);
        var s = new float[n];
        var rnd = new Random(7);
        float lp = 0;
        for (var i = 0; i < n; i++)
        {
            var t = i / (double)Rate;
            var env = Math.Exp(-t * 55) + (t > 0.07 ? 0.7 * Math.Exp(-(t - 0.07) * 70) : 0);
            var noise = (float)(rnd.NextDouble() * 2 - 1);
            lp += 0.35f * (noise - lp);
            s[i] = (float)(lp * env * 0.9);
        }
        return s;
    }

    private static float[] Chirp(double f0, double f1, double seconds)
    {
        var n = (int)(Rate * seconds);
        var s = new float[n];
        double phase = 0;
        for (var i = 0; i < n; i++)
        {
            var k = i / (double)n;
            phase += 2 * Math.PI * (f0 + (f1 - f0) * k) / Rate;
            var env = Math.Min(1, k * 20) * Math.Min(1, (1 - k) * 8);
            s[i] = (float)(Math.Sin(phase) * env * 0.25);
        }
        return s;
    }

    private static byte[] Build(float[] samples)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        var dataLen = samples.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataLen); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataLen);
        foreach (var f in samples) w.Write((short)Math.Clamp(f * 32767, -32768, 32767));
        w.Flush();
        return ms.ToArray();
    }
}
