using System.IO;
using System.Text.Json;
using Pantallazo.Core;

namespace Pantallazo.Tests;

public class HotkeyTests
{
    [Fact]
    public void Los_predeterminados_son_los_del_pedido()
    {
        var d = HotkeyDefaults.Create();
        Assert.Equal("Impr Pant", d.Single(h => h.Action == HotkeyAction.FullScreen).Gesture.ToString());
        Assert.Equal("Ctrl + Impr Pant", d.Single(h => h.Action == HotkeyAction.Region).Gesture.ToString());
        Assert.Equal("Alt + Impr Pant", d.Single(h => h.Action == HotkeyAction.Record).Gesture.ToString());
        Assert.Empty(HotkeyConflicts.Find(d));
    }

    [Theory]
    [InlineData(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x53, "Ctrl + Mayús + S")]
    [InlineData(HotkeyModifiers.Windows | HotkeyModifiers.Alt, 0x72, "Alt + Win + F3")]
    [InlineData(HotkeyModifiers.None, 0x61, "Num 1")]
    [InlineData(HotkeyModifiers.None, 0, "Sin asignar")]
    public void Formatea_en_castellano(HotkeyModifiers mods, int key, string expected) =>
        Assert.Equal(expected, new HotkeyGesture(mods, key).ToString());

    [Fact]
    public void Un_modificador_solo_no_es_valido()
    {
        Assert.False(new HotkeyGesture(HotkeyModifiers.Control, 0x11).IsValid);
        Assert.False(HotkeyGesture.None.IsValid);
        Assert.True(new HotkeyGesture(HotkeyModifiers.None, 0x2C).IsValid);
    }

    [Fact]
    public void Detecta_conflictos_entre_acciones()
    {
        var g = new HotkeyGesture(HotkeyModifiers.Control, 0x2C);
        var list = new[]
        {
            new HotkeyBinding(HotkeyAction.Region, g),
            new HotkeyBinding(HotkeyAction.Record, g),
            new HotkeyBinding(HotkeyAction.FullScreen, new HotkeyGesture(HotkeyModifiers.None, 0x2C)),
            new HotkeyBinding(HotkeyAction.FullScreen, HotkeyGesture.None),
            new HotkeyBinding(HotkeyAction.Record, HotkeyGesture.None),
        };
        var conflicts = HotkeyConflicts.Find(list);
        Assert.Single(conflicts);
        Assert.Equal(2, conflicts[0].Count);
        Assert.True(HotkeyConflicts.IsInConflict(list, list[0]));
        Assert.False(HotkeyConflicts.IsInConflict(list, list[2]));
        Assert.False(HotkeyConflicts.IsInConflict(list, list[3])); // vacías no chocan
    }
}

public class FileNameTests
{
    [Fact]
    public void Nombres_con_fecha_y_hora()
    {
        var t = new DateTime(2026, 10, 17, 16, 45, 32);
        Assert.Equal("Captura_2026-10-17_16-45-32.png", FileNames.Screenshot(t));
        Assert.Equal("Grabacion_2026-10-17_16-45-32.mp4", FileNames.Recording(t));
    }

    [Fact]
    public void No_pisa_archivos_existentes()
    {
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine("C:\\x", "a.png"),
            Path.Combine("C:\\x", "a (2).png"),
        };
        Assert.Equal(Path.Combine("C:\\x", "b.png"), FileNames.MakeUnique("C:\\x", "b.png", existing.Contains));
        Assert.Equal(Path.Combine("C:\\x", "a (3).png"), FileNames.MakeUnique("C:\\x", "a.png", existing.Contains));
    }
}

public class RegionGeometryTests
{
    [Fact]
    public void Arrastre_normaliza_y_se_limita_a_la_imagen()
    {
        Assert.Equal(new PixelRect(10, 20, 90, 50), RegionGeometry.FromDrag(100, 70, 10, 20, false, 1920, 1080));
        Assert.Equal(new PixelRect(1900, 0, 20, 70), RegionGeometry.FromDrag(1900, 70, 2500, -40, false, 1920, 1080));
    }

    [Fact]
    public void Shift_fuerza_cuadrado_hacia_el_mouse_y_cabe_en_la_imagen()
    {
        var r = RegionGeometry.FromDrag(100, 100, 40, 130, true, 1920, 1080);
        Assert.Equal(new PixelRect(40, 100, 60, 60), r);
        // Hacia abajo a la derecha, cerca del borde: el lado se recorta al espacio disponible.
        Assert.Equal(new PixelRect(1000, 1000, 80, 80), RegionGeometry.FromDrag(1000, 1000, 1500, 1200, true, 1920, 1080));
    }

