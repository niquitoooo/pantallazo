using System.Runtime.InteropServices;
using Pantallazo.Recording;
using Vortice.MediaFoundation;

namespace RecHarness;

/// <summary>Lee un MP4 con IMFSourceReader y resume video y audio.</summary>
internal sealed record MediaReport(
    TimeSpan Duration, long FileBytes, int Width, int Height, double DeclaredFps, string VideoSubtype,
    int VideoFrames, TimeSpan FirstVideo, TimeSpan LastVideo, double MaxGapMs,
    bool HasAudio, int AudioRate, int AudioChannels, TimeSpan AudioDuration,
    float AudioPeak, float AudioRms, float LoudestWindowRms, double LoudestWindowHz, TimeSpan LoudestWindowAt,
    double ToneSeconds, TimeSpan? SoundOnset);

internal static class MediaInspector
{
    private static readonly Guid MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    private static readonly Guid MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    private static readonly Guid MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    private static readonly Guid MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid MT_AUDIO_NUM_CHANNELS = new("37e48bf5-645e-4c5b-89de-ada9e29b696a");
    private static readonly Guid MT_AUDIO_SAMPLES_PER_SECOND = new("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
    private static readonly Guid PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    private static readonly Guid MediaTypeAudio = new("73647561-0000-0010-8000-00AA00389B71");
    private static readonly Guid AudioFloat = new("00000003-0000-0010-8000-00AA00389B71");

    public static MediaReport Inspect(string path)
    {
        MediaFactory.MFStartup(false).CheckError();
        long bytes = new FileInfo(path).Length;

        // ---- video
        TimeSpan duration;
        int w, h, frames = 0;
        double fps;
        string subtype;
        long first = -1, last = -1;
        double maxGap = 0;
        using (var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!))
        {
            var v = reader.GetPresentationAttribute(SourceReaderIndex.MediaSource, PD_DURATION);
            duration = TimeSpan.FromTicks((long)Convert.ToUInt64(v.Value));
            using (var t = reader.GetNativeMediaType(SourceReaderIndex.FirstVideoStream, 0))
            {
                ulong size = t.GetUInt64(MT_FRAME_SIZE);
                w = (int)(size >> 32); h = (int)(size & 0xFFFFFFFF);
                ulong rate = t.GetUInt64(MT_FRAME_RATE);
                fps = (rate & 0xFFFFFFFF) == 0 ? 0 : (double)(rate >> 32) / (rate & 0xFFFFFFFF);
                var g = t.GetGUID(MT_SUBTYPE);
                subtype = FourCc(g);
            }
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
            while (true)
            {
                using var s = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None,
                    out _, out var flags, out long ts);
                if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                if (s == null) continue;
                frames++;
                if (first < 0) first = ts;
                if (last >= 0) maxGap = Math.Max(maxGap, (ts - last) / 10_000.0);
                last = ts;
            }
        }

