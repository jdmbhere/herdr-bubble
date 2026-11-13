using System.IO;
using System.Text.Json;

namespace HerdrBubble;

internal sealed class BubbleSettings
{
    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public bool MagneticEdgeSnap { get; set; }
    public bool StrictHerdrTitleFilter { get; set; }
    public bool IdleDimEnabled { get; set; } = true;
    public string LaunchCommand { get; set; } = "herdr";
    public string TargetKeyword { get; set; } = "herdr";
}

internal static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "HerdrBubble",
        "settings.json");

    public static BubbleSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
            {
                return new BubbleSettings();
            }

            string json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<BubbleSettings>(json, JsonOptions) ?? new BubbleSettings();
        }
        catch
        {
            return new BubbleSettings();
        }
    }

    public static void Save(BubbleSettings settings)
    {
        try
        {
            string? dir = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // Settings persistence must never crash the floating bubble.
        }
    }
}
