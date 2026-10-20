namespace Pantallazo.Recording.Tests;

public class AudioConverterTests
{
    private static byte[] Floats(params float[] v)
    {
        var b = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, b, 0, b.Length);
        return b;
    }

    [Fact]
    public void Estereo48kFloatPasaIgual()
    {
        var c = new AudioConverter(new SourceFormat(48000, 2, SampleKind.Float32));
        var o = c.Convert(Floats(0.1f, -0.2f, 0.3f, -0.4f)).ToArray();
        Assert.Equal(new[] { 0.1f, -0.2f, 0.3f, -0.4f }, o);
    }

    [Fact]
    public void MonoSeDuplica()
    {
        var c = new AudioConverter(new SourceFormat(48000, 1, SampleKind.Float32));
        var o = c.Convert(Floats(0.5f, -0.25f)).ToArray();
        Assert.Equal(new[] { 0.5f, 0.5f, -0.25f, -0.25f }, o);
    }

    [Fact]
    public void Pcm16SeNormaliza()
    {
        var c = new AudioConverter(new SourceFormat(48000, 2, SampleKind.Int16));
        var bytes = new byte[8];
        BitConverter.TryWriteBytes(bytes.AsSpan(0), (short)16384);
        BitConverter.TryWriteBytes(bytes.AsSpan(2), (short)-32768);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), (short)0);
        BitConverter.TryWriteBytes(bytes.AsSpan(6), (short)32767);
        var o = c.Convert(bytes).ToArray();
        Assert.Equal(0.5f, o[0], 4);
        Assert.Equal(-1f, o[1], 4);
        Assert.Equal(0f, o[2], 4);
        Assert.Equal(1f, o[3], 3);
    }

    [Fact]
    public void Pcm24SeNormaliza()
    {
        var c = new AudioConverter(new SourceFormat(48000, 1, SampleKind.Int24));
        // 0x400000 = 0,5
        var o = c.Convert(new byte[] { 0x00, 0x00, 0x40 }).ToArray();
        Assert.Equal(0.5f, o[0], 4);
    }

    [Fact]
    public void Remuestreo44100A48000MantieneLaDuracionYLaFrecuencia()
    {
        var c = new AudioConverter(new SourceFormat(44100, 2, SampleKind.Float32));
        var output = new List<float>();
        // 1 s de seno de 1 kHz en bloques de 441 cuadros (10 ms)
        int n = 0;
        for (int block = 0; block < 100; block++)
        {
            var v = new float[441 * 2];
            for (int i = 0; i < 441; i++, n++)
            {
                float s = MathF.Sin(2 * MathF.PI * 1000 * n / 44100f) * 0.5f;
                v[2 * i] = s; v[2 * i + 1] = s;
            }
            output.AddRange(c.Convert(Floats(v)).ToArray());
        }
        int frames = output.Count / 2;
        Assert.InRange(frames, 47990, 48000);
        // cruces por cero ascendentes ≈ 1000
        int crossings = 0;
        for (int i = 1; i < frames; i++)
            if (output[2 * (i - 1)] < 0 && output[2 * i] >= 0) crossings++;
        Assert.InRange(crossings, 995, 1001);
        Assert.InRange(output.Max(), 0.49f, 0.5f);
    }

    [Fact]
    public void Remuestreo96000A48000()
    {
        var c = new AudioConverter(new SourceFormat(96000, 2, SampleKind.Float32));
        var v = new float[960 * 2];
        int total = 0;
        for (int b = 0; b < 10; b++) total += c.Convert(Floats(v)).Length / 2;
        Assert.InRange(total, 4799, 4800);
    }

    [Fact]
    public void Bajada51AEstereo()
    {
        var c = new AudioConverter(new SourceFormat(48000, 6, SampleKind.Float32));
        var o = c.Convert(Floats(0.2f, 0.1f, 0f, 1f, 0f, 0f)).ToArray();
        Assert.Equal(0.2f, o[0], 4);   // el LFE no entra
        Assert.Equal(0.1f, o[1], 4);
    }
}

