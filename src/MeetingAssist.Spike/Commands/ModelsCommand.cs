using Google.GenAI;
using Google.GenAI.Types;
using MeetingAssist.Core.Configuration;

namespace MeetingAssist.Spike.Commands;

/// <summary>
/// Lists the Gemini models this API key can actually use. Resolves spec open item O1:
/// model ids change faster than any document, so the authority is the API, not our notes.
/// </summary>
public static class ModelsCommand
{
    public static async Task<int> RunAsync(Args cli, AppConfig config, CancellationToken ct)
    {
        using var client = new Client(apiKey: config.RequireGeminiKey());

        var filter = cli.Get("filter");
        var showAll = cli.Has("all");
        var rows = new List<(string Name, string Display, int? In, int? Out)>();

        await foreach (var model in await client.Models.ListAsync(
            new ListModelsConfig { QueryBase = true }, ct).ConfigureAwait(false))
        {
            var name = model.Name ?? "";
            if (!showAll && !model.SupportedActions?.Contains("generateContent") is true) continue;
            if (filter is not null && !name.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            rows.Add((name.Replace("models/", ""), model.DisplayName ?? "",
                      model.InputTokenLimit, model.OutputTokenLimit));
        }

        if (rows.Count == 0)
        {
            Console.WriteLine("No models returned. Check the key, or try --all.");
            return 1;
        }

        var width = Math.Max(28, rows.Max(r => r.Name.Length));
        Console.WriteLine($"{"model id".PadRight(width)}  {"in".PadLeft(9)}  {"out".PadLeft(7)}  display name");
        Console.WriteLine(new string('-', width + 40));

        foreach (var r in rows.OrderBy(r => r.Name, StringComparer.Ordinal))
            Console.WriteLine($"{r.Name.PadRight(width)}  {r.In,9}  {r.Out,7}  {r.Display}");

        Console.WriteLine($"\n{rows.Count} model(s). Set the one you want via GEMINI_MODEL, " +
                          "or geminiModel in the secrets file.");
        return 0;
    }
}
