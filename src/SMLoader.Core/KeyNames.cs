namespace SMLoader.Core;

/// <summary>
/// Friendly names for Windows virtual-key codes, so settings can show "F2"
/// rather than 113.
/// </summary>
internal static class KeyNames
{
    private static readonly Dictionary<int, string> Named = new()
    {
        [0x08] = "Backspace", [0x09] = "Tab", [0x0D] = "Enter", [0x10] = "Shift",
        [0x11] = "Ctrl", [0x12] = "Alt", [0x14] = "Caps Lock", [0x1B] = "Esc",
        [0x20] = "Space", [0x21] = "Page Up", [0x22] = "Page Down", [0x23] = "End",
        [0x24] = "Home", [0x25] = "Left", [0x26] = "Up", [0x27] = "Right",
        [0x28] = "Down", [0x2D] = "Insert", [0x2E] = "Delete",
        [0xBA] = ";", [0xBB] = "=", [0xBC] = ",", [0xBD] = "-", [0xBE] = ".",
        [0xBF] = "/", [0xC0] = "`", [0xDB] = "[", [0xDC] = "\\", [0xDD] = "]",
        [0xDE] = "'",
    };

    public static string Describe(int virtualKey)
    {
        if (Named.TryGetValue(virtualKey, out string? name))
            return name;

        if (virtualKey >= 0x30 && virtualKey <= 0x39)
            return ((char)virtualKey).ToString();

        if (virtualKey >= 0x41 && virtualKey <= 0x5A)
            return ((char)virtualKey).ToString();

        if (virtualKey >= 0x70 && virtualKey <= 0x87)
            return "F" + (virtualKey - 0x6F);

        if (virtualKey >= 0x60 && virtualKey <= 0x69)
            return "Numpad " + (virtualKey - 0x60);

        return $"Key {virtualKey}";
    }
}
