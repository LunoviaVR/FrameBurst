using System.Text.Json;
using System.Text.Json.Serialization;

namespace FrameBurst;

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
    public string OutputFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "FrameBurst");
    public string FileNamePattern { get; set; } = "FrameBurst_{yyyy-MM-dd_HH-mm-ss-fff}";
    public bool UseDateSubfolder { get; set; } = false;
    public string DateSubfolderPattern { get; set; } = @"{yyyy}\{MM}\{dd}";
    public bool SaveToFile { get; set; } = true;
    public bool CopyToClipboard { get; set; } = true;
    public bool ShowNotification { get; set; } = true;
    public bool CaptureCursor { get; set; } = false;
    public PngCompression PngCompression { get; set; } = PngCompression.Balanced;
    public bool SaveHdr { get; set; } = false;

    // Behaviour
    public int CaptureDelayMs { get; set; } = 0;

    // Updates
    public bool CheckForUpdates { get; set; } = true;
    public DateTime LastUpdateCheckUtc { get; set; }

    [JsonIgnore]
    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FrameBurst", "settings.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    public static Settings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(SettingsPath), JsonOpts) ?? new Settings();
            if (MigrateFromOldName() is { } migrated) return migrated;
        }
        catch { /* fall back to defaults on a corrupt file */ }
        return new Settings();
    }

    /// <summary>
    /// One-time import of settings saved under the program's previous name (GpuShot). The old folder is left
    /// untouched. Old default paths and file names are switched to the new name; custom ones are kept as-is.
    /// </summary>
    private static Settings? MigrateFromOldName()
    {
        const string OldName = "GpuShot";
        string oldPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName, "settings.json");
        if (!File.Exists(oldPath)) return null;
        var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(oldPath), JsonOpts);
        if (s == null) return null;

        var defaults = new Settings();
        string oldDefaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), OldName);
        if (string.Equals(s.OutputFolder, oldDefaultFolder, StringComparison.OrdinalIgnoreCase)) s.OutputFolder = defaults.OutputFolder;
        if (s.FileNamePattern.StartsWith(OldName + "_", StringComparison.Ordinal))
            s.FileNamePattern = "FrameBurst_" + s.FileNamePattern[(OldName.Length + 1)..];
        s.Save();
        return s;
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
    }

    public Settings Clone() => JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(this, JsonOpts), JsonOpts)!;
}
