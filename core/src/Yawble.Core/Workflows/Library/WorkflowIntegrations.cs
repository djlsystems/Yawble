using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Integrations;

namespace Yawble.Core.Workflows.Library;

/// <summary>
/// One of the person's integrations a workflow can name: its name as Settings > Integrations shows it, its kind ("email" or
/// "api") and the teams it is turned on for (every team when <see cref="EnabledAll"/>).
/// </summary>
public sealed record WorkflowIntegrationInfo(string Name, string Kind, bool EnabledAll, IReadOnlyList<long> TeamIds)
{
    public bool IsOnFor(long teamId) => EnabledAll || TeamIds.Contains(teamId);
}

/// <summary>
/// The person's integrations as workflows see them: the Email integrations and the web APIs, by name, with the teams each is
/// turned on for, and an API's operations a workflow may call.
/// </summary>
public interface IWorkflowIntegrationDirectory
{
    IReadOnlyList<WorkflowIntegrationInfo> Integrations();

    /// <summary>
    /// The operations of the API <paramref name="api"/> a workflow may call (allowed or ask), with their parameters as a workflow passes them:
    /// for <paramref name="teamId"/>'s rules, or with no team the rules for every team. Empty when it is unknown.
    /// </summary>
    IReadOnlyList<WorkflowOperation> ApiOperations(string api, long? teamId);
}

public static class WorkflowIntegrationDirectoryExtensions
{
    /// <summary>The integration with this name (any case; an Email integration first when an API has the same name), or null.</summary>
    public static WorkflowIntegrationInfo? Find(this IWorkflowIntegrationDirectory directory, string? name)
    {
        var n = (name ?? "").Trim();
        if (n.Length == 0) return null;
        var all = directory.Integrations();
        return all.FirstOrDefault(i => i.Kind == "email" && string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>No integrations (tests, and a library made without a directory): every name a step uses must then be in requires.</summary>
public sealed class NoWorkflowIntegrations : IWorkflowIntegrationDirectory
{
    public static readonly NoWorkflowIntegrations Instance = new();
    public IReadOnlyList<WorkflowIntegrationInfo> Integrations() => [];
    public IReadOnlyList<WorkflowOperation> ApiOperations(string api, long? teamId) => [];
}

/// <summary>A fixed set of integrations (tests).</summary>
public sealed class WorkflowIntegrationSet(IEnumerable<WorkflowIntegrationInfo> integrations,
    Func<string, long?, IReadOnlyList<WorkflowOperation>>? operations = null) : IWorkflowIntegrationDirectory
{
    private readonly List<WorkflowIntegrationInfo> _all = integrations.ToList();
    public IReadOnlyList<WorkflowIntegrationInfo> Integrations() => _all;
    public IReadOnlyList<WorkflowOperation> ApiOperations(string api, long? teamId) => operations?.Invoke(api, teamId) ?? [];
}

/// <summary>
/// The app's directory: the Email integrations and web APIs read from the database, and an API's operations through the
/// <see cref="IntegrationService"/> (made after the workflow library: until it is there, no operations).
/// </summary>
public sealed class IntegrationWorkflowDirectory(string connectionString, Task<IntegrationService> service) : IWorkflowIntegrationDirectory
{
    public IReadOnlyList<WorkflowIntegrationInfo> Integrations()
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        var list = new List<WorkflowIntegrationInfo>();
        list.AddRange(Read(c, "SELECT id, name, enabled_all FROM email_integrations ORDER BY name COLLATE NOCASE;",
            "SELECT team_id FROM email_integration_teams WHERE email_id = $id ORDER BY team_id;", "email"));
        list.AddRange(Read(c, "SELECT id, name, enabled_all FROM apis WHERE kind = 'http' ORDER BY name COLLATE NOCASE;",
            "SELECT team_id FROM api_teams WHERE api_id = $id ORDER BY team_id;", "api"));
        return list;
    }

    public IReadOnlyList<WorkflowOperation> ApiOperations(string api, long? teamId)
    {
        if (!service.IsCompletedSuccessfully) return [];
        try { return service.Result.ListWorkflowOperationsFor(api, teamId); }
        catch (WorkflowApiRefusedException) { return []; }
    }

    private static List<WorkflowIntegrationInfo> Read(SqliteConnection c, string rows, string teams, string kind)
    {
        var found = new List<(long Id, string Name, bool All)>();
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = rows;
            using var r = cmd.ExecuteReader();
            while (r.Read()) found.Add((r.GetInt64(0), r.GetString(1), r.GetInt64(2) != 0));
        }
        var list = new List<WorkflowIntegrationInfo>();
        foreach (var (id, name, all) in found)
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = teams;
            cmd.Parameters.AddWithValue("$id", id);
            using var r = cmd.ExecuteReader();
            var ids = new List<long>();
            while (r.Read()) ids.Add(r.GetInt64(0));
            list.Add(new WorkflowIntegrationInfo(name, kind, all, ids));
        }
        return list;
    }
}

