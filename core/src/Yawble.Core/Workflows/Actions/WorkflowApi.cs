using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Integrations;

namespace Yawble.Core.Workflows.Actions;

/// <summary>
/// What the workflow engine's API actions need from the integrations: a workflow's call, and the operations a team's
/// workflows may call. <see cref="IntegrationWorkflowApi"/> is the real one; tests fake it.
/// </summary>
public interface IWorkflowApi
{
    /// <summary><see cref="IntegrationService.CallForWorkflowAsync"/>: throws <see cref="WorkflowApiRefusedException"/> when the call is not made.</summary>
    Task<WorkflowApiResult> CallAsync(WorkflowCaller caller, string integration, string action, JsonObject args, CancellationToken ct);

    /// <summary>The teams by name (the global catalog is every team's operations).</summary>
    IReadOnlyList<string> Teams();

    /// <summary>Every operation <paramref name="team"/>'s workflows may call (allowed or ask), across the web APIs turned on for it.</summary>
    IReadOnlyList<WorkflowOperation> Operations(string team);
}

/// <summary>
/// <see cref="IWorkflowApi"/> over the app's <see cref="IntegrationService"/>. The service is made after the workflow runner in the app, so it
/// comes as a task: a call waits for it; a catalog read before it is there sees no operations.
/// </summary>
public sealed class IntegrationWorkflowApi(string connectionString, Task<IntegrationService> service) : IWorkflowApi
{
    public IntegrationWorkflowApi(string connectionString, IntegrationService service) : this(connectionString, Task.FromResult(service)) { }

    public async Task<WorkflowApiResult> CallAsync(WorkflowCaller caller, string integration, string action, JsonObject args, CancellationToken ct)
    {
        var svc = await service.WaitAsync(ct);
        return await svc.CallForWorkflowAsync(caller, integration, action, args, ct);
    }

    public IReadOnlyList<string> Teams() => Names("SELECT name FROM teams ORDER BY id;");

    public IReadOnlyList<WorkflowOperation> Operations(string team)
    {
        if (!service.IsCompletedSuccessfully) return [];
        var svc = service.Result;
        var list = new List<WorkflowOperation>();
        foreach (var api in Names("SELECT name FROM apis WHERE kind = 'http' ORDER BY id;"))
        {
            try { list.AddRange(svc.ListWorkflowOperations(team, api)); }
            catch (WorkflowApiRefusedException) { } // not turned on for the team (or gone meanwhile)
        }
        return list;
    }

    private List<string> Names(string sql)
    {
        using var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        return names;
    }
}
