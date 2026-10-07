using System.Runtime.InteropServices;

namespace OwnBridge;

// The standard Windows "Open" dialog through comdlg32. It runs on its own STA thread, owned by the
// window that is in front (Visual Studio, since the user just clicked in it).
internal static class FileDialog
{
    public const string PlanFilter = "Plans and issue lists (*.xlsx;*.csv;*.pdf;*.docx;*.md;*.txt)|*.xlsx;*.xlsm;*.csv;*.pdf;*.md;*.markdown;*.txt;*.docx|All files (*.*)|*.*";
    public const string ChatFilter = "Images, documents, code and zip|*.png;*.jpg;*.jpeg;*.gif;*.webp;*.pdf;*.zip;*.xlsx;*.xlsm;*.csv;*.md;*.txt;*.docx;*.json;*.xml;*.log;*.cs;*.cshtml;*.razor;*.js;*.ts;*.css;*.html;*.sql;*.config;*.yml;*.yaml|Images (*.png;*.jpg;*.gif;*.webp)|*.png;*.jpg;*.jpeg;*.gif;*.webp|All files (*.*)|*.*";

    public static async Task<string?> PickFileAsync(string title, string filter, string? initialFolder) =>
        (await PickFilesAsync(title, filter, initialFolder, multiple: false)).FirstOrDefault();

    // Several files at once when multiple is true (Ctrl or Shift + click in the dialog). Empty when cancelled.
    public static Task<IReadOnlyList<string>> PickFilesAsync(string title, string filter, string? initialFolder, bool multiple)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Show(title, filter, initialFolder, multiple)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        })
        { IsBackground = true, Name = "OwnBridge file dialog" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static IReadOnlyList<string> Show(string title, string filter, string? initialFolder, bool multiple)
    {
        const int bufferChars = 32768;
        var buffer = Marshal.AllocHGlobal(bufferChars * 2);
        try
        {
            for (var i = 0; i < bufferChars * 2; i++) Marshal.WriteByte(buffer, i, 0);
            var dialog = new OpenFileName
            {
                lStructSize = Marshal.SizeOf<OpenFileName>(),
                hwndOwner = GetForegroundWindow(),
                // "Name|pattern|Name|pattern" → NUL-separated, ending with two NULs.
                lpstrFilter = filter.Replace('|', '\0') + "\0\0",
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = bufferChars,
                lpstrInitialDir = initialFolder is not null && Directory.Exists(initialFolder) ? initialFolder : null,
                lpstrTitle = title,
                Flags = FileMustExist | PathMustExist | Explorer | NoChangeDir | HideReadOnly | (multiple ? AllowMultiSelect : 0),
            };
            if (!GetOpenFileNameW(ref dialog)) return Array.Empty<string>(); // Cancelled (or failed).

            // One file: "C:\dir\file.ext\0\0". Several: "C:\dir\0a.ext\0b.ext\0\0".
            var parts = new List<string>();
            var offset = 0;
            while (offset < bufferChars * 2)
            {
                var part = Marshal.PtrToStringUni(buffer + offset);
                if (string.IsNullOrEmpty(part)) break;
                parts.Add(part);
                offset += (part.Length + 1) * 2;
            }
            if (parts.Count <= 1) return parts;
            return parts.Skip(1).Select(name => Path.Combine(parts[0], name)).ToList();
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const int HideReadOnly = 0x00000004;
    private const int AllowMultiSelect = 0x00000200;
    private const int NoChangeDir = 0x00000008;
    private const int PathMustExist = 0x00000800;
    private const int FileMustExist = 0x00001000;
    private const int Explorer = 0x00080000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string? lpstrFilter;
        public IntPtr lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public IntPtr lpstrFileTitle;
        public int nMaxFileTitle;
        public string? lpstrInitialDir;
        public string? lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string? lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public IntPtr lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
