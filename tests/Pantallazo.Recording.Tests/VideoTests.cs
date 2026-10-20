namespace Pantallazo.Recording.Tests;

public class FrameGridTests
{
    private const long Ms = 10_000; // 1 ms en unidades de 100 ns

    [Fact]
    public void PrimerCuadroSiempreSeAcepta()
    {
        var g = new FrameGrid(30);
        Assert.True(g.TryAccept(123_456_789));
        Assert.Equal(0, g.LastSlot);
    }

    [Fact]
    public void A120HzPidiendo60SeAceptaUnoDeCadaDos()
    {
        var g = new FrameGrid(60);
        long t0 = 1_000_000;
        int accepted = 0;
        for (int i = 0; i < 120; i++)
        {
            long t = t0 + (long)(i * (10_000_000.0 / 120));
            if (g.TryAccept(t)) accepted++;
        }
        Assert.InRange(accepted, 59, 61);
    }

    [Fact]
    public void RepetidosDentroDelMismoRefrescoSeDescartan()
    {
        var g = new FrameGrid(60);
        Assert.True(g.TryAccept(0));
        Assert.False(g.TryAccept(1 * Ms));   // mismo refresco
        Assert.False(g.TryAccept(8 * Ms));   // ranura floor(0,48+0,5)=0
        Assert.True(g.TryAccept(17 * Ms));   // ranura 1
        Assert.False(g.TryAccept(18 * Ms));
    }

    [Fact]
    public void RanuraRedondeaAlMasCercano()
    {
        var g = new FrameGrid(30);
        g.TryAccept(0);
        // 1/30 s = 33,33 ms. 16 ms -> 0,48 -> floor(0,98)=0 ; 17 ms -> 0,51 -> 1
        Assert.Equal(0, g.SlotOf(16 * Ms));
        Assert.Equal(1, g.SlotOf(17 * Ms));
        Assert.Equal(30, g.SlotOf(1000 * Ms));
    }

    [Fact]
    public void PantallaQuietaNoGeneraCuadrosYDespuesSigue()
    {
        var g = new FrameGrid(30);
        Assert.True(g.TryAccept(0));
        Assert.True(g.TryAccept(5000 * Ms));   // 5 s después: ranura 150
        Assert.Equal(150, g.LastSlot);
        Assert.False(g.TryAccept(5010 * Ms));
    }

    [Fact]
    public void TiempoAnteriorNoSeAcepta()
    {
        var g = new FrameGrid(60);
        g.TryAccept(1000 * Ms);
        Assert.False(g.TryAccept(900 * Ms));
    }
}

public class VideoMathTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(2560, 1440, 2560, 1440)]
    [InlineData(3840, 2160, 3840, 2160)]
    [InlineData(1921, 1081, 1920, 1080)]
    public void HardwareSinEscalarSiEntra(int w, int h, int ew, int eh)
    {
        Assert.Equal((ew, eh), VideoMath.OutputSize(w, h, software: false));
    }

    [Fact]
    public void HardwareEscalaEscritorioAncho()
    {
        // Dos monitores 2560x1440 lado a lado: 5120x1440 -> ancho 4096, alto proporcional par.
        var (w, h) = VideoMath.OutputSize(5120, 1440, software: false);
        Assert.Equal(4096, w);
        Assert.Equal(1152, h);
        Assert.True(w % 2 == 0 && h % 2 == 0);
    }

    [Fact]
    public void HardwareEscalaPorAlto()
    {
        var (w, h) = VideoMath.OutputSize(3840, 2400, software: false);
        Assert.Equal(2304, h);
        Assert.True(w <= 4096 && w % 2 == 0);
        Assert.InRange(w / (double)h, 1.59, 1.61);
    }

    [Fact]
    public void SoftwareModoSeguro1080p()
    {
        Assert.Equal((1920, 1080), VideoMath.OutputSize(2560, 1440, software: true));
        Assert.Equal((1280, 720), VideoMath.OutputSize(1280, 720, software: true));
        var (w, h) = VideoMath.OutputSize(2560, 1600, software: true);
        Assert.Equal(1080, h);
        Assert.Equal(1728, w);
    }

    [Fact]
    public void ResolucionElegidaAchicaProporcionalYNuncaAgranda()
    {
        Assert.Equal((1920, 1080), VideoMath.OutputSize(2560, 1440, software: false, maxHeight: 1080));
        Assert.Equal((1280, 720), VideoMath.OutputSize(2560, 1440, software: false, maxHeight: 720));
        Assert.Equal((1600, 900), VideoMath.OutputSize(1600, 900, software: false, maxHeight: 1080)); // no agranda
        Assert.Equal((2560, 1440), VideoMath.OutputSize(2560, 1440, software: false, maxHeight: null));
        // Región no 16:9: mantiene la proporción y queda en pares.
        var (w, h) = VideoMath.OutputSize(2000, 1400, software: false, maxHeight: 1080);
        Assert.Equal(1080, h);
        Assert.Equal(1542, w);
        // Dos monitores lado a lado: 5120×1440 → 3840×1080.
        Assert.Equal((3840, 1080), VideoMath.OutputSize(5120, 1440, software: false, maxHeight: 1080));
    }

    [Fact]
    public void TamanoPedidoSinLimitesDelEncoder()
    {
        Assert.Equal((1000, 700), VideoMath.WantedSize(1001, 701, null));
        Assert.Equal((1920, 1080), VideoMath.WantedSize(2560, 1440, 1080));
        Assert.Equal((5120, 1440), VideoMath.WantedSize(5120, 1440, null));
    }

    [Fact]
    public void DimensionesImparesSeRecortanAPar()
    {
        Assert.Equal((800, 600), VideoMath.EvenFloor(801, 601));
        Assert.Equal((2, 2), VideoMath.EvenFloor(1, 1));
    }

    [Theory]
    [InlineData(1920, 1080, 30, VideoQuality.Balanced, 6_220_800)]
    [InlineData(1920, 1080, 60, VideoQuality.Balanced, 12_441_600)]
    [InlineData(1920, 1080, 60, VideoQuality.High, 19_906_560)]
    [InlineData(1920, 1080, 60, VideoQuality.Low, 7_464_960)]
    [InlineData(1280, 720, 30, VideoQuality.Low, 4_000_000)]   // acotado abajo
    [InlineData(3840, 2160, 120, VideoQuality.High, 60_000_000)] // acotado arriba
    public void Bitrate(int w, int h, int fps, VideoQuality q, int expected)
    {
        Assert.Equal(expected, VideoMath.Bitrate(w, h, fps, q));
    }

    [Fact]
    public void FpsSoftwareMaximo60()
    {
        Assert.Equal(60, VideoMath.EffectiveFps(144, software: true));
        Assert.Equal(144, VideoMath.EffectiveFps(144, software: false));
        Assert.Equal(30, VideoMath.EffectiveFps(0, software: false));
    }
}

