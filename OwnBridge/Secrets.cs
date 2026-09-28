using System.Runtime.InteropServices;
using System.Text;

namespace OwnBridge;

// Stores the Gemini API key encrypted with Windows DPAPI for the current Windows user.
internal static class Secrets
{
    private static string KeyFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwnBridge", "gemini.key");

    public static bool HasGeminiKey => File.Exists(KeyFile);

    public static void SaveGeminiKey(string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        File.WriteAllBytes(KeyFile, Protect(Encoding.UTF8.GetBytes(key.Trim())));
    }

    public static string? ReadGeminiKey()
    {
        try
        {
            return File.Exists(KeyFile) ? Encoding.UTF8.GetString(Unprotect(File.ReadAllBytes(KeyFile))) : null;
        }
        catch
        {
            return null; // Encrypted by another Windows user or damaged.
        }
    }

    public static void DeleteGeminiKey()
    {
        if (File.Exists(KeyFile)) File.Delete(KeyFile);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    private const int UiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, IntPtr entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            DataBlob output;
            var ok = protect
                ? CryptProtectData(ref input, "OwnBridge", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new InvalidOperationException($"Windows could not {(protect ? "encrypt" : "decrypt")} the key.");
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }
}