        // ---- audio (decodificado a float)
        bool hasAudio = false;
        int rateHz = 0, ch = 0;
        long totalFrames = 0;
        float peak = 0;
        double sumSq = 0;
        float loudRms = 0;
        double loudHz = 0;
        TimeSpan loudAt = TimeSpan.Zero;
        double toneSeconds = 0;
        TimeSpan? onset = null;
        try
        {
            using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, null!);
            using (reader.GetNativeMediaType(SourceReaderIndex.FirstAudioStream, 0)) hasAudio = true;
            reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
            reader.SetStreamSelection(SourceReaderIndex.FirstAudioStream, true);
            using (var want = MediaFactory.MFCreateMediaType())
            {
                want.Set(MT_MAJOR_TYPE, MediaTypeAudio);
                want.Set(MT_SUBTYPE, AudioFloat);
                reader.SetCurrentMediaType(SourceReaderIndex.FirstAudioStream, want);
            }
            using (var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstAudioStream))
            {
                rateHz = (int)cur.GetUInt32(MT_AUDIO_SAMPLES_PER_SECOND);
                ch = (int)cur.GetUInt32(MT_AUDIO_NUM_CHANNELS);
            }
            int window = rateHz / 10;   // 100 ms
            int small = rateHz / 100;   // 10 ms, para el inicio del sonido
            double smallSq = 0; int smallCount = 0;
            var win = new List<float>(window);
            double winSq = 0;
            long winStart = 0;
            while (true)
            {
                using var s = reader.ReadSample(SourceReaderIndex.FirstAudioStream, SourceReaderControlFlag.None,
                    out _, out var flags, out _);
                if ((flags & SourceReaderFlag.EndOfStream) != 0) break;
                if (s == null) continue;
                using var buf = s.ConvertToContiguousBuffer();
                buf.Lock(out nint p, out _, out int len);
                try
                {
                    int n = len / 4;
                    var data = new float[n];
                    Marshal.Copy(p, data, 0, n);
                    for (int i = 0; i + ch - 1 < n; i += ch)
                    {
                        float l = data[i];
                        for (int c = 0; c < ch; c++) { float a = Math.Abs(data[i + c]); if (a > peak) peak = a; sumSq += data[i + c] * (double)data[i + c]; }
                        win.Add(l);
                        winSq += l * (double)l;
                        totalFrames++;
                        smallSq += l * (double)l;
                        if (++smallCount == small)
                        {
                            if (onset == null && Math.Sqrt(smallSq / small) > 0.01)
                                onset = TimeSpan.FromSeconds((totalFrames - small) / (double)rateHz);
                            smallSq = 0; smallCount = 0;
                        }
                        if (win.Count == window)
                        {
                            float rms = (float)Math.Sqrt(winSq / window);
                            if (rms > 0.02f && IsTone(win, rateHz)) toneSeconds += 0.1;
                            if (rms > loudRms)
                            {
                                loudRms = rms;
                                loudHz = ZeroCrossingHz(win, rateHz);
                                loudAt = TimeSpan.FromSeconds(winStart / (double)rateHz);
                            }
                            win.Clear();
                            winSq = 0;
                            winStart = totalFrames;
                        }
                    }
                }
                finally { buf.Unlock(); }
            }
        }
        catch (Exception) when (!hasAudio) { }

        float totalRms = totalFrames > 0 && ch > 0 ? (float)Math.Sqrt(sumSq / (totalFrames * ch)) : 0;
        return new MediaReport(duration, bytes, w, h, fps, subtype, frames,
            TimeSpan.FromTicks(Math.Max(0, first)), TimeSpan.FromTicks(Math.Max(0, last)), maxGap,
            hasAudio, rateHz, ch, TimeSpan.FromSeconds(rateHz > 0 ? totalFrames / (double)rateHz : 0),
            peak, totalRms, loudRms, loudHz, loudAt, toneSeconds, onset);
    }

    /// <summary>Tiempo del primer cuadro en el que todos los puntos son blancos (destello de la animación).</summary>
    public static TimeSpan? FindWhiteFrame(string path, (int X, int Y)[] points)
    {
        MediaFactory.MFStartup(false).CheckError();
        using var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"), 1u);
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attrs);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var want = MediaFactory.MFCreateMediaType())
        {
            want.Set(MT_MAJOR_TYPE, new Guid("73646976-0000-0010-8000-00AA00389B71"));
            want.Set(MT_SUBTYPE, new Guid("00000016-0000-0010-8000-00AA00389B71"));
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, want);
        }
        int w, h;
        using (var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
        {
            ulong size = cur.GetUInt64(MT_FRAME_SIZE);
            w = (int)(size >> 32); h = (int)(size & 0xFFFFFFFF);
        }
        while (true)
        {
            using var s = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out long ts);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) return null;
            if (s == null) continue;
            using var buf = s.ConvertToContiguousBuffer();
            buf.Lock(out nint p, out _, out int total);
            try
            {
                int pitch = w * 4;
                foreach (int align in new[] { 1, 16, 32, 64, 128, 256 })
                {
                    int cand = (w + align - 1) / align * align * 4;
                    if (total % cand == 0 && total / cand >= h && total / cand < h + 64) { pitch = cand; break; }
                }
                bool all = true;
                foreach (var (x, y) in points)
                {
                    if (x < 0 || y < 0 || x >= w || y >= h) { all = false; break; }
                    int px = Marshal.ReadInt32(p + y * pitch + x * 4);
                    int b = px & 0xFF, g = (px >> 8) & 0xFF, r = (px >> 16) & 0xFF;
                    if (r < 235 || g < 235 || b < 235) { all = false; break; }
                }
                if (all) return TimeSpan.FromTicks(ts);
            }
            finally { buf.Unlock(); }
        }
    }

    /// <summary>Decodifica el primer cuadro en o después de <paramref name="at"/> y lo guarda como PNG.</summary>
    public static void SaveFrame(string path, TimeSpan at, string pngPath)
    {
        MediaFactory.MFStartup(false).CheckError();
        using var attrs = MediaFactory.MFCreateAttributes(1);
        attrs.Set(new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d"), 1u);   // MF_SOURCE_READER_ENABLE_VIDEO_PROCESSING
        using var reader = MediaFactory.MFCreateSourceReaderFromURL(path, attrs);
        reader.SetStreamSelection(SourceReaderIndex.AllStreams, false);
        reader.SetStreamSelection(SourceReaderIndex.FirstVideoStream, true);
        using (var want = MediaFactory.MFCreateMediaType())
        {
            want.Set(MT_MAJOR_TYPE, new Guid("73646976-0000-0010-8000-00AA00389B71"));
            want.Set(MT_SUBTYPE, new Guid("00000016-0000-0010-8000-00AA00389B71"));   // RGB32
            reader.SetCurrentMediaType(SourceReaderIndex.FirstVideoStream, want);
        }
        int w, h, stride = 0;
        using (var cur = reader.GetCurrentMediaType(SourceReaderIndex.FirstVideoStream))
        {
            ulong size = cur.GetUInt64(MT_FRAME_SIZE);
            w = (int)(size >> 32); h = (int)(size & 0xFFFFFFFF);
            try { stride = unchecked((int)cur.GetUInt32(new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6"))); } catch { }
        }
        while (true)
        {
            using var s = reader.ReadSample(SourceReaderIndex.FirstVideoStream, SourceReaderControlFlag.None, out _, out var flags, out long ts);
            if ((flags & SourceReaderFlag.EndOfStream) != 0) throw new InvalidOperationException("No hay cuadro en ese tiempo.");
            if (s == null || ts < at.Ticks) continue;
            using var buf = s.ConvertToContiguousBuffer();
            buf.Lock(out nint p, out _, out int total);
            try
            {
                // El decodificador alinea ancho y alto (p. ej. 1000×700 → 1008×704): se deduce el pitch real del tamaño.
                int pitch = w * 4;
                foreach (int align in new[] { 1, 16, 32, 64, 128, 256 })
                {
                    int cand = (w + align - 1) / align * align * 4;
                    if (total % cand == 0 && total / cand >= h && total / cand < h + 64) { pitch = cand; break; }
                }
                _ = stride;
                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
                var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, bmp.PixelFormat);
                var row = new byte[w * 4];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(p + y * pitch, row, 0, row.Length);
                    Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
                }
                bmp.UnlockBits(data);
                bmp.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"Cuadro de {ts / 1e7:0.000} s ({w}×{h}, stride {pitch}, buffer {total} bytes) guardado en {pngPath}");
            }
            finally { buf.Unlock(); }
            return;
        }
    }

    private static double ZeroCrossingHz(List<float> win, int rate)
    {
        int crossings = 0;
        for (int i = 1; i < win.Count; i++) if (win[i - 1] < 0 && win[i] >= 0) crossings++;
        return crossings / (win.Count / (double)rate);
    }

    private static bool IsTone(List<float> win, int rate)
    {
        double hz = ZeroCrossingHz(win, rate);
        return hz is > 950 and < 1050;
    }

    private static string FourCc(Guid g)
    {
        var b = g.ToByteArray();
        uint code = BitConverter.ToUInt32(b, 0);
        if (code < 256) return code.ToString();
        return new string([(char)b[0], (char)b[1], (char)b[2], (char)b[3]]);
    }
}
