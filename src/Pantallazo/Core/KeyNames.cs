namespace Pantallazo.Core;

/// <summary>Nombres en castellano de las teclas, con el estilo del teclado español ("Impr Pant").</summary>
public static class KeyNames
{
    /// <summary>Nombre de respaldo para teclas que no están en la tabla (la app usa GetKeyNameText).</summary>
    public static Func<int, string?>? SystemKeyName { get; set; }

    private static readonly Dictionary<int, string> Names = new()
    {
        [0x08] = "Retroceso", [0x09] = "Tab", [0x0D] = "Enter", [0x13] = "Pausa", [0x14] = "Bloq Mayús",
        [0x1B] = "Esc", [0x20] = "Espacio", [0x21] = "Re Pág", [0x22] = "Av Pág", [0x23] = "Fin",
        [0x24] = "Inicio", [0x25] = "Izquierda", [0x26] = "Arriba", [0x27] = "Derecha", [0x28] = "Abajo",
        [0x2C] = "Impr Pant", [0x2D] = "Insert", [0x2E] = "Supr", [0x5D] = "Menú", [0x91] = "Bloq Despl",
        [0x90] = "Bloq Num", [0x6A] = "Num *", [0x6B] = "Num +", [0x6D] = "Num -", [0x6E] = "Num .",
        [0x6F] = "Num /",
    };

    public static bool IsModifierKey(int vk) => vk is
        0x10 or 0x11 or 0x12 or // Shift, Ctrl, Alt
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or // izquierdo/derecho
        0x5B or 0x5C; // Windows

    public static string KeyName(int vk)
    {
        if (Names.TryGetValue(vk, out var name)) return name;
        if (vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A) return ((char)vk).ToString();
        if (vk is >= 0x60 and <= 0x69) return $"Num {vk - 0x60}";
        if (vk is >= 0x70 and <= 0x87) return $"F{vk - 0x6F}";
        return SystemKeyName?.Invoke(vk) ?? $"Tecla 0x{vk:X2}";
    }

    public static string Format(HotkeyGesture gesture)
    {
        if (gesture.IsEmpty) return "Sin asignar";
        var parts = new List<string>(5);
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Mayús");
        if (gesture.Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(KeyName(gesture.Key));
        return string.Join(" + ", parts);
    }
}