    [Fact]
    public void Panel_de_lupa_se_da_vuelta_en_los_bordes()
    {
        Assert.Equal((118.0, 118.0), RegionGeometry.PanelNearPointer(100, 100, 152, 190, 1920, 1080));
        Assert.Equal((1900 - 152 - 18.0, 1000 - 190 - 18.0), RegionGeometry.PanelNearPointer(1900, 1000, 152, 190, 1920, 1080));
    }

    [Fact]
    public void Rotulo_de_tamano_abajo_o_arriba()
    {
        Assert.Equal((100.0, 308.0), RegionGeometry.SizeLabelPosition(100, 100, 300, 1920, 1080));
        Assert.Equal((100.0, 864.0), RegionGeometry.SizeLabelPosition(100, 900, 1070, 1920, 1080));
        Assert.Equal((1820.0, 308.0), RegionGeometry.SizeLabelPosition(1900, 100, 300, 1920, 1080));
    }

    [Fact]
    public void Lupa_impar_entre_3_y_el_maximo()
    {
        Assert.Equal(15, RegionGeometry.ClampMagnifierPixels(15));
        Assert.Equal(17, RegionGeometry.ClampMagnifierPixels(16));
        Assert.Equal(3, RegionGeometry.ClampMagnifierPixels(1));
        Assert.Equal(35, RegionGeometry.ClampMagnifierPixels(99));
        Assert.Equal(49 > 35 ? 35 : 49, RegionGeometry.ClampMagnifierPixels(99, 50));
        Assert.Equal(15, RegionGeometry.ClampMagnifierPixels(17, 16));
    }

    [Fact]
    public void Union_e_interseccion()
    {
        var a = new PixelRect(-1920, 0, 1920, 1080);
        var b = new PixelRect(0, -200, 2560, 1440);
        Assert.Equal(new PixelRect(-1920, -200, 4480, 1440), a.Union(b));
        Assert.True(a.Intersect(b).IsEmpty);
        Assert.Equal(new PixelRect(0, 0, 10, 10), new PixelRect(-5, -5, 15, 15).Intersect(new PixelRect(0, 0, 100, 100)));
    }
}

public class ColorTests
{
    [Theory]
    [InlineData("#4F6CB8")]
    [InlineData("#E91E63")]
    [InlineData("#808080")]
    [InlineData("#00FF00")]
    public void Esquemas_con_contraste_suficiente(string seedHex)
    {
        Assert.True(ColorMath.TryParseHex(seedHex, out var seed));
        foreach (var dark in new[] { false, true })
        {
            var s = MaterialScheme.Create(seed, dark);
            Assert.True(ColorMath.Contrast(s["Primary"], s["OnPrimary"]) >= 4.5, $"{seedHex} dark={dark} primary");
            Assert.True(ColorMath.Contrast(s["Surface"], s["OnSurface"]) >= 7, $"{seedHex} dark={dark} surface");
            Assert.True(ColorMath.Contrast(s["SecondaryContainer"], s["OnSecondaryContainer"]) >= 4.5);
            Assert.True(ColorMath.Contrast(s["SurfaceContainerHigh"], s["OnSurfaceVariant"]) >= 4.5);
        }
    }

    [Theory]
    [InlineData("#6D6D6D")]
    [InlineData("#808080")]
    [InlineData("#6E6D6C")] // casi gris
    public void Semilla_gris_da_esquema_gris(string seedHex)
    {
        Assert.True(ColorMath.TryParseHex(seedHex, out var seed));
        foreach (var dark in new[] { false, true })
        {
            var s = MaterialScheme.Create(seed, dark);
            foreach (var role in new[] { "Primary", "PrimaryContainer", "SecondaryContainer", "TertiaryContainer", "Surface", "SurfaceContainerHigh", "Outline" })
            {
                var (_, chroma, _) = ColorMath.ToLch(s[role]);
                Assert.True(chroma < 2, $"{seedHex} dark={dark} {role} {s[role].ToHex()} croma {chroma:0.0}");
            }
            Assert.True(ColorMath.Contrast(s["Primary"], s["OnPrimary"]) >= 4.5);
        }
    }

    [Fact]
    public void Tono_es_L_de_Lab()
    {
        var c = ColorMath.FromLch(50, 30, 250);
        Assert.InRange(ColorMath.ToLch(c).L, 49.5, 50.5);
    }
}

