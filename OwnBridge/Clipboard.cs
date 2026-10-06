using System.Runtime.InteropServices;

namespace OwnBridge;

// Clipboard text (write) and images (read) through the Win32 API. The extension runs outside Visual Studio's UI thread,
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

    private const uint Dib = 8;

    // A screenshot (Win+Shift+S, Print Screen, "Copy image") as PNG bytes, or null when the clipboard has no image.
    // Uses the "PNG" clipboard format when a program put one there, otherwise converts the bitmap (CF_DIB).
    public static byte[]? TryGetImagePng()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (!OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(50);
                continue;
            }
            try
            {
                var png = RegisterClipboardFormatW("PNG");
                if (png != 0 && IsClipboardFormatAvailable(png) && ReadGlobal(GetClipboardData(png)) is { Length: > 8 } bytes)
                    return bytes;
                if (IsClipboardFormatAvailable(Dib) && ReadGlobal(GetClipboardData(Dib)) is { } dib)
                    return PngWriter.FromDib(dib);
                return null;
            }
            finally
            {
                CloseClipboard();
            }
        }
        return null;
    }

    private static byte[]? ReadGlobal(IntPtr memory)
    {
        if (memory == IntPtr.Zero) return null;
        var size = (long)GlobalSize(memory);
        if (size <= 0 || size > 200_000_000) return null;
        var source = GlobalLock(memory);
        if (source == IntPtr.Zero) return null;
        try
        {
            var bytes = new byte[size];
            Marshal.Copy(source, bytes, 0, (int)size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormatW(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
}
