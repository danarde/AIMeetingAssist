using System.Text.Json;
using MeetingAssist.Core.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace MeetingAssist.Tests;

/// <summary>
/// DPAPI is real encryption against the current Windows account, so these tests exercise the
/// actual round-trip rather than a stub — a store that "encrypts" but cannot decrypt its own
/// output would pass any test that mocked it, and would lose the user's keys.
/// </summary>
public class SecretStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "MeetingAssistTests", Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_folder, "secrets.dat");
    private string PlaintextPath => Path.Combine(_folder, "secrets.local.json");

    private const string FakeGroqKey = "gsk_TESTONLYnotarealkey0123456789abcdef";
    private const string FakeGeminiKey = "AIzaTESTONLYnotarealkey0123456789ab";

    public SecretStoreTests() => Directory.CreateDirectory(_folder);

    private void WritePlaintext(object secrets) =>
        File.WriteAllText(PlaintextPath, JsonSerializer.Serialize(secrets));

    [Fact]
    public void Values_survive_a_write_and_a_reload()
    {
        var store = SecretStore.Load(StorePath, PlaintextPath);
        store.Set(SecretStore.GroqApiKey, FakeGroqKey);
        store.Set(SecretStore.GeminiModel, "gemini-3.5-flash-lite");
        Assert.True(store.Save());

        var reloaded = SecretStore.Load(StorePath, PlaintextPath);
        Assert.Equal(FakeGroqKey, reloaded.Get(SecretStore.GroqApiKey));
        Assert.Equal("gemini-3.5-flash-lite", reloaded.Get(SecretStore.GeminiModel));
    }

    [Fact]
    public void The_file_on_disk_does_not_contain_the_key()
    {
        // The whole point of FR-10.1. If this fails, everything else here is decoration.
        var store = SecretStore.Load(StorePath, PlaintextPath);
        store.Set(SecretStore.GroqApiKey, FakeGroqKey);
        store.Save();

        var bytes = File.ReadAllBytes(StorePath);
        Assert.DoesNotContain(FakeGroqKey, System.Text.Encoding.UTF8.GetString(bytes));
        Assert.DoesNotContain(FakeGroqKey, System.Text.Encoding.Unicode.GetString(bytes));
    }

    [Fact]
    public void A_missing_store_reads_as_empty_rather_than_failing()
    {
        var store = SecretStore.Load(StorePath, PlaintextPath);

        Assert.Null(store.Get(SecretStore.GroqApiKey));
        Assert.Equal(SecretMigration.None, store.Migration);
    }

    [Fact]
    public void A_corrupt_store_leaves_the_app_keyless_rather_than_dead()
    {
        File.WriteAllBytes(StorePath, [0x01, 0x02, 0x03, 0x04, 0x05]);

        var store = SecretStore.Load(StorePath, PlaintextPath);

        Assert.Null(store.Get(SecretStore.GroqApiKey));

        // And it must still be repairable in place.
        store.Set(SecretStore.GroqApiKey, FakeGroqKey);
        Assert.True(store.Save());
        Assert.Equal(FakeGroqKey, SecretStore.Load(StorePath, PlaintextPath).Get(SecretStore.GroqApiKey));
    }

    [Fact]
    public void Plaintext_secrets_are_imported_and_then_removed()
    {
        WritePlaintext(new { groqApiKey = FakeGroqKey, geminiApiKey = FakeGeminiKey });

        var store = SecretStore.Load(StorePath, PlaintextPath);

        Assert.Equal(SecretMigration.Completed, store.Migration);
        Assert.Equal(FakeGroqKey, store.Get(SecretStore.GroqApiKey));
        Assert.Equal(FakeGeminiKey, store.Get(SecretStore.GeminiApiKey));

        // Leaving the plaintext behind would defeat the entire exercise.
        Assert.False(File.Exists(PlaintextPath));

        // And the imported values must be in the encrypted file, not just in memory.
        Assert.Equal(FakeGroqKey, SecretStore.Load(StorePath, PlaintextPath).Get(SecretStore.GroqApiKey));
    }

    [Fact]
    public void Migration_reads_names_case_insensitively()
    {
        // The old file was written by hand; casing is not something to lose keys over.
        WritePlaintext(new { GroqApiKey = FakeGroqKey, GEMINIAPIKEY = FakeGeminiKey });

        var store = SecretStore.Load(StorePath, PlaintextPath);

        Assert.Equal(FakeGroqKey, store.Get(SecretStore.GroqApiKey));
        Assert.Equal(FakeGeminiKey, store.Get(SecretStore.GeminiApiKey));
    }

    [Fact]
    public void An_existing_encrypted_store_is_never_overwritten_by_a_stale_plaintext_file()
    {
        var store = SecretStore.Load(StorePath, PlaintextPath);
        store.Set(SecretStore.GroqApiKey, FakeGroqKey);
        store.Save();

        WritePlaintext(new { groqApiKey = "gsk_STALEkeyfromlastyear000000000000" });

        var reloaded = SecretStore.Load(StorePath, PlaintextPath);

        Assert.Equal(FakeGroqKey, reloaded.Get(SecretStore.GroqApiKey));
        Assert.Equal(SecretMigration.None, reloaded.Migration);

        // Left alone on purpose: deleting a file we did not import is not ours to do.
        Assert.True(File.Exists(PlaintextPath));
    }

    [Fact]
    public void Blank_values_are_removed_rather_than_stored_empty()
    {
        var store = SecretStore.Load(StorePath, PlaintextPath);
        store.Set(SecretStore.GroqApiKey, FakeGroqKey);
        store.Set(SecretStore.GroqApiKey, "   ");

        Assert.Null(store.Get(SecretStore.GroqApiKey));
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed()
    {
        // Pasting a key from a browser very often brings a trailing newline with it, and the
        // resulting 401 looks exactly like a wrong key.
        var store = SecretStore.Load(StorePath, PlaintextPath);
        store.Set(SecretStore.GroqApiKey, $"  {FakeGroqKey}\r\n");

        Assert.Equal(FakeGroqKey, store.Get(SecretStore.GroqApiKey));
    }

    [Fact]
    public void Masking_never_shows_enough_of_a_key_to_use_it()
    {
        var masked = SecretStore.Mask(FakeGroqKey);

        Assert.DoesNotContain(FakeGroqKey, masked);
        Assert.StartsWith("gsk_", masked);
        Assert.Equal("(not set)", SecretStore.Mask(null));
        Assert.Equal("••••••••", SecretStore.Mask("short"));
    }

    [Fact]
    public void No_key_material_ever_reaches_a_log_sink()
    {
        // FR-10.3. Asserted against the real Serilog pipeline, because the requirement is about
        // what lands in the file during a meeting, not about what any one call site intended.
        var captured = new CapturingSink();
        var previous = Log.Logger;
        Log.Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(captured).CreateLogger();

        try
        {
            WritePlaintext(new { groqApiKey = FakeGroqKey, geminiApiKey = FakeGeminiKey });

            var store = SecretStore.Load(StorePath, PlaintextPath);
            store.Set(SecretStore.GeminiApiKey, FakeGeminiKey);
            store.Save();
            SecretStore.Load(StorePath, PlaintextPath);

            // The corrupt path logs an error; make sure that error is not chatty either.
            File.WriteAllBytes(StorePath, [0x09, 0x09, 0x09]);
            SecretStore.Load(StorePath, PlaintextPath);
        }
        finally
        {
            // Restored, not disposed: other test classes share this static and may be writing
            // to it right now.
            Log.Logger = previous;
        }

        Assert.NotEmpty(captured.Messages);
        Assert.DoesNotContain(captured.Messages, m => m.Contains(FakeGroqKey));
        Assert.DoesNotContain(captured.Messages, m => m.Contains(FakeGeminiKey));
    }

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<string> _messages = [];
        private readonly Lock _gate = new();

        public IReadOnlyList<string> Messages { get { lock (_gate) return [.. _messages]; } }

        public void Emit(LogEvent logEvent)
        {
            using var writer = new StringWriter();
            logEvent.RenderMessage(writer);

            // Properties and the exception too: a key smuggled into a structured property or an
            // exception message would still be written to the file sink.
            var text = writer.ToString()
                       + string.Join(" ", logEvent.Properties.Select(p => $"{p.Key}={p.Value}"))
                       + logEvent.Exception?.ToString();

            lock (_gate) _messages.Add(text);
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp file is not worth failing a test run over.
        }
    }
}