public class SettingsTests
{
    [Fact]
    public void Ida_y_vuelta_por_json_y_normalizacion()
    {
        var s = new AppSettings { Fps = 60, Theme = ThemePreference.Dark, SeedColor = "#112233" };
        s.Hotkeys[0] = s.Hotkeys[0] with { Gesture = new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x41) };
        var json = JsonSerializer.Serialize(s);
        var back = JsonSerializer.Deserialize<AppSettings>(json)!;
        back.Normalize();
        Assert.Equal(60, back.Fps);
        Assert.Equal(ThemePreference.Dark, back.Theme);
        Assert.Equal("Ctrl + Mayús + A", back.Hotkeys[0].Gesture.ToString());

        var bad = new AppSettings { Fps = 7, SeedColor = "azul", Hotkeys = [] };
        bad.Normalize();
        Assert.Equal(30, bad.Fps);
        Assert.Null(bad.SeedColor);
        Assert.Equal(3, bad.Hotkeys.Count);
    }

    [Theory]
    [InlineData(120, 120, true)]
    [InlineData(144, 120, false)]
    [InlineData(144, 143, true)]   // monitores de 143,98 Hz
    [InlineData(144, 165, true)]
    [InlineData(60, 60, true)]
    public void Fps144OnlyWithFastMonitor(int fps, int hz, bool offered)
    {
        Assert.Equal(offered, AppSettings.IsFpsOffered(fps, hz));
        var s = new AppSettings { Fps = 144 };
        s.Normalize();
        Assert.Equal(144, s.Fps);
    }

    [Theory]
    [InlineData("Mi captura", true)]
    [InlineData("Captura_2026-10-17 (2)", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a/b", false)]
    [InlineData("a:b", false)]
    [InlineData("que?", false)]
    [InlineData("fin.", false)]
    [InlineData("con", false)]
    [InlineData("LPT1", false)]
    public void NewNameValidation(string name, bool ok)
    {
        Assert.Equal(ok, FileNames.ValidateNewName(name) is null);
    }

    [Fact]
    public void NameTemplateDefaults()
    {
        var t = new DateTime(2026, 10, 18, 0, 15, 32);
        Assert.Equal("Captura 001", NameTemplate.Expand(NameTemplate.DefaultScreenshot, new NameContext(false, t, "Chrome", "x", 1)));
        Assert.Equal("Grabacion 012", NameTemplate.Expand(NameTemplate.DefaultRecording, new NameContext(true, t, null, null, 12)));
        // Las predeterminadas viejas (nunca tocadas) pasan a las nuevas; una propia se respeta.
        var old = new AppSettings { ScreenshotNameTemplate = NameTemplate.OldDefaultsScreenshot[0], RecordingNameTemplate = "{tipo} {n}" };
        old.Normalize();
        Assert.Equal(NameTemplate.DefaultScreenshot, old.ScreenshotNameTemplate);
        Assert.Equal("{tipo} {n}", old.RecordingNameTemplate);
    }

    [Fact]
    public void NameTemplateTags()
    {
        var t = new DateTime(2026, 10, 18, 0, 15, 32);
        var c = new NameContext(false, t, "Google Chrome", "YouTube: \"Cocina\" | Inicio", 7);
        Assert.Equal("Captura Google Chrome 2026-10-18 00.15.32", NameTemplate.Expand("{tipo} {app} {aaaa}-{mm}-{dd} {hh}.{min}.{ss}", c));
        Assert.Equal("YouTube Cocina Inicio", NameTemplate.Expand("{ventana}", c)); // sin caracteres prohibidos
        Assert.Equal("Grabación 007", NameTemplate.Expand("{tipo} {n}", c with { Recording = true }));
        Assert.Equal("Captura Escritorio", NameTemplate.Expand("{tipo} {app}", c with { App = null }));
        Assert.Equal("Captura", NameTemplate.Expand("{tipo} {ventana}", c with { Window = "" })); // sin espacio al final
        Assert.Equal("Captura 007", NameTemplate.Expand("{ventana}", c with { Window = "" })); // vacío → predeterminada
        Assert.Equal("_CON", NameTemplate.Expand("CON", c));
        Assert.True(NameTemplate.UsesCounter("Pantallazo {N}"));
    }

    [Theory]
    [InlineData("{tipo} {app}", true)]
    [InlineData("Captura", true)]
    [InlineData("", false)]
    [InlineData("{fecha}", false)]
    [InlineData("{tipo", false)]
    [InlineData("a:b {n}", false)]
    public void NameTemplateValidation(string template, bool ok)
    {
        Assert.Equal(ok, NameTemplate.Validate(template) is null);
    }
}
