using System.IO;
using System.Text.Json;
using Serilog;

namespace MeetingAssist.App.Overlay;

public enum OverlayCorner { TopRight, BottomRight, BottomLeft, TopLeft }

/// <summary>One step in the FR-7.8 cycle: where the overlay sits and how solid it is.</summary>
public sealed record OverlayPreset(OverlayCorner Corner, double Opacity, string Display);

/// <summary>
/// Overlay placement, persisted so a preference chosen once survives a restart (FR-7.8).
///
/// A plain JSON file rather than a settings system: this is four fields, and the settings UI
/// is deliberately out of scope for the first milestone. It lives beside the secrets file so
/// nothing user-specific ends up in the working tree.
/// </summary>
public sealed class OverlaySettings
{
    /// <summary>
    /// Corners first, opacity second, so one press moves the overlay out of the way and a
    /// second dims it — the two things a user actually wants mid-meeting.
    /// </summary>
    public static readonly OverlayPreset[] Presets =
    [
        new(OverlayCorner.TopRight,    0.92, "top right, solid"),
        new(OverlayCorner.BottomRight, 0.92, "bottom right, solid"),
        new(OverlayCorner.BottomLeft,  0.92, "bottom left, solid"),
        new(OverlayCorner.TopLeft,     0.92, "top left, solid"),
        new(OverlayCorner.TopRight,    0.65, "top right, faint"),
        new(OverlayCorner.BottomRight, 0.65, "bottom right, faint")
    ];

    public int PresetIndex { get; set; }
    public double FontSize { get; set; } = 19;
    public double Width { get; set; } = 460;
    public double MaxHeight { get; set; } = 520;

    public OverlayPreset Current => Presets[Math.Clamp(PresetIndex, 0, Presets.Length - 1)];

    public OverlayPreset Advance()
    {
        PresetIndex = (PresetIndex + 1) % Presets.Length;
        return Current;
    }

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetingAssist", "overlay.json");

    public static OverlaySettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<OverlaySettings>(File.ReadAllText(Path));
                if (loaded is not null) return loaded;
            }
        }
        catch (Exception ex)
        {
            // A corrupt preferences file must never stop the app starting mid-meeting.
            Log.Warning(ex, "Could not read overlay settings; using defaults");
        }
        return new OverlaySettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not save overlay settings");
        }
    }
}
