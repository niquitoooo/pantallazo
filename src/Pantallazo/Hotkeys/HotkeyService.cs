using System.ComponentModel;
using System.Windows.Interop;
using Pantallazo.Core;
using Pantallazo.Logging;
using Pantallazo.Native;

namespace Pantallazo.Hotkeys;

/// <summary>
/// Atajos globales con RegisterHotKey sobre una ventana de mensajes oculta (como ShareX: sin hook de teclado).
/// MOD_NOREPEAT evita repeticiones al mantener apretado; además se ignora el mismo atajo durante 500 ms.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private const int RepeatLimitMs = 500; // ShareX: HotkeyRepeatLimit = 500

    private readonly HwndSource _host;
    private readonly Dictionary<int, HotkeyBinding> _registered = [];
    private readonly Dictionary<HotkeyBinding, HotkeyStatus> _status = [];
    private readonly Dictionary<HotkeyGesture, long> _lastFired = [];
    private IReadOnlyList<HotkeyBinding> _bindings = [];
    private int _nextId = 0x5000;
    private bool _suspended;

    public event EventHandler<HotkeyAction>? Pressed;
    public event EventHandler? StatusChanged;

    public HotkeyService()
    {
        var p = new HwndSourceParameters("Pantallazo - atajos")
        {
            ParentWindow = new nint(-3), // HWND_MESSAGE: ventana solo de mensajes, invisible
            WindowStyle = 0,
        };
        _host = new HwndSource(p);
        _host.AddHook(WndProc);
    }

    public IReadOnlyList<HotkeyBinding> Bindings => _bindings;

    public HotkeyStatus GetStatus(HotkeyBinding binding) =>
        _status.TryGetValue(binding, out var s) ? s : HotkeyStatus.NotConfigured;

    /// <summary>Asignaciones que no se pudieron registrar (ocupadas por otra app o repetidas).</summary>
    public IEnumerable<HotkeyBinding> Failed => _status.Where(kv => kv.Value == HotkeyStatus.Failed).Select(kv => kv.Key);

    /// <summary>Error de Windows del último intento por asignación (para explicar el fallo).</summary>
    public Dictionary<HotkeyBinding, string> FailureReasons { get; } = [];

    public void Apply(IReadOnlyList<HotkeyBinding> bindings)
    {
        _bindings = bindings;
        if (!_suspended) RegisterAll();
        else
        {
            _status.Clear();
            StatusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Mientras se captura una combinación nueva, los atajos globales se sueltan (como ShareX).</summary>
    public void Suspend()
    {
        if (_suspended) return;
        _suspended = true;
        UnregisterAll();
    }

    public void Resume()
    {
        if (!_suspended) return;
        _suspended = false;
        RegisterAll();
    }

    private void RegisterAll()
    {
        UnregisterAll();
        _status.Clear();
        FailureReasons.Clear();
        var seen = new HashSet<HotkeyGesture>();
        foreach (var b in _bindings)
        {
            if (!b.Gesture.IsValid)
            {
                _status[b] = HotkeyStatus.NotConfigured;
                continue;
            }
            if (!seen.Add(b.Gesture))
            {
                _status[b] = HotkeyStatus.Failed;
                FailureReasons[b] = "La misma combinación ya está asignada a otra acción de Pantallazo.";
                continue;
            }
            var id = _nextId++;
            var mods = (uint)b.Gesture.Modifiers | NativeMethods.MOD_NOREPEAT;
            if (NativeMethods.RegisterHotKey(_host.Handle, id, mods, (uint)b.Gesture.Key))
            {
                _registered[id] = b;
                _status[b] = HotkeyStatus.Registered;
            }
            else
            {
                var err = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                _status[b] = HotkeyStatus.Failed;
                // 1409 = ERROR_HOTKEY_ALREADY_REGISTERED
                FailureReasons[b] = err == 1409
                    ? "Otra aplicación ya usa esta combinación."
                    : $"Windows no permitió registrarla ({new Win32Exception(err).Message}).";
                Log.Warn($"No se pudo registrar el atajo {b.Gesture} ({b.Action}): error {err}");
            }
        }
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UnregisterAll()
    {
        foreach (var id in _registered.Keys) NativeMethods.UnregisterHotKey(_host.Handle, id);
        _registered.Clear();
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _registered.TryGetValue((int)wParam, out var binding))
        {
            handled = true;
            var now = Environment.TickCount64;
            if (_lastFired.TryGetValue(binding.Gesture, out var last) && now - last < RepeatLimitMs) return 0;
            _lastFired[binding.Gesture] = now;
            Pressed?.Invoke(this, binding.Action);
        }
        return 0;
    }

    public void Dispose()
    {
        UnregisterAll();
        _host.Dispose();
    }
}