public class AudioTimelineTests
{
    private static float[] Block(int frames, float value)
    {
        var v = new float[frames * 2];
        Array.Fill(v, value);
        return v;
    }

    [Fact]
    public void FuenteContinuaSeLeeEnOrden()
    {
        var t = new AudioTimeline();
        t.Write(Block(480, 0.5f), arrivalFrame: 480);
        t.Write(Block(480, 0.25f), arrivalFrame: 960);
        var dest = new float[960 * 2];
        int real = t.Read(0, 960, dest);
        Assert.Equal(960, real);
        Assert.Equal(0.5f, dest[0]);
        Assert.Equal(0.25f, dest[2 * 959]);
    }

    [Fact]
    public void SinDatosDaSilencioYAvanza()
    {
        var t = new AudioTimeline();
        var dest = new float[480 * 2];
        Array.Fill(dest, 9f);
        Assert.Equal(0, t.Read(0, 480, dest));
        Assert.All(dest, s => Assert.Equal(0f, s));
        Assert.Equal(480, t.ReadPos);
    }

    [Fact]
    public void LoopbackQueEmpiezaTardeSeUbicaEnSuMomento()
    {
        var t = new AudioTimeline();
        // Nada durante 1 s, después llega un bloque de 10 ms que termina en el cuadro 48000.
        t.Write(Block(480, 0.7f), arrivalFrame: 48000);
        Assert.Equal(48000, t.WritePos);
        var dest = new float[48000 * 2];
        t.Read(0, 48000, dest);
        Assert.Equal(0f, dest[2 * (47520 - 1)]);
        Assert.Equal(0.7f, dest[2 * 47520]);
    }

    [Fact]
    public void FuenteAdelantadaDescartaElExceso()
    {
        var t = new AudioTimeline(toleranceFrames: 2880);
        t.Write(Block(4800, 0.1f), arrivalFrame: 4800);
        // Llega un bloque que, según el reloj, debería empezar en 0 (ya escribimos hasta 4800): adelantada 4800.
        t.Write(Block(4800, 0.2f), arrivalFrame: 4800);
        Assert.Equal(4800, t.WritePos);
    }

    [Fact]
    public void DesvioChicoNoSeCorrige()
    {
        var t = new AudioTimeline(toleranceFrames: 2880);
        t.Write(Block(480, 0.1f), arrivalFrame: 480);
        t.Write(Block(480, 0.1f), arrivalFrame: 960 + 1000);   // 1000 cuadros de jitter
        Assert.Equal(960, t.WritePos);
    }

    [Fact]
    public void DatosTardiosParaZonaYaMezcladaSeDescartan()
    {
        var t = new AudioTimeline(toleranceFrames: 2880);
        var dest = new float[9600 * 2];
        t.Read(0, 9600, dest);    // el mezclador ya pasó 200 ms
        t.Write(Block(480, 0.3f), arrivalFrame: 480);  // llega tardísimo
        Assert.True(t.WritePos >= 9600 || t.Buffered == 0);
        Assert.Equal(0, t.Read(9600, 480, dest));
    }

    [Fact]
    public void CapacidadLlenaTiraLoMasViejo()
    {
        var t = new AudioTimeline(capacityFrames: 1000, toleranceFrames: 100_000);
        t.Write(Block(800, 0.1f), arrivalFrame: 800);
        t.Write(Block(800, 0.2f), arrivalFrame: 1600);
        Assert.Equal(1000, t.Buffered);
        Assert.Equal(600, t.ReadPos);
    }

    [Fact]
    public void PicoSeMideYSeReinicia()
    {
        var t = new AudioTimeline(buffering: false);
        t.Write(new float[] { 0.1f, -0.6f, 0.3f, 0.2f }, 2);
        Assert.Equal(0.6f, t.TakePeak());
        Assert.Equal(0f, t.TakePeak());
    }
}

