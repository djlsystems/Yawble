using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yawble.Core.Integrations;

/// <summary>The team's configured model, as far as Discover needs it: a prompt in, text out. The host supplies it.</summary>
public interface ICommandModel
{
    Task<string> CompleteAsync(string prompt, CancellationToken ct);
}

/// <summary>
/// The model fallback for leaves the verb table could not place. It is sent only the CLI's name and, per
/// command, its path and one-line summary: no help body, no user data. The answer is a fixed schema; nothing free-form is
/// kept except a clipped "why". A model answer is only ever advice: it may lock a command (credential) or mark it as a change
/// or destructive, and "read-only" from a model never pre-ticks anything (source "model").
/// </summary>
public static class ModelClassifier
{
    public const int BatchSize = 40;
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(20);

    public static async Task<IReadOnlyDictionary<string, Classification>> ClassifyAsync(string cli,
        IReadOnlyList<(string Path, string Summary)> leaves, ICommandModel? model, CancellationToken ct)
    {
        var result = new Dictionary<string, Classification>(StringComparer.Ordinal);
        if (model is null) return result;
        foreach (var batch in leaves.Chunk(BatchSize))
        {
            for (var attempt = 0; attempt < 2; attempt++) // one retry
            {
                try
                {
                    using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    limit.CancelAfter(CallTimeout);
                    var answer = await model.CompleteAsync(Prompt(cli, batch), limit.Token);
                    foreach (var (path, cls) in Validate(answer, batch.Select(b => b.Path).ToHashSet(StringComparer.Ordinal))) result[path] = cls;
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception e) when (e is OperationCanceledException or JsonException or FormatException or InvalidOperationException or HttpRequestException or IOException)
                {
                    // timeout or a bad answer: the batch stays unknown after the retry
                }
            }
        }
        return result;
    }

    public static string Prompt(string cli, IEnumerable<(string Path, string Summary)> batch)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Classify commands of the command-line tool \"{cli}\" by what running them does. For each, answer one class:");
        sb.AppendLine("readonly (changes nothing, reveals no secret), change (creates or modifies something), destructive (deletes or cannot be undone), credential (reveals or changes a sign-in, token, key or secret).");
        sb.AppendLine("Answer ONLY JSON: {\"items\":[{\"path\":\"<the path as given>\",\"class\":\"readonly|change|destructive|credential\",\"why\":\"<=80 chars\"}]}");
        sb.AppendLine("Commands (path - summary):");
        foreach (var (path, summary) in batch) sb.AppendLine($"{path} - {HelpParser.Clip(summary ?? "", 160)}");
        return sb.ToString();
    }

    /// <summary>The items of an answer that are valid: a path that was asked, a known class; the reason clipped and cleaned.</summary>
    public static IEnumerable<(string Path, Classification Class)> Validate(string answer, ISet<string> asked)
    {
        var text = (answer ?? "").Trim();
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) throw new JsonException("no JSON object in the answer");
        var items = JsonNode.Parse(text[start..(end + 1)])?["items"] as JsonArray ?? throw new JsonException("no items");
        foreach (var item in items)
        {
            var path = item?["path"]?.GetValue<string>();
            var cls = item?["class"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            if (path is null || !asked.Contains(path)) continue;
            if (cls is not (CommandClass.ReadOnly or CommandClass.Change or CommandClass.Destructive or CommandClass.Credential)) continue;
            var why = new string((item?["why"]?.GetValue<string>() ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
            yield return (path, new Classification(cls, ClassSource.Model, "suggested by the model: " + HelpParser.Clip(why, 80)));
        }
    }
}