/// <summary>
/// A workflow's <c>requires</c>, derived from its steps: one entry per name the integration steps <c>use</c> (in the order the
/// steps first use it), its kind (the integration's of that name, else the kind the document's own entry gives) and the
/// distinct actions the steps call, sorted. A name that is neither an integration nor in the document's requires is left
/// out (the check then reports the step).
/// </summary>
public static class WorkflowRequires
{
    public sealed record Derived(JsonArray Requires, bool Stale);

    public static Derived Derive(JsonObject document, IWorkflowIntegrationDirectory integrations)
    {
        var declared = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var r in document["requires"] as JsonArray ?? [])
            if (r is JsonObject o && Text(o, "name") is { } n && Text(o, "kind") is { } k) declared.TryAdd(n, k);

        var order = new List<string>();
        var actions = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var s in document["steps"] as JsonArray ?? [])
        {
            if (s is not JsonObject step || Text(step, "type") != "integration" || Text(step, "use") is not { } use) continue;
            if (!actions.TryGetValue(use, out var set))
            {
                actions[use] = set = new SortedSet<string>(StringComparer.Ordinal);
                order.Add(use);
            }
            if (Text(step, "action") is { } action) set.Add(action);
        }

        var known = integrations.Integrations();
        var list = new JsonArray();
        foreach (var use in order)
        {
            var kind = known.FirstOrDefault(i => i.Kind == "email" && string.Equals(i.Name, use, StringComparison.OrdinalIgnoreCase))?.Kind
                ?? known.FirstOrDefault(i => string.Equals(i.Name, use, StringComparison.OrdinalIgnoreCase))?.Kind
                ?? declared.GetValueOrDefault(use);
            if (kind is null) continue;
            list.Add(new JsonObject
            {
                ["name"] = use, ["kind"] = kind, ["actions"] = new JsonArray(actions[use].Select(a => (JsonNode?)a).ToArray()),
            });
        }
        // An alias no step uses stays as it is (its members keep their binding for it); an integration no step uses goes.
        foreach (var r in document["requires"] as JsonArray ?? [])
            if (r is JsonObject o && Text(o, "name") is { } n && !actions.ContainsKey(n)
                && !known.Any(i => string.Equals(i.Name, n, StringComparison.OrdinalIgnoreCase)))
                list.Add(o.DeepClone());
        return new Derived(list, !Same(document["requires"] as JsonArray ?? [], list));
    }

    /// <summary>The same entries, whatever the order of the entries and of their actions.</summary>
    private static bool Same(JsonArray current, JsonArray derived)
    {
        static string Key(JsonNode? n) => n is JsonObject o
            ? $"{Text(o, "name")}\u0001{Text(o, "kind")}\u0001{string.Join("\u0002", (o["actions"] as JsonArray ?? []).Select(a => a is JsonValue v && v.TryGetValue<string>(out var s) ? s : "?").Order(StringComparer.Ordinal))}"
            : "?";
        var a = current.Select(Key).Order(StringComparer.Ordinal).ToList();
        var b = derived.Select(Key).Order(StringComparer.Ordinal).ToList();
        return a.SequenceEqual(b, StringComparer.Ordinal);
    }

    /// <summary>The names in requires that are not one of the person's integrations: aliases each member binds.</summary>
    public static IReadOnlyList<string> Aliases(JsonObject document, IWorkflowIntegrationDirectory integrations) =>
        (document["requires"] as JsonArray ?? []).OfType<JsonObject>().Select(o => Text(o, "name")).OfType<string>()
            .Where(n => integrations.Find(n) is null).ToList();

    private static string? Text(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : null;
}
