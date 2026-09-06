using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

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
        // One syscall for the whole keyboard instead of 247 of them. This is
        // polled every frame while the panel waits for a rebind.
        //
        // GetKeyboardState reports the state as of the calling thread's last
        // processed message, so it is only correct on the game's UI thread -
        // which is where the panel poll already runs.
        Span<byte> state = stackalloc byte[256];
        if (!GetKeyboardState(ref MemoryMarshal.GetReference(state)))
            return 0;

        for (int key = 0x07; key <= 0xFE; key++)
        {
            if (key is 0x10 or 0x11 or 0x12)
                continue; // bare Shift/Ctrl/Alt are modifiers, not bindings

            if ((state[key] & 0x80) != 0)
                return key;
        }
        return 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetKeyboardState(ref byte state);
}
