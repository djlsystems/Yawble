using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Integrations;

namespace Yawble.Core.Scripts.Actions;

/// <summary>
/// What the script engine's API actions need from the integrations (card 586's entry points): a script's call, and the operations a team's
/// scripts may call. <see cref="IntegrationScriptApi"/> is the real one; tests fake it.
/// </summary>
public interface IScriptApi
{
    /// <summary><see cref="IntegrationService.CallForScriptAsync"/>: throws <see cref="ScriptApiRefusedException"/> when the call is not made.</summary>
    Task<ScriptApiResult> CallAsync(ScriptCaller caller, string integration, string action, JsonObject args, CancellationToken ct);

    /// <summary>The teams by name (the global catalog is every team's operations).</summary>
    IReadOnlyList<string> Teams();

    /// <summary>Every operation <paramref name="team"/>'s scripts may call (allowed or ask), across the web APIs turned on for it.</summary>
    IReadOnlyList<ScriptOperation> Operations(string team);
}

/// <summary>
/// <see cref="IScriptApi"/> over the app's <see cref="IntegrationService"/>. The service is made after the script runner in the app, so it
/// comes as a task: a call waits for it; a catalog read before it is there sees no operations.
/// </summary>
public sealed class IntegrationScriptApi(string connectionString, Task<IntegrationService> service) : IScriptApi
{
    public IntegrationScriptApi(string connectionString, IntegrationService service) : this(connectionString, Task.FromResult(service)) { }

    public async Task<ScriptApiResult> CallAsync(ScriptCaller caller, string integration, string action, JsonObject args, CancellationToken ct)
    {
        var svc = await service.WaitAsync(ct);
        return await svc.CallForScriptAsync(caller, integration, action, args, ct);
    }

    public IReadOnlyList<string> Teams() => Names("SELECT name FROM teams ORDER BY id;");

    public IReadOnlyList<ScriptOperation> Operations(string team)
    {
        if (!service.IsCompletedSuccessfully) return [];
        var svc = service.Result;
        var list = new List<ScriptOperation>();
        foreach (var api in Names("SELECT name FROM apis WHERE kind = 'http' ORDER BY id;"))
        {
            try { list.AddRange(svc.ListScriptOperations(team, api)); }
            catch (ScriptApiRefusedException) { } // not turned on for the team (or gone meanwhile)
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
