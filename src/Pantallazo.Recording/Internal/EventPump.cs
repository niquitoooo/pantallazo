using System.Collections.Concurrent;

namespace Pantallazo.Recording;

/// <summary>
/// Dispara los eventos públicos en orden, en un hilo propio, para que un manejador lento (o uno que hace
/// Dispatcher.Invoke mientras la UI espera al grabador) nunca bloquee la captura ni el encoder.
/// </summary>
internal sealed class EventPump : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    private readonly Thread _thread;

    public EventPump(string name)
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = name };
        _thread.Start();
    }

    public void Post(Action action)
    {
        try { _queue.Add(action); } catch (InvalidOperationException) { }
    }

    private void Loop()
    {
        foreach (var a in _queue.GetConsumingEnumerable())
        {
            try { a(); }
            catch (Exception ex) { RecordingLog.Write($"Un manejador de evento falló: {ex.Message}"); }
        }
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread) _thread.Join(2000);
    }
}
