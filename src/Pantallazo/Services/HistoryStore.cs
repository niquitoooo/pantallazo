using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Pantallazo.Config;
using Pantallazo.Logging;

namespace Pantallazo.Services;

public enum HistoryKind { Screenshot, Recording }

public sealed record HistoryItem(string Path, HistoryKind Kind, DateTime Created)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string KindText => Kind == HistoryKind.Screenshot ? "Captura" : "Grabación";
    public string When => Created.Date == DateTime.Today ? $"Hoy, {Created:HH:mm}" :
        Created.Date == DateTime.Today.AddDays(-1) ? $"Ayer, {Created:HH:mm}" : Created.ToString("dd/MM/yyyy HH:mm");
    public bool Exists => File.Exists(Path);
}

/// <summary>
/// Recientes: las imágenes y videos más nuevos de la carpeta de capturas y la de grabaciones (las de Configuración),
/// los haya hecho Pantallazo u otra cosa. Vigila las dos carpetas: lo que se agrega, borra, renombra o mueve se
/// refleja solo, y si se cambia una carpeta en Configuración pasa a mirar la nueva.
/// </summary>
public sealed class HistoryStore
{
    private const int Max = 30;
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp"];
    private static readonly string[] VideoExtensions = [".mp4", ".mkv", ".mov", ".webm", ".avi", ".wmv"];

    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private SettingsStore? _settings;
    private DispatcherTimer? _debounce;
    private int _scanVersion;

    public ObservableCollection<HistoryItem> Items { get; } = [];

    public void Load(SettingsStore settings)
    {
        _settings = settings;
        // Los avisos de las carpetas llegan en ráfagas (copiar diez archivos, una grabación que se cierra): se junta
        // todo y se vuelve a leer una vez.
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _debounce.Tick += (_, _) => { _debounce.Stop(); _ = RescanAsync(); };
        settings.Changed += (_, _) => OnUi(() =>
        {
            if (!Folders().SetEquals(_watchers.Keys)) { UpdateWatchers(); Schedule(); }
        });
        UpdateWatchers();
        Sync(ScanAll(Folders())); // la primera vez, enseguida: la ventana se abre con la lista lista
    }

    /// <summary>Lo acaba de guardar Pantallazo: aparece ya, sin esperar el aviso de la carpeta.</summary>
    public void Add(HistoryItem item)
    {
        OnUiNow(() =>
        {
            if (!Items.Any(i => SamePath(i.Path, item.Path))) Items.Insert(0, item);
            Schedule();
        });
    }

    /// <summary>Vuelve a leer las carpetas (por ejemplo al activar la ventana, por si algo cambió sin aviso).</summary>
    public void Prune() => Schedule();

    public void Remove(string path)
    {
        var item = Items.FirstOrDefault(i => SamePath(i.Path, path));
        if (item is not null) Items.Remove(item);
        Schedule();
    }

    public void Rename(string oldPath, string newPath)
    {
        var index = Items.ToList().FindIndex(i => SamePath(i.Path, oldPath));
        if (index >= 0) Items[index] = Items[index] with { Path = newPath };
        Schedule();
    }

    // ---------- Lectura de las carpetas ----------

    private HashSet<string> Folders()
    {
        var s = _settings?.Current;
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in new[] { s?.ScreenshotsFolder ?? AppPaths.DefaultScreenshotsFolder, s?.RecordingsFolder ?? AppPaths.DefaultRecordingsFolder })
        {
            try { set.Add(System.IO.Path.GetFullPath(f).TrimEnd('\\')); } catch { }
        }
        return set;
    }

    public static HistoryKind? KindOf(string fileName)
    {
        // Temporales de Pantallazo mientras escribe: no son archivos terminados.
        if (fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".grabando.mp4", StringComparison.OrdinalIgnoreCase)) return null;
        var ext = System.IO.Path.GetExtension(fileName);
        if (ImageExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return HistoryKind.Screenshot;
        if (VideoExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase)) return HistoryKind.Recording;
        return null;
    }

    private static List<HistoryItem> ScanAll(IEnumerable<string> folders)
    {
        var all = new List<HistoryItem>();
        foreach (var folder in folders)
        {
            try
            {
                var dir = new DirectoryInfo(folder);
                if (!dir.Exists) continue;
                foreach (var f in dir.EnumerateFiles())
                {
                    if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (KindOf(f.Name) is HistoryKind kind) all.Add(new HistoryItem(f.FullName, kind, f.LastWriteTime));
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"No se pudo leer la carpeta {folder}: {ex.Message}");
            }
        }
        return all.OrderByDescending(i => i.Created).Take(Max).ToList();
    }

    private async Task RescanAsync()
    {
        var version = ++_scanVersion;
        var folders = Folders();
        var found = await Task.Run(() => ScanAll(folders));
        if (version == _scanVersion) Sync(found); // si mientras tanto empezó otra lectura, gana esa
    }

    /// <summary>
    /// Deja la lista igual a <paramref name="wanted"/> moviendo, agregando y sacando lo justo: las filas que no
    /// cambian conservan su miniatura.
    /// </summary>
    private void Sync(List<HistoryItem> wanted)
    {
        for (var i = 0; i < wanted.Count; i++)
        {
            var w = wanted[i];
            if (i < Items.Count && SamePath(Items[i].Path, w.Path))
            {
                if (Items[i] != w) Items[i] = w;
                continue;
            }
            var j = -1;
            for (var k = i + 1; k < Items.Count; k++) if (SamePath(Items[k].Path, w.Path)) { j = k; break; }
            if (j >= 0)
            {
                Items.Move(j, i);
                if (Items[i] != w) Items[i] = w;
            }
            else Items.Insert(i, w);
        }
        while (Items.Count > wanted.Count) Items.RemoveAt(Items.Count - 1);
    }

    // ---------- Vigilancia ----------

    private void UpdateWatchers()
    {
        var folders = Folders();
        foreach (var gone in _watchers.Keys.Where(k => !folders.Contains(k)).ToList())
        {
            _watchers[gone].Dispose();
            _watchers.Remove(gone);
        }
        foreach (var folder in folders.Where(f => !_watchers.ContainsKey(f)))
        {
            try
            {
                Directory.CreateDirectory(folder);
                var w = new FileSystemWatcher(folder)
                {
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                    IncludeSubdirectories = false,
                };
                FileSystemEventHandler changed = (_, e) => { if (Relevant(e.Name)) Schedule(); };
                w.Created += changed;
                w.Deleted += changed;
                w.Changed += changed;
                w.Renamed += (_, e) => { if (Relevant(e.Name) || Relevant(e.OldName)) Schedule(); };
                w.Error += (_, _) => Schedule(); // se perdieron avisos: se vuelve a leer todo
                w.EnableRaisingEvents = true;
                _watchers[folder] = w;
            }
            catch (Exception ex)
            {
                // _watchers queda sin esa carpeta: igual se lee al abrir la ventana y al activarla.
                Log.Warn($"No se puede vigilar la carpeta {folder}: {ex.Message}");
            }
        }
    }

    private static bool Relevant(string? name) => name is not null && KindOf(name) is not null;

    private void Schedule() => OnUi(() => { _debounce?.Stop(); _debounce?.Start(); });

    private static bool SamePath(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void OnUi(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.HasShutdownStarted) return;
        d.BeginInvoke(action);
    }

    private static void OnUiNow(Action action)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.HasShutdownStarted) return;
        if (d.CheckAccess()) action(); else d.Invoke(action);
    }
}
