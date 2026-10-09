using Serilog;

namespace MeetingAssist.Core.Profiles;

/// <summary>
/// The named context profiles on disk (spec FR-9.3), one JSON file each under
/// <c>%APPDATA%\MeetingAssist\profiles</c>.
///
/// Separate files rather than one document so a profile can be copied to another machine, or
/// hand-edited, or deleted, without risking the others — and so a single corrupt file costs one
/// profile instead of all of them.
/// </summary>
public sealed class ProfileLibrary(string? folder = null)
{
    public const string DefaultName = "default";

    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetingAssist", "profiles");

    public string Folder { get; } = folder ?? DefaultFolder;

    /// <summary>
    /// Profile names, alphabetical. Empty only when the folder cannot be read — the first call
    /// to <see cref="Load"/> seeds a starter profile, so an empty library is not a normal state.
    /// </summary>
    public IReadOnlyList<string> Names()
    {
        try
        {
            if (!Directory.Exists(Folder)) return [];

            return [.. Directory.EnumerateFiles(Folder, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not list profiles in {Folder}", Folder);
            return [];
        }
    }

    public string PathFor(string name) => Path.Combine(Folder, $"{Sanitize(name)}.json");

    public bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>
    /// Loads a profile, falling back to a freshly written sample rather than failing. A meeting
    /// starting with a sample profile is recoverable; one that will not start is not.
    /// </summary>
    public ContextProfile Load(string? name = null)
    {
        var wanted = string.IsNullOrWhiteSpace(name) ? DefaultName : name;

        try
        {
            if (Exists(wanted))
            {
                var profile = ContextProfile.Load(PathFor(wanted));

                // The file name is the identity; a mismatched Name inside would make the picker
                // disagree with what it loads.
                profile.Name = wanted;
                return profile;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not read profile {Name}; falling back to a sample", wanted);
        }

        var sample = ContextProfile.Sample();
        sample.Name = wanted;
        Save(sample);
        Log.Information("Wrote a starter profile to {Path}", PathFor(wanted));
        return sample;
    }

    /// <summary>
    /// Adopts a single pre-library <c>profile.json</c> as the first named profile.
    ///
    /// Before profiles were named there was one file, and it is the one the user actually wrote
    /// and tuned. Losing it to a folder reorganisation and silently replacing it with a sample
    /// would be the worst kind of upgrade. Only runs when the library is empty, so it can never
    /// overwrite a real profile, and the original file is left where it is.
    /// </summary>
    public bool ImportLegacy(string path, string name = DefaultName)
    {
        if (Names().Count > 0 || !File.Exists(path)) return false;

        try
        {
            var profile = ContextProfile.Load(path);
            profile.Name = name;

            if (!Save(profile)) return false;

            Log.Information("Adopted {Path} as the {Name} profile", path, name);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Could not adopt {Path} as a profile", path);
            return false;
        }
    }

    public bool Save(ContextProfile profile)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            profile.Save(PathFor(profile.Name));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not save profile {Name}", profile.Name);
            return false;
        }
    }

    /// <summary>Copies a profile under a new name and returns it, or null if that name is taken.</summary>
    public ContextProfile? Duplicate(string source, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || Exists(newName)) return null;

        var copy = Load(source);
        copy.Name = Sanitize(newName);
        return Save(copy) ? copy : null;
    }

    /// <summary>
    /// Deletes a profile. The last one is never deleted: a library with no profiles would leave
    /// the picker empty and the next session with nothing to load.
    /// </summary>
    public bool Delete(string name)
    {
        if (Names().Count <= 1) return false;

        try
        {
            File.Delete(PathFor(name));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not delete profile {Name}", name);
            return false;
        }
    }

    private static readonly HashSet<char> InvalidFileNameChars = [.. Path.GetInvalidFileNameChars()];

    /// <summary>
    /// Reserved DOS device names still cannot be file names on Windows, decades later — the
    /// full set, not just the first couple, since a name that merely looks handled (COM3,
    /// LPT4) is worse than one that plainly is not.
    /// </summary>
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    /// <summary>
    /// Profile names become file names, so anything that cannot be one is replaced. The result
    /// is still recognisable to the user, which matters more here than reversibility.
    /// </summary>
    public static string Sanitize(string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return DefaultName;

        var cleaned = new string([.. trimmed.Select(c => InvalidFileNameChars.Contains(c) ? '-' : c)]);

        if (ReservedNames.Contains(cleaned)) cleaned = $"{cleaned}-profile";

        return cleaned.Length > 64 ? cleaned[..64] : cleaned;
    }
}
