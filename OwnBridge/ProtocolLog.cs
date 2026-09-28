namespace OwnBridge;

// Writes engine traffic to %LOCALAPPDATA%\OwnBridge\logs\<name>.log for troubleshooting.
// The file is recreated when an engine starts and is capped at about 2 MB.
internal sealed class ProtocolLog
{
    private const long MaxBytes = 2_000_000;
    private readonly object gate = new();

    public ProtocolLog(string name)
    {
        FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OwnBridge", "logs", name + ".log");
    }

    public string FilePath { get; }

    public void Reset()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"--- started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ---\n");
        }
        catch (IOException) { }
    }

    public void Write(string direction, string line)
    {
        try
        {
            lock (gate)
            {
                var info = new FileInfo(FilePath);
                if (info.Exists && info.Length > MaxBytes) return;
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff} {direction} {line}\n");
            }
        }
        catch (IOException) { }
    }
}
