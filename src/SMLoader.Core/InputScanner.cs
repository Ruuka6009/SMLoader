using System.Runtime.InteropServices;

namespace SMLoader.Core;

/// <summary>Finds which key is currently held, for "press any key" rebinding.</summary>
internal static partial class InputScanner
{
    /// <summary>
    /// First held key, or 0. Mouse buttons (0x01-0x06) are skipped: the click
    /// that started the rebind would otherwise be captured as the new binding.
    /// </summary>
    public static int FirstPressedKey()
    {
        for (int key = 0x07; key <= 0xFE; key++)
        {
            if (key is 0x10 or 0x11 or 0x12)
                continue; // bare Shift/Ctrl/Alt are modifiers, not bindings

            if ((GetAsyncKeyState(key) & 0x8000) != 0)
                return key;
        }
        return 0;
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);
}