public class AudioMixerTests
{
    private static AudioTimeline Filled(int frames, float value)
    {
        var t = new AudioTimeline();
        var v = new float[frames * 2];
        Array.Fill(v, value);
        t.Write(v, frames);
        return t;
    }

    [Fact]
    public void FuenteSinDatosAportaSilencio()
    {
        var a = Filled(480, 0.3f);
        var b = new AudioTimeline();
        var m = new AudioMixer([new MixerInput("system", a, 1f), new MixerInput("mic", b, 1f)]);
        var o = m.Mix(480).ToArray();
        Assert.Equal(960, o.Length);
        Assert.All(o, s => Assert.Equal(0.3f, s, 5));
        Assert.Equal(480, m.Position);
    }

    [Fact]
    public void MezclaSiempreProduceLoPedidoAunqueNoHayaNada()
    {
        var m = new AudioMixer([new MixerInput("mic", new AudioTimeline(), 1f)]);
        var o = m.Mix(4800).ToArray();
        Assert.Equal(9600, o.Length);
        Assert.All(o, s => Assert.Equal(0f, s));
    }

    [Fact]
    public void GananciaPorFuente()
    {
        var a = Filled(10, 0.2f);
        var b = Filled(10, 0.1f);
        var m = new AudioMixer([new MixerInput("a", a, 0.5f), new MixerInput("b", b, 2f)]);
        var o = m.Mix(10).ToArray();
        Assert.Equal(0.3f, o[0], 5);   // 0,1 + 0,2
        var levels = m.TakeLevels();
        Assert.Equal(0.1f, levels[0].Peak, 5);
        Assert.Equal(0.2f, levels[1].Peak, 5);
        Assert.All(m.TakeLevels(), l => Assert.Equal(0f, l.Peak));
    }

    [Fact]
    public void SaturacionSuaveNuncaPasaDeUno()
    {
        var a = Filled(10, 0.9f);
        var b = Filled(10, 0.9f);
        var m = new AudioMixer([new MixerInput("a", a, 1f), new MixerInput("b", b, 1f)]);
        var o = m.Mix(10).ToArray();
        Assert.All(o, s => Assert.InRange(s, 0.9f, 1f));
        Assert.All(m.TakeLevels(), l => Assert.Equal(0.9f, l.Peak, 5));
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(-0.8f, -0.8f)]
    public void SaturacionSuaveEsIdentidadAbajoDelUmbral(float x, float expected)
    {
        Assert.Equal(expected, AudioMixer.SoftClip(x), 6);
    }

    [Fact]
    public void SaturacionSuaveEsMonotonaYAcotada()
    {
        float prev = AudioMixer.SoftClip(0.8f);
        for (float x = 0.81f; x < 10f; x += 0.01f)
        {
            float y = AudioMixer.SoftClip(x);
            Assert.True(y >= prev);
            Assert.True(y <= 1f);
            Assert.Equal(-y, AudioMixer.SoftClip(-x), 6);
            prev = y;
        }
    }

    [Fact]
    public void IdsDuplicadosSeMezclanUnaSolaVez()
    {
        var a = Filled(10, 0.2f);
        var m = new AudioMixer([new MixerInput("mic1", a, 1f), new MixerInput("MIC1", a, 1f)]);
        Assert.Single(m.Inputs);
        var o = m.Mix(10).ToArray();
        Assert.Equal(0.2f, o[0], 5);
    }

    [Fact]
    public void Pcm16()
    {
        var bytes = new byte[6];
        AudioMixer.ToPcm16(new[] { 1f, -1f, 2f }, bytes);
        Assert.Equal(32767, BitConverter.ToInt16(bytes, 0));
        Assert.Equal(-32767, BitConverter.ToInt16(bytes, 2));
        Assert.Equal(32767, BitConverter.ToInt16(bytes, 4));
    }
}
