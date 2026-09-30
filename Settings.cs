using System.Text.Json;
using System.Text.Json.Serialization;

namespace GpuShot;

public enum PngCompression { Fast = 0, Balanced = 1, Smallest = 2 }

public sealed class Hotkey
{
    public Keys Key { get; set; }
    public Keys Modifiers { get; set; }
    public bool IsEmpty => Key == Keys.None;
    public override string ToString()
    {
        if (IsEmpty) return "(none)";
        var parts = new List<string>();
        if (Modifiers.HasFlag(Keys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(Keys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(Keys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(Keys.LWin)) parts.Add("Win");
        parts.Add(Key switch { Keys.Snapshot => "PrintScreen", Keys.Next => "PageDown", Keys.Prior => "PageUp", _ => Key.ToString() });
        return string.Join(" + ", parts);
    }
}

public sealed class Settings
{
    // Hotkeys
    public Hotkey RegionHotkey { get; set; } = new() { Key = Keys.Snapshot };
    public Hotkey FullscreenHotkey { get; set; } = new() { Key = Keys.Snapshot, Modifiers = Keys.Control };
    public Hotkey MonitorHotkey { get; set; } = new() { Key = Keys.Snapshot, Modifiers = Keys.Shift };
    public Hotkey WindowHotkey { get; set; } = new() { Key = Keys.Snapshot, Modifiers = Keys.Alt };

    // Output
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "GpuShot");
    public string FileNamePattern { get; set; } = "GpuShot_{yyyy-MM-dd_HH-mm-ss-fff}";
    public bool SaveToFile { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    public bool ShowNotification { get; set; } = true;
    public bool CaptureCursor { get; set; } = false;
    public PngCompression PngCompression { get; set; } = PngCompression.Balanced;

    // Behaviour
    public int CaptureDelayMs { get; set; } = 0;

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GpuShot", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonOpts) ?? new Settings();
        }
        catch { /* fall back to defaults on a corrupt file */ }
        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
}
