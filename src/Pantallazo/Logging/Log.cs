using System.Globalization;
using Pantallazo.Config;

namespace Pantallazo.Logging;

/// <summary>Log local, un archivo por día, se borran los de más de 14 días. No guarda datos personales.</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _folder;

    public static void Init(string? folder = null)
    {
        _folder = folder ?? AppPaths.LogsFolder;
        try
        {
            Directory.CreateDirectory(_folder);
            foreach (var f in Directory.EnumerateFiles(_folder, "pantallazo-*.log"))
                if (File.GetLastWriteTime(f) < DateTime.Now.AddDays(-14)) File.Delete(f);
        }
        catch { }
    }

    public static string? CurrentFile =>
        _folder is null ? null : Path.Combine(_folder, $"pantallazo-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("AVISO", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex is null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        var file = CurrentFile;
        if (file is null) return;
        var line = $"{DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)} [{level}] {message}{Environment.NewLine}";
        lock (Gate)
        {
            try { File.AppendAllText(file, line); } catch { }
        }
    }
}
