using System.Text.Json;

namespace FileShare.Win;

static class Settings
{
    public static string ReceiveDir { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "FileShare");

    static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FileShare", "config.json");

    public static void Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
                ReceiveDir = JsonDocument.Parse(File.ReadAllText(ConfigPath)).RootElement
                    .GetProperty("receiveDir").GetString() ?? ReceiveDir;
        }
        catch { /* default */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new { receiveDir = ReceiveDir }));
        }
        catch { /* best effort */ }
    }
}