public class CanvasLayoutTests
{
    private static DisplayInfo D(string id, int x, int y, int w, int h, bool primary = false)
        => new(id, id, x, y, w, h, 60, primary, 0);

    private static readonly DisplayInfo[] TwoMonitors =
    [
        D(@"\\.\DISPLAY1", 0, 0, 2560, 1440, primary: true),
        D(@"\\.\DISPLAY2", -1920, 360, 1920, 1080),
    ];

    [Fact]
    public void TodosLosMonitoresUsaElEscritorioVirtual()
    {
        var canvas = CanvasLayout.ResolveCanvas(CaptureTargetKind.AllDisplays, TwoMonitors, null, null);
        Assert.Equal(new RectI(-1920, 0, 4480, 1440), canvas);
        var placements = CanvasLayout.Place(canvas, TwoMonitors);
        Assert.Equal(2, placements.Count);
        var p1 = placements.Single(p => p.Display.Id.EndsWith('1'));
        var p2 = placements.Single(p => p.Display.Id.EndsWith('2'));
        Assert.Equal((1920, 0), (p1.DestX, p1.DestY));
        Assert.Equal(new RectI(0, 0, 2560, 1440), p1.Source);
        Assert.Equal((0, 360), (p2.DestX, p2.DestY));   // debajo queda negro (0..360)
        Assert.Equal(new RectI(0, 0, 1920, 1080), p2.Source);
    }

    [Fact]
    public void UnMonitor()
    {
        var canvas = CanvasLayout.ResolveCanvas(CaptureTargetKind.Display, TwoMonitors, @"\\.\DISPLAY2", null);
        Assert.Equal(new RectI(-1920, 360, 1920, 1080), canvas);
        var placements = CanvasLayout.Place(canvas, TwoMonitors);
        Assert.Single(placements);
        Assert.Equal((0, 0), (placements[0].DestX, placements[0].DestY));
    }

    [Fact]
    public void MonitorInexistenteDaError()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            CanvasLayout.ResolveCanvas(CaptureTargetKind.Display, TwoMonitors, @"\\.\DISPLAY9", null));
        Assert.Contains("monitor", ex.Message);
    }

    [Fact]
    public void RegionQueCruzaDosMonitores()
    {
        var canvas = CanvasLayout.ResolveCanvas(CaptureTargetKind.Region, TwoMonitors, null, (-100, 400, 300, 201));
        Assert.Equal(new RectI(-100, 400, 300, 200), canvas);   // alto impar -> par
        var placements = CanvasLayout.Place(canvas, TwoMonitors);
        Assert.Equal(2, placements.Count);
        var left = placements.Single(p => p.Display.Id.EndsWith('2'));
        var right = placements.Single(p => p.Display.Id.EndsWith('1'));
        Assert.Equal(new RectI(1820, 40, 100, 200), left.Source);
        Assert.Equal((0, 0), (left.DestX, left.DestY));
        Assert.Equal(new RectI(0, 400, 200, 200), right.Source);
        Assert.Equal((100, 0), (right.DestX, right.DestY));
        Assert.Same(right, placements[0]);   // mayor área primero
    }

    [Fact]
    public void RegionFueraDeLosMonitoresDaError()
    {
        Assert.Throws<ArgumentException>(() =>
            CanvasLayout.ResolveCanvas(CaptureTargetKind.Region, TwoMonitors, null, (5000, 5000, 100, 100)));
    }

    [Fact]
    public void RegionSeRecortaAlEscritorio()
    {
        var canvas = CanvasLayout.ResolveCanvas(CaptureTargetKind.Region, TwoMonitors, null, (2400, 1300, 400, 400));
        Assert.Equal(new RectI(2400, 1300, 160, 140), canvas);
    }
}

public class FileNamingTests
{
    [Fact]
    public void Temporal()
    {
        Assert.Equal(@"C:\v\Grabación 1.grabando.mp4", FileNaming.TempPathFor(@"C:\v\Grabación 1.mp4"));
    }

    [Fact]
    public void NombreLibreSinColision()
    {
        Assert.Equal(@"C:\v\a.mp4", FileNaming.UniquePath(@"C:\v\a.mp4", _ => false));
    }

    [Fact]
    public void NombreConNumeros()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\v\a.mp4", @"C:\v\a (2).mp4" };
        Assert.Equal(@"C:\v\a (3).mp4", FileNaming.UniquePath(@"C:\v\a.mp4", existing.Contains));
    }
}
