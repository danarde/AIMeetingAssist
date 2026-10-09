using System.IO;
using System.Text.Json;
using Serilog;

namespace MeetingAssist.App.Interop;

/// <summary>
/// Hotkey bindings, configurable without rebuilding (spec §4.8: every action is configurable,
/// including Quit, added after the "all six" the section was originally written against).
///
/// Configurability is not a nicety here. `Ctrl+Alt+Space`, the spec's default for Ask, is
/// already claimed on the author's own machine — collisions are the normal case, not the edge
/// case, and a user cannot be told to recompile to get their Ask key back.
/// </summary>
public sealed class HotkeySettings
{
    /// <summary>
    /// Ctrl+Alt throughout: these are global, so a plain Ctrl+letter would take select-all,
    /// refresh, print and the like away from every other app while this one runs.
    ///
    /// The keys pressed again and again during a call — Ask, the mock's next line, pause — sit
    /// on S, D and F, next to the modifiers, so one hand presses them without looking. Keys
    /// that type a character with AltGr (Ctrl+Alt) on the user's layouts are avoided: E and
    /// 1–6 on Spanish, and A, E, I, O, U and 4 on UK, which is why the overlay moved off A.
    /// </summary>
    public Dictionary<string, string> Bindings { get; set; } = new()
    {
        [nameof(HotkeyAction.AskNarrow)] = "Ctrl+Alt+S",
        [nameof(HotkeyAction.AskWide)] = "Ctrl+Alt+Shift+S",
        [nameof(HotkeyAction.ToggleOverlay)] = "Ctrl+Alt+W",
        [nameof(HotkeyAction.PanicHide)] = "Ctrl+Alt+X",
        [nameof(HotkeyAction.CyclePreset)] = "Ctrl+Alt+P",
        [nameof(HotkeyAction.ToggleSession)] = "Ctrl+Alt+R",
        [nameof(HotkeyAction.TogglePause)] = "Ctrl+Alt+F",
        [nameof(HotkeyAction.MockNext)] = "Ctrl+Alt+D",
        [nameof(HotkeyAction.Quit)] = "Ctrl+Alt+Q"
    };

    /// <summary>
    /// The defaults before version 2. A saved binding still equal to one of these was never
    /// chosen by the user, so it moves to the new default; one they changed stays as it is.
    /// </summary>
    private static readonly Dictionary<string, string> Version1Defaults = new()
    {
        [nameof(HotkeyAction.AskNarrow)] = "Ctrl+Alt+G",
        [nameof(HotkeyAction.AskWide)] = "Ctrl+Alt+Shift+G",
        [nameof(HotkeyAction.ToggleOverlay)] = "Ctrl+Alt+A",
        [nameof(HotkeyAction.MockNext)] = "Ctrl+Alt+N"
    };

    private const int CurrentVersion = 2;

    /// <summary>Absent (0) in files written before the defaults changed.</summary>
    public int Version { get; set; }

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetingAssist", "hotkeys.json");

    public static HotkeySettings Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                var loaded = JsonSerializer.Deserialize<HotkeySettings>(File.ReadAllText(Path));
                if (loaded is { Bindings.Count: > 0 })
                {
                    var merged = Merge(loaded);
                    if (loaded.Version < CurrentVersion)
                    {
                        Log.Information("Hotkey defaults updated to version {Version}", CurrentVersion);
                        merged.Save();
                    }

                    return merged;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not read hotkey settings; using defaults");
        }

        var defaults = new HotkeySettings { Version = CurrentVersion };
        defaults.Save(); // Write it out so the file exists to be edited.
        return defaults;
    }

    /// <summary>
    /// The saved bindings over the current defaults. Merged rather than replaced, so a file
    /// written by an older version cannot leave newer actions unbound; and a binding left on an
    /// old default takes the new one.
    /// </summary>
    public static HotkeySettings Merge(HotkeySettings loaded)
    {
        var merged = new HotkeySettings { Version = CurrentVersion };
        foreach (var (action, combo) in loaded.Bindings)
        {
            var untouched = loaded.Version < CurrentVersion
                            && Version1Defaults.TryGetValue(action, out var old)
                            && string.Equals(old, combo, StringComparison.OrdinalIgnoreCase);
            if (!untouched) merged.Bindings[action] = combo;
        }

        return merged;
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
            Log.Warning(ex, "Could not save hotkey settings");
        }
    }

    /// <summary>Bindings in registration order, skipping any that cannot be parsed.</summary>
    public IEnumerable<HotkeyBinding> Resolve()
    {
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            if (!Bindings.TryGetValue(action.ToString(), out var combo)) continue;

            if (HotkeyCombo.TryParse(combo, out var modifiers, out var vk))
            {
                yield return new HotkeyBinding(action, modifiers, vk, combo);
            }
            else
            {
                Log.Warning("Hotkey {Combo} for {Action} is not a valid combination; leaving it unbound",
                    combo, action);
            }
        }
    }
}

/// <summary>Parses "Ctrl+Alt+Shift+G" into modifier flags and a virtual-key code.</summary>
public static class HotkeyCombo
{
    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["space"] = 0x20, ["enter"] = 0x0D, ["return"] = 0x0D, ["tab"] = 0x09,
        ["escape"] = 0x1B, ["esc"] = 0x1B, ["backspace"] = 0x08, ["insert"] = 0x2D,
        ["delete"] = 0x2E, ["home"] = 0x24, ["end"] = 0x23,
        ["pageup"] = 0x21, ["pagedown"] = 0x22,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28
    };

    public static bool TryParse(string combo, out HotkeyModifiers modifiers, out uint virtualKey)
    {
        modifiers = HotkeyModifiers.None;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;

        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= HotkeyModifiers.Control; continue;
                case "alt": modifiers |= HotkeyModifiers.Alt; continue;
                case "shift": modifiers |= HotkeyModifiers.Shift; continue;
                case "win": modifiers |= HotkeyModifiers.Win; continue;
            }

            // Anything that is not a modifier is the key itself, and there can be only one.
            if (virtualKey != 0) return false;

            if (NamedKeys.TryGetValue(raw, out var named)) virtualKey = named;
            else if (raw.Length == 1 && char.IsLetterOrDigit(raw[0])) virtualKey = char.ToUpperInvariant(raw[0]);
            else if (raw.Length is 2 or 3 && (raw[0] == 'F' || raw[0] == 'f')
                     && int.TryParse(raw[1..], out var f) && f is >= 1 and <= 24)
                virtualKey = (uint)(0x70 + f - 1);
            else return false;
        }

        // A bare key with no modifiers would swallow that key system-wide.
        return virtualKey != 0 && modifiers != HotkeyModifiers.None;
    }
}
