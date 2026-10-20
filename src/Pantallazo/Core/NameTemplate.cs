using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Pantallazo.Core;

/// <summary>Datos con los que se arma el nombre de un archivo nuevo.</summary>
public sealed record NameContext(bool Recording, DateTime When, string? App, string? Window, int Counter);

/// <summary>
/// Plantillas de nombre de archivo, como ShareX: texto libre con etiquetas entre llaves. La extensión la pone la app.
/// </summary>
public static partial class NameTemplate
{
    public const string DefaultScreenshot = "Captura {n}";
    public const string DefaultRecording = "Grabacion {n}";

    // Las predeterminadas de antes. Si una configuración todavía tiene alguna, nunca se tocó: pasa a la actual.
    public static readonly string[] OldDefaultsScreenshot = ["Captura_{aaaa}-{mm}-{dd}_{hh}-{min}-{ss}", "Captura_{hh}-{min}-{ss}_{dd}-{mm}-{aaaa}"];
    public static readonly string[] OldDefaultsRecording = ["Grabacion_{aaaa}-{mm}-{dd}_{hh}-{min}-{ss}", "Grabación_{hh}-{min}-{ss}_{dd}-{mm}-{aaaa}"];

    /// <summary>Etiquetas que se pueden usar, con lo que significan (para la ayuda de Configuración).</summary>
    public static readonly (string Tag, string Meaning)[] Tags =
    [
        ("{tipo}", "Captura o Grabación"),
        ("{app}", "Programa que estaba al frente"),
        ("{ventana}", "Título de esa ventana"),
        ("{aaaa}", "Año"),
        ("{mm}", "Mes"),
        ("{dd}", "Día"),
        ("{hh}", "Hora"),
        ("{min}", "Minutos"),
        ("{ss}", "Segundos"),
        ("{n}", "Número que sube en cada archivo (001, 002…)"),
    ];

    private const int MaxTextTag = 60;

    [GeneratedRegex(@"\{[^{}]*\}")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    /// <summary>null si la plantilla sirve; si no, el motivo en castellano.</summary>
    public static string? Validate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template)) return "Escribí una plantilla.";
        foreach (Match m in TagRegex().Matches(template))
            if (Tags.All(t => t.Tag != m.Value.ToLowerInvariant())) return $"No conozco la etiqueta {m.Value}.";
        var rest = TagRegex().Replace(template, "");
        if (rest.Contains('{') || rest.Contains('}')) return "Hay una llave sin cerrar.";
        if (rest.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || rest.Any(char.IsControl))
            return "La plantilla no puede tener \\ / : * ? \" < > |";
        return null;
    }

    public static bool UsesCounter(string template) => template.Contains("{n}", StringComparison.OrdinalIgnoreCase);

    /// <summary>Nombre del archivo, sin extensión. Si la plantilla no sirve o queda vacía, usa la predeterminada.</summary>
    public static string Expand(string? template, NameContext c)
    {
        var fallback = c.Recording ? DefaultRecording : DefaultScreenshot;
        if (Validate(template) is not null) template = fallback;
        var name = Clean(Replace(template!, c));
        if (name.Length == 0) name = Clean(Replace(fallback, c));
        if (name.Length > 200) name = name[..200].TrimEnd('.', ' ');
        if (FileNames.ValidateNewName(name) is not null) name = "_" + name; // nombres reservados (CON, LPT1…)
        return name;
    }

    private static string Replace(string template, NameContext c) => TagRegex().Replace(template, m => m.Value.ToLowerInvariant() switch
    {
        "{tipo}" => c.Recording ? "Grabación" : "Captura",
        "{app}" => SafeText(c.App ?? "Escritorio"),
        "{ventana}" => SafeText(c.Window ?? ""),
        "{aaaa}" => c.When.ToString("yyyy", CultureInfo.InvariantCulture),
        "{mm}" => c.When.ToString("MM", CultureInfo.InvariantCulture),
        "{dd}" => c.When.ToString("dd", CultureInfo.InvariantCulture),
        "{hh}" => c.When.ToString("HH", CultureInfo.InvariantCulture),
        "{min}" => c.When.ToString("mm", CultureInfo.InvariantCulture),
        "{ss}" => c.When.ToString("ss", CultureInfo.InvariantCulture),
        "{n}" => Math.Max(1, c.Counter).ToString("000", CultureInfo.InvariantCulture),
        _ => m.Value,
    });

    /// <summary>Texto de afuera (programa, título): sin caracteres prohibidos ni saltos, y no demasiado largo.</summary>
    private static string SafeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
            sb.Append(ch is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' || char.IsControl(ch) ? ' ' : ch);
        var s = Spaces().Replace(sb.ToString(), " ").Trim();
        return s.Length > MaxTextTag ? s[..MaxTextTag].TrimEnd() : s;
    }

    /// <summary>Espacios repetidos y puntos o espacios al final (Windows no los permite) fuera.</summary>
    private static string Clean(string name) => Spaces().Replace(name, " ").Trim().TrimEnd('.', ' ');
}
