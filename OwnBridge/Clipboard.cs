using System.Runtime.InteropServices;

namespace OwnBridge;

// Plain-text clipboard through the Win32 API. The extension runs outside Visual Studio's UI thread,
// and this API (unlike the WPF/WinForms clipboard) does not need an STA thread.
internal static class Clipboard
{
    private const uint UnicodeText = 13;
    private const uint Moveable = 0x0002;

    public static bool TrySetText(string text)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                try
                {
                    EmptyClipboard();
                    var bytes = (text.Length + 1) * 2;
                    var memory = GlobalAlloc(Moveable, (UIntPtr)bytes);
                    if (memory == IntPtr.Zero) return false;
                    var target = GlobalLock(memory);
                    if (target == IntPtr.Zero)
                    {
                        GlobalFree(memory);
                        return false;
                    }
                    try
                    {
                        Marshal.Copy(text.ToCharArray(), 0, target, text.Length);
                        Marshal.WriteInt16(target, text.Length * 2, 0);
                    }
                    finally
                    {
                        GlobalUnlock(memory);
                    }
                    if (SetClipboardData(UnicodeText, memory) == IntPtr.Zero)
                    {
                        GlobalFree(memory);
                        return false;
                    }
                    return true; // The system owns the memory now.
                }
                finally
                {
                    CloseClipboard();
                }
            }
            Thread.Sleep(50); // Another program has the clipboard open; try again shortly.
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
}
