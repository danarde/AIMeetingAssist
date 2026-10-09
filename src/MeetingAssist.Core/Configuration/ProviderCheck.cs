using System.Net;
using System.Net.Http.Headers;
using Google.GenAI;
using Google.GenAI.Types;
using Serilog;

namespace MeetingAssist.Core.Configuration;

/// <summary>Outcome of a provider check. <paramref name="Detail"/> is safe to display.</summary>
public sealed record ProviderStatus(bool Ok, string Detail);

/// <summary>
/// Answers "is this key going to work?" before a meeting rather than during one (spec FR-10.5).
///
/// Each check is the cheapest authenticated call the provider offers — listing models — so it
/// costs nothing and still proves the key, the network and the endpoint together. A key is only
/// ever sent in a header, never in a URL, and never reaches the log in any form.
/// </summary>
public static class ProviderCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static async Task<ProviderStatus> GroqAsync(string? apiKey, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new ProviderStatus(false, "No key set");

        try
        {
            using var http = new HttpClient { Timeout = Timeout };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());

            using var response = await http
                .GetAsync("https://api.groq.com/openai/v1/models", ct)
                .ConfigureAwait(false);

            return response.StatusCode switch
            {
                HttpStatusCode.OK => new ProviderStatus(true, "Key works"),
                HttpStatusCode.Unauthorized => new ProviderStatus(false, "Key rejected (401)"),
                HttpStatusCode.Forbidden => new ProviderStatus(false, "Key has no access (403)"),
                // A 429 proves the key is valid — it is the quota that is the problem.
                HttpStatusCode.TooManyRequests => new ProviderStatus(true, "Key works, but rate limited right now"),
                var other => new ProviderStatus(false, $"Groq returned {(int)other} {other}")
            };
        }
        catch (Exception ex)
        {
            // The same scrubbed sentence goes to the log and to the UI; neither gets the raw
            // exception, which is the thing most likely to carry something it should not.
            Log.Warning("Groq key check failed: {Reason}", Describe(ex, apiKey));
            return new ProviderStatus(false, Describe(ex, apiKey));
        }
    }

    public static async Task<ProviderStatus> GeminiAsync(
        string? apiKey, string? modelId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) return new ProviderStatus(false, "No key set");

        try
        {
            using var client = new Client(apiKey: apiKey.Trim());

            var names = new List<string>();
            await foreach (var model in await client.Models
                .ListAsync(new ListModelsConfig { QueryBase = true }, ct).ConfigureAwait(false))
            {
                if (model.Name is { Length: > 0 } name) names.Add(name.Replace("models/", ""));
                if (names.Count >= 200) break;
            }

            if (names.Count == 0) return new ProviderStatus(false, "Key returned no models");

            if (string.IsNullOrWhiteSpace(modelId))
                return new ProviderStatus(true, $"Key works ({names.Count} models)");

            // Model ids move faster than any document (spec O1), so the useful answer is not
            // "the key works" but "the key works *and* the model you configured is there".
            return names.Contains(modelId, StringComparer.OrdinalIgnoreCase)
                ? new ProviderStatus(true, $"Key works, {modelId} available")
                : new ProviderStatus(false, $"Key works, but {modelId} is not in the {names.Count} models it can use");
        }
        catch (Exception ex)
        {
            Log.Warning("Gemini key check failed: {Reason}", Describe(ex, apiKey));
            return new ProviderStatus(false, Describe(ex, apiKey));
        }
    }

    private static string Describe(Exception ex, string apiKey) => ex switch
    {
        TaskCanceledException or OperationCanceledException => "Timed out — check the network",
        HttpRequestException => "Could not reach the provider",

        // An unrecognised failure means an unrecognised message, and a provider SDK that echoes
        // the argument it rejected would put the key straight into the log and the UI. Cheap
        // insurance against a case that is impossible to notice after the fact.
        _ => Scrub(ex.Message, apiKey)
    };

    private static string Scrub(string message, string apiKey)
    {
        var key = apiKey.Trim();
        return key.Length >= 8 ? message.Replace(key, "***") : message;
    }
}
