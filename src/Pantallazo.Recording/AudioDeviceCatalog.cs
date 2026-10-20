using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Pantallazo.Recording;

/// <summary>Micrófonos activos y salida predeterminada (WASAPI), con aviso de cambios.</summary>
public static class AudioDeviceCatalog
{
    private static readonly object Gate = new();
    private static MMDeviceEnumerator? _notifyEnumerator;
    private static NotificationClient? _client;
    private static EventHandler? _devicesChanged;

    public static IReadOnlyList<AudioDeviceInfo> GetInputDevices()
    {
        var list = new List<AudioDeviceInfo>();
        try
        {
            using var en = new MMDeviceEnumerator();
            string? defaultId = null;
            if (en.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
                using (var def = en.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console)) defaultId = def.ID;
            foreach (var d in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (d)
                    list.Add(new AudioDeviceInfo(d.ID, SafeName(d), AudioDeviceKind.Input,
                        string.Equals(d.ID, defaultId, StringComparison.OrdinalIgnoreCase)));
            }
        }
        catch (Exception ex)
        {
            RecordingLog.Write($"No se pudieron listar los micrófonos: {ex.Message}");
        }
        return list;
    }

    public static AudioDeviceInfo? GetDefaultOutput()
    {
        try
        {
            using var en = new MMDeviceEnumerator();
            if (!en.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            using var d = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return new AudioDeviceInfo(d.ID, SafeName(d), AudioDeviceKind.Output, true);
        }
        catch (Exception ex)
        {
            RecordingLog.Write($"No se pudo leer la salida predeterminada: {ex.Message}");
            return null;
        }
    }

    /// <summary>Alta, baja, cambio de estado o de predeterminado de un dispositivo. Se dispara en un hilo del pool.</summary>
    public static event EventHandler? DevicesChanged
    {
        add
        {
            lock (Gate)
            {
                _devicesChanged += value;
                EnsureRegistered();
            }
        }
        remove
        {
            lock (Gate)
            {
                _devicesChanged -= value;
                if (_devicesChanged == null) Unregister();
            }
        }
    }

    internal static string SafeName(MMDevice d)
    {
        try { return d.FriendlyName; } catch { return "Dispositivo de audio"; }
    }

    private static void EnsureRegistered()
    {
        if (_client != null) return;
        try
        {
            _notifyEnumerator = new MMDeviceEnumerator();
            _client = new NotificationClient(Raise);
            _notifyEnumerator.RegisterEndpointNotificationCallback(_client);
        }
        catch (Exception ex)
        {
            RecordingLog.Write($"No se pudo registrar el aviso de dispositivos de audio: {ex.Message}");
            _client = null;
            _notifyEnumerator?.Dispose();
            _notifyEnumerator = null;
        }
    }

    private static void Unregister()
    {
        if (_client == null || _notifyEnumerator == null) return;
        try { _notifyEnumerator.UnregisterEndpointNotificationCallback(_client); } catch { }
        _notifyEnumerator.Dispose();
        _notifyEnumerator = null;
        _client = null;
    }

    private static void Raise()
    {
        // Nunca llamar a la API de audio dentro del callback: se despacha al pool.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            EventHandler? h;
            lock (Gate) h = _devicesChanged;
            try { h?.Invoke(null, EventArgs.Empty); } catch (Exception ex) { RecordingLog.Write($"DevicesChanged: {ex.Message}"); }
        });
    }

    /// <summary>Cliente de notificaciones reutilizable (también lo usa el grabador).</summary>
    internal sealed class NotificationClient(Action onChange, Action<DataFlow, Role, string>? onDefaultChanged = null,
        Action<string, DeviceState>? onStateChanged = null) : IMMNotificationClient
    {
        public void OnDeviceStateChanged(string deviceId, DeviceState newState)
        {
            onStateChanged?.Invoke(deviceId, newState);
            onChange();
        }
        public void OnDeviceAdded(string pwstrDeviceId) => onChange();
        public void OnDeviceRemoved(string deviceId) { onStateChanged?.Invoke(deviceId, DeviceState.NotPresent); onChange(); }
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            onDefaultChanged?.Invoke(flow, role, defaultDeviceId);
            onChange();
        }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }
}
