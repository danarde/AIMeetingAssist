using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;

namespace MeetingAssist.Core.Configuration;

/// <summary>What <see cref="SecretStore.Load"/> did with an older plaintext secrets file.</summary>
public enum SecretMigration
{
    /// <summary>No plaintext file was present. The normal case after the first run.</summary>
    None,

    /// <summary>Plaintext was imported, verified, and the original deleted.</summary>
    Completed,

    /// <summary>
    /// Plaintext was imported but could not be verified or deleted, so it is still on disk.
    /// The keys are safe; the user needs to know the plaintext copy survived.
    /// </summary>
    Incomplete
}

/// <summary>
/// Keys at rest, encrypted with DPAPI under the current user (spec FR-10.1).
///
/// Three properties are load-bearing:
///
/// 1. <b>Encryption is tied to the Windows account.</b> Copying <c>secrets.dat</c> to another
///    machine or another user profile yields nothing readable, which is the point.
/// 2. <b>Migration never risks the keys.</b> An existing plaintext file is imported, written
///    encrypted, and read back and compared before the plaintext is deleted. Deleting first
///    would trade a privacy problem for a data-loss one.
/// 3. <b>Failure is visible, not silent.</b> If the store cannot be read the app still starts;
///    it just has no keys, and says so.
///
/// Values never reach the log. Only names and outcomes do.
/// </summary>
public sealed class SecretStore
{
    public const string GroqApiKey = "groqApiKey";
    public const string GeminiApiKey = "geminiApiKey";
    public const string GeminiModel = "geminiModel";
    public const string SttModel = "sttModel";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly Dictionary<string, string> _values;
    private readonly string _path;

    private SecretStore(string path, Dictionary<string, string> values, SecretMigration migration)
    {
        _path = path;
        _values = values;
        Migration = migration;
    }

    /// <summary>The encrypted blob. Outside the working tree by construction.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MeetingAssist", "secrets.dat");

    public SecretMigration Migration { get; private set; }

    public string? Get(string name) =>
        _values.TryGetValue(name, out var value) && value.Length > 0 ? value : null;

    /// <summary>Sets a value, or removes it when given null or blank.</summary>
    public void Set(string name, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) _values.Remove(name);
        else _values[name] = value.Trim();
    }

    /// <summary>
    /// Loads the encrypted store, importing an older plaintext secrets file if one is present.
    /// Never throws — an unreadable store leaves the app keyless rather than dead.
    /// </summary>
    public static SecretStore Load(string? path = null, string? plaintextPath = null)
    {
        var target = path ?? DefaultPath;
        var legacy = plaintextPath ?? AppConfig.SecretsPath;

        var values = ReadEncrypted(target);
        var store = new SecretStore(target, values, SecretMigration.None);

        // Migrate only when there is nothing encrypted yet. If both exist, the encrypted store
        // is the live one and the plaintext file is a leftover the user must resolve — silently
        // overwriting good keys with stale ones would be worse than leaving it alone.
        if (values.Count == 0 && File.Exists(legacy)) store.MigrateFrom(legacy);

        return store;
    }

    private void MigrateFrom(string plaintextPath)
    {
        try
        {
            var imported = JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(plaintextPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (imported is null || imported.Count == 0)
            {
                Log.Information("Plaintext secrets file at {Path} held nothing to import", plaintextPath);
                return;
            }

            foreach (var (name, value) in imported) Set(name, value);

            if (!Save())
            {
                Migration = SecretMigration.Incomplete;
                Log.Error("Could not write the encrypted secret store; {Path} was left in place", plaintextPath);
                return;
            }

            // Read back from disk before deleting anything. A write that appeared to succeed but
            // cannot be decrypted would otherwise take the only copy of the keys with it.
            var verified = ReadEncrypted(_path);
            var intact = _values.All(pair =>
                verified.TryGetValue(pair.Key, out var value) && value == pair.Value);

            if (!intact)
            {
                Migration = SecretMigration.Incomplete;
                Log.Error("Encrypted secrets did not read back intact; {Path} was left in place", plaintextPath);
                return;
            }

            File.Delete(plaintextPath);
            Migration = SecretMigration.Completed;
            Log.Information(
                "Migrated {Count} secret(s) to the encrypted store and deleted {Path}",
                imported.Count, plaintextPath);
        }
        catch (Exception ex)
        {
            Migration = SecretMigration.Incomplete;
            Log.Error(ex, "Could not migrate {Path} to the encrypted store", plaintextPath);
        }
    }

    /// <summary>Writes the store. Returns false on failure rather than throwing.</summary>
    public bool Save()
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(_path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            var plain = JsonSerializer.SerializeToUtf8Bytes(_values, Json);

            try
            {
                var encrypted = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_path, encrypted);
            }
            finally
            {
                // The serialized form holds the keys in the clear; do not leave it for the GC.
                Array.Clear(plain);
            }

            Log.Information("Wrote {Count} secret(s) to the encrypted store", _values.Count);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not write the encrypted secret store at {Path}", _path);
            return false;
        }
    }

    private static Dictionary<string, string> ReadEncrypted(string path)
    {
        var empty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(path)) return empty;

            var encrypted = File.ReadAllBytes(path);
            if (encrypted.Length == 0) return empty;

            var plain = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);

            try
            {
                var values = JsonSerializer.Deserialize<Dictionary<string, string>>(plain);
                return values is null
                    ? empty
                    : new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase);
            }
            finally
            {
                Array.Clear(plain);
            }
        }
        catch (CryptographicException ex)
        {
            // The usual cause is the file having been created under a different Windows account.
            Log.Error(ex, "Could not decrypt {Path}. It belongs to a different Windows user "
                          + "account, or it is corrupt; re-enter the keys to replace it", path);
            return empty;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Could not read the encrypted secret store at {Path}", path);
            return empty;
        }
    }

    /// <summary>
    /// A safe rendering for the UI and for logs: enough to tell two keys apart, never enough to
    /// use one. Short values are hidden entirely rather than half-revealed.
    /// </summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "(not set)";
        return value.Length <= 12 ? "••••••••" : $"{value[..4]}…{value[^4..]} ({value.Length} chars)";
    }
}
