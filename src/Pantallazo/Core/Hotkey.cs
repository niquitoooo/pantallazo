using System.Text.Json.Serialization;

namespace Pantallazo.Core;

/// <summary>Modificadores con los mismos valores que MOD_* de RegisterHotKey.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x1,
    Control = 0x2,
    Shift = 0x4,
    Windows = 0x8,
}

public enum HotkeyAction
{
    FullScreen,
    Region,
    Record,
}

/// <summary>Combinación de teclas: modificadores + una tecla virtual (VK_*) que no sea modificador.</summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, int Key)
{
    public const int VkPrintScreen = 0x2C;

    public static HotkeyGesture None => default;

    [JsonIgnore]
    public bool IsEmpty => Key == 0;

    /// <summary>Tecla presente y que no sea un modificador suelto.</summary>
    [JsonIgnore]
    public bool IsValid => Key != 0 && !KeyNames.IsModifierKey(Key);

    /// <summary>Impr Pant sola o con modificadores: lo que se suelta al pausar Impr Pant.</summary>
    [JsonIgnore]
    public bool UsesPrintScreen => Key == VkPrintScreen;

    public override string ToString() => KeyNames.Format(this);
}

public sealed record HotkeyBinding(HotkeyAction Action, HotkeyGesture Gesture);

public enum HotkeyStatus
{
    NotConfigured,
    Registered,
    Failed,
    Paused,
}

public static class HotkeyDefaults
{
    public static IReadOnlyList<HotkeyBinding> Create() =>
    [
        new(HotkeyAction.FullScreen, new HotkeyGesture(HotkeyModifiers.None, HotkeyGesture.VkPrintScreen)),
        new(HotkeyAction.Region, new HotkeyGesture(HotkeyModifiers.Control, HotkeyGesture.VkPrintScreen)),
        new(HotkeyAction.Record, new HotkeyGesture(HotkeyModifiers.Alt, HotkeyGesture.VkPrintScreen)),
    ];
}

public static class HotkeyConflicts
{
    /// <summary>
    /// Devuelve, para cada combinación válida repetida, las asignaciones que la comparten.
    /// La misma combinación repetida en la misma acción también es conflicto (la segunda no se puede registrar).
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<HotkeyBinding>> Find(IEnumerable<HotkeyBinding> bindings) =>
        bindings.Where(b => b.Gesture.IsValid)
            .GroupBy(b => b.Gesture)
            .Where(g => g.Count() > 1)
            .Select(g => (IReadOnlyList<HotkeyBinding>)g.ToList())
            .ToList();

    public static bool IsInConflict(IEnumerable<HotkeyBinding> bindings, HotkeyBinding binding) =>
        binding.Gesture.IsValid && bindings.Count(b => b.Gesture == binding.Gesture) > 1;
}
