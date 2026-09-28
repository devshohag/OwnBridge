using System.Text.Json;

namespace OwnBridge;

// Small per-user settings file: %LOCALAPPDATA%\OwnBridge\settings.json
internal static class Settings
{
    // The free Gemini API tier does not include Pro models, so a Flash model is the default.
    public const string DefaultGeminiModel = "gemini-2.5-flash";

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OwnBridge", "settings.json");

    public static string GeminiModel
    {
        get => Read().TryGetValue("geminiModel", out var value) && !string.IsNullOrWhiteSpace(value) ? value : DefaultGeminiModel;
        set
        {
            var all = Read();
            all["geminiModel"] = value.Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(all));
        }
    }

    private static Dictionary<string, string> Read()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }
}
