namespace Pantallazo.Recording;

/// <summary>
/// Medidores sin grabar (pantalla Grabación): abre las fuentes y publica picos ~cada 50 ms.
/// <see cref="LevelsChanged"/> trae una entrada por fuente pedida (en el orden pedido, sin repetidos);
/// las que fallaron quedan en 0. Los eventos llegan en un hilo del pool.
/// </summary>
public sealed class AudioLevelMonitor : IDisposable
{
    private readonly List<string> _ids = [];
    private readonly Dictionary<string, AudioCaptureSource> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _pendingFailures = [];
    private readonly object _lock = new();
    private readonly Timer _timer;
    private bool _disposed;

    public event EventHandler<IReadOnlyList<AudioLevel>>? LevelsChanged;
    /// <summary>Id de la fuente que no se pudo abrir o se desconectó.</summary>
    public event EventHandler<string>? SourceFailed;

    public AudioLevelMonitor(bool systemAudio, IEnumerable<string> microphoneIds)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (systemAudio && seen.Add(AudioSourceIds.System)) _ids.Add(AudioSourceIds.System);
        foreach (var id in microphoneIds ?? [])
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id)) _ids.Add(id);

        // WASAPI se abre en un hilo MTA del pool (el que llama puede ser el hilo STA de la UI).
        Task.Run(OpenAll).GetAwaiter().GetResult();
        _timer = new Timer(_ => Tick(), null, 50, 50);
    }

    private void OpenAll()
    {
        foreach (var id in _ids)
        {
            try
            {
                var src = id == AudioSourceIds.System
                    ? AudioCaptureSource.OpenSystem(buffering: false)
                    : AudioCaptureSource.OpenMicrophone(id, buffering: false);
                src.Failed += (s, _) => { lock (_lock) _pendingFailures.Add(s.Id); };
                src.Start();
                _sources[id] = src;
            }
            catch (Exception ex)
            {
                RecordingLog.Write($"Medidor: no se pudo abrir «{id}»: {ex.Message}");
                // Se avisa en el primer tick, cuando el que creó el monitor ya pudo suscribirse.
                _pendingFailures.Add(id);
            }
        }
    }

    private void Tick()
    {
        List<string>? failures = null;
        IReadOnlyList<AudioLevel> levels;
        lock (_lock)
        {
            if (_disposed) return;
            if (_pendingFailures.Count > 0) { failures = [.. _pendingFailures]; _pendingFailures.Clear(); }
            levels = _ids.Select(id => new AudioLevel(id,
                _sources.TryGetValue(id, out var s) && !s.HasFailed ? Math.Min(1f, s.Timeline.TakePeak()) : 0f)).ToArray();
        }
        if (failures != null)
            foreach (var f in failures)
                try { SourceFailed?.Invoke(this, f); } catch { }
        try { LevelsChanged?.Invoke(this, levels); } catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _timer.Dispose();
        var sources = _sources.Values.ToList();
        _sources.Clear();
        Task.Run(() => { foreach (var s in sources) s.Dispose(); }).Wait(TimeSpan.FromSeconds(5));
    }
}
