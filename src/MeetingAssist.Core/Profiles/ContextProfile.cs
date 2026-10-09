using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using MeetingAssist.Core.Audio;

namespace MeetingAssist.Core.Profiles;

/// <summary>
/// Pre-meeting context, authored by hand. Stuffed directly into the prompt — no retrieval
/// (spec D15), because realistic context is a few thousand tokens against a ~1M window.
///
/// Three free-text fields, the same for any kind of meeting: a client check-in, a negotiation
/// and a salary talk all have a meeting, a "me", and questions to prepare for. Blank fields are
/// left out of the prompt, so a field that does not apply costs nothing.
/// </summary>
public sealed class ContextProfile
{
    public string Name { get; set; } = "default";

    /// <summary>The goal, who is attending and who they are, the agenda, a good outcome.</summary>
    public string Meeting { get; set; } = "";

    /// <summary>What the user brings: their role, experience, stories, figures, their offer.</summary>
    public string AboutMe { get; set; } = "";

    /// <summary>
    /// Questions and pushback the user expects, each with the answer they want to give. The
    /// field that most changes what appears on screen: when one comes up, the answer is ready.
    /// </summary>
    public string Questions { get; set; } = "";

    /// <summary>
    /// Manually written vocabulary for Whisper's `prompt` parameter (spec D9). Proper nouns,
    /// product names, jargon, attendee names. Hard cap ~224 tokens — longer is silently
    /// truncated by the API. Never contains prior transcript (spec D10).
    /// </summary>
    public string Vocabulary { get; set; } = "";

    /// <summary>ISO-639-1. Drives both the STT `language` parameter and the answer language.</summary>
    public string Language { get; set; } = "en";

    /// <summary>
    /// How answers should be written (spec FR-6.7). Blank means the built-in default,
    /// <see cref="Assistant.PromptBuilder.DefaultStyle"/> — so nobody has to author a prompt to
    /// use the app, and a bad edit is undone by clearing the box.
    ///
    /// It lives on the profile rather than in one global file because it is the same kind of
    /// thing as the rest of the context: text tuned between meetings. A sales call profile and
    /// a project review profile can want different answers from the same transcript.
    ///
    /// Read once at session start, never per Ask, so the system block stays byte-identical
    /// within a session and implicit caching keeps working (FR-6.5/D14).
    /// </summary>
    public string AnswerStyle { get; set; } = "";

    /// <summary>
    /// Mock mode only: who the AI playing the other party is and how to play them — sample
    /// questions, tone, difficulty, what to probe. Added to the profile's own context, which the
    /// counterpart also receives. Blank means it plays the other party as the meeting
    /// describes them. Never sent to the cue-note assistant.
    /// </summary>
    public string MockBrief { get; set; } = "";

    public string MicLabel { get; set; } = "You";
    public string LoopbackLabel { get; set; } = "Other side";

    public string LabelFor(AudioChannelKind channel) =>
        channel == AudioChannelKind.Mic ? MicLabel : LoopbackLabel;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static ContextProfile Load(string path)
    {
        try
        {
            return FromJson(File.ReadAllText(path));
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"Could not parse profile: {path}", ex);
        }
    }

    /// <summary>
    /// Reads a profile from JSON: a file on disk, or text an AI chat wrote. Chats tend to wrap
    /// JSON in a code fence or a sentence, so only the outermost braces are read.
    /// </summary>
    public static ContextProfile FromJson(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end < start) throw new InvalidDataException("There is no JSON object in the text.");

        try
        {
            var json = JsonNode.Parse(text[start..(end + 1)], documentOptions: new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject ?? throw new InvalidDataException("The JSON is not an object.");

            var profile = json.Deserialize<ContextProfile>(Json)
                          ?? throw new InvalidDataException("The JSON is empty.");
            profile.AdoptPreviousFields(json);
            return profile;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The JSON could not be read: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Profiles written before the fields were made generic had company (who the user
    /// represents), product, objections and notes. They map onto the new fields, so the text
    /// the user wrote is carried over rather than silently dropped; saving writes the new names.
    /// </summary>
    private void AdoptPreviousFields(JsonObject json)
    {
        string Old(string key) =>
            json[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";

        static string Join(params string[] parts) =>
            string.Join("\n\n", parts.Where(part => part.Length > 0));

        if (string.IsNullOrWhiteSpace(Meeting)) Meeting = Old("notes");
        if (string.IsNullOrWhiteSpace(AboutMe)) AboutMe = Join(Old("company"), Old("product"));
        if (string.IsNullOrWhiteSpace(Questions)) Questions = Old("objections");
    }

    public void Save(string path) =>
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json));

    /// <summary>
    /// A starter profile so `spike` runs before the user has written one, and the content a new
    /// profile starts from. Fictional, and deliberately not a sales call: the app is for any
    /// meeting.
    /// </summary>
    public static ContextProfile Sample() => new()
    {
        Name = "sample",
        Meeting = "Fortnightly check-in on the Northwind data warehouse migration. Attending: "
                  + "Priya Shah, Northwind's head of data, who decides scope, and Tom Becker, their "
                  + "platform engineer. Goal: confirm the 14 March cut-over date and agree how the "
                  + "extra reporting work is handled.",
        AboutMe = "Lead consultant on the migration since September. 31 of 40 pipelines are moved and "
                  + "the nightly load is down from 5 hours to 70 minutes. Of the 9 left, 3 depend on "
                  + "the CRM export, which Northwind owns.",
        Questions = "\"Will we make 14 March?\" -> Yes for 37 pipelines; the 3 CRM ones need the export "
                    + "fixed by 28 February.\n"
                    + "\"Why is the reporting work extra?\" -> It was outside the signed scope: 6 days, "
                    + "and it can start after cut-over.\n"
                    + "\"Can Tom's team take over support?\" -> Yes: two handover sessions the week after "
                    + "cut-over.",
        Vocabulary = "Northwind, Priya Shah, Tom Becker, cut-over, Snowflake, dbt, Airflow, CRM export, "
                     + "pipelines, data warehouse",
        Language = "en"
    };
}
