using System.ComponentModel;
using System.Data.Common;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

/// <summary>
/// Admin → MCP servers and Team settings → MCP servers (MCP 1). The list is an administrator's, and a team
/// is given entries from it by a person: every route here is a person's only (<c>HumansOnly</c>). Every
/// write lands with its tenant rows in one transaction. No answer, row or log line carries a connection's
/// value or the URL's query string. See <c>docs/mcp-servers.md</c>.
/// </summary>
public static class McpServerEndpoints
{
    private const string Area = "MCP servers";

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/mcp-servers", async (McpServers servers, CancellationToken ct) =>
            Results.Ok(new { servers = (await ViewsAsync(servers, ct)).Select(v => v.View) }))
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("The instance's MCP servers")
            .WithDescription(
                "`{ servers: [...] }` by name: each entry's id, name, kind (`url` or `runs`), its settings - a web address "
                + "without its query (`url`, with `queryHidden`), or the command, package, pinned version, arguments and "
                + "environment variables (each a connection's field) - its `health` (`ok`, `needs-you`, `unchecked`) with "
                + "`healthReason`, the tools it last listed and the teams it is given to. Never a value or a query string.");

        app.MapPost("/api/mcp-servers", async (
            SaveMcpServer request, McpServers servers, WorkerAsks asks, McpStdio stdio, HttpContext context, CancellationToken ct) =>
        {
            var (server, url, error) = await servers.ReadAsync(request, null, ct);
            if (error is not null) return Results.BadRequest(new { error });

            var actor = ConnectionEndpoints.Actor(context);
            McpServerWrite written;
            try
            {
                written = await servers.Store.AddAsync(server!, url, actor.Row(
                    TenantActions.McpServerAdded, server!.Id, server.Name, McpServers.Detail(server, await ConnectionNameAsync(servers, server, ct))), ct);
            }
            catch (DbException exception)
            {
                return Unrecorded("The MCP server was not added", exception);
            }

            if (written == McpServerWrite.NameTaken) return Results.Conflict(new { error = McpServers.NameTaken(server.Name) });

            return await SavedAsync(servers, server.Id, actor, asks, stdio, StatusCodes.Status201Created, ct);
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Add an MCP server")
            .WithDescription(
                "Body `{ name, kind, ... }`. `name`: 1 to 20 letters, digits, `-` or `_`, unique on the instance (409 when "
                + "taken). `kind: url`: `url`, and optionally `connection` (an API key connection's id), `field` (default its "
                + "one secret field), `header` (default `Authorization`) and `template` holding `{value}` (default "
                + "`Bearer {value}`). `kind: runs`: `command` (`npx` or `uvx`), `package`, `version` (one exact version: "
                + "missing, `latest` or a range is refused 400 with a sentence saying a version must be pinned), `args` and "
                + "`env` (`[{ name, connection, field }]`). 201 `{ server, check }` with an `mcp.server-added` row. A web "
                + "address is then checked - initialize and list its tools - and `check` says `ok` with the tools, `refused` "
                + "(the entry and its connection read Needs you) or `failed` (nothing marked). A `runs` entry is checked on a "
                + "worker: started as the `mcp` user in a process of its own, its tools listed, then stopped - `ok` or `failed`.");

        app.MapPut("/api/mcp-servers/{id}", async (
            [Description("The entry's id.")] string id,
            SaveMcpServer request, McpServers servers, WorkerAsks asks, McpStdio stdio, HttpContext context, CancellationToken ct) =>
        {
            if (await servers.Store.GetAsync(id, ct) is not { } before) return NoServer(id);

            var (server, url, error) = await servers.ReadAsync(request, before, ct);
            if (error is not null) return Results.BadRequest(new { error });

            var actor = ConnectionEndpoints.Actor(context);
            McpServerWrite written;
            try
            {
                written = await servers.Store.UpdateAsync(server!, url, actor.Row(
                    TenantActions.McpServerEdited, server!.Id, server.Name,
                    McpServers.Detail(server, await ConnectionNameAsync(servers, server, ct), new { changed = McpServers.Changed(before, server, url is not null) })), ct);
            }
            catch (DbException exception)
            {
                return Unrecorded("The MCP server was not changed", exception);
            }

            if (written == McpServerWrite.NameTaken) return Results.Conflict(new { error = McpServers.NameTaken(server.Name) });
            if (written == McpServerWrite.Missing) return NoServer(id);

            // THE PROCESS A WORKER RUNS FOR IT STOPS: the next call starts it with what the entry holds now.
            if (before.Kind == McpServerRecord.Runs) await stdio.StopEverywhereAsync(before.Id, ct);

            return await SavedAsync(servers, server.Id, actor, asks, stdio, StatusCodes.Status200OK, ct);
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Change an MCP server")
            .WithDescription(
                "Body as for an add; for a web address, `url` left out keeps the stored address (query and all). 200 "
                + "`{ server, check }` with an `mcp.server-edited` row naming the settings that changed, then the same check "
                + "as a save. An entry the platform runs is stopped on every worker first; the next call starts it again. "
                + "404 for an unknown id, 409 for a name taken.");

        app.MapPost("/api/mcp-servers/{id}/test", async (
            [Description("The entry's id.")] string id,
            McpServers servers, WorkerAsks asks, McpStdio stdio, HttpContext context, CancellationToken ct) =>
        {
            if (await servers.Store.GetAsync(id, ct) is not { } server) return NoServer(id);

            McpServerCheckAnswer check;
            try
            {
                check = await servers.CheckAsync(ConnectionEndpoints.Actor(context), server, asks, stdio, ct);
            }
            catch (DbException exception)
            {
                return Unrecorded("The check's result was not recorded", exception);
            }

            return Results.Ok(new { check = check.View, server = await ViewAsync(servers, id, ct) });
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Test an MCP server")
            .WithDescription(
                "The check a save runs, again: initialize and list the tools, on a worker. `{ check: { outcome, sentence, "
                + "tools, toolCount }, server }`, with an `mcp.server-checked` row. `refused` marks the entry and its "
                + "connection Needs you; `failed` (not reached) marks neither; `ok` clears the entry's mark. A `runs` entry is "
                + "started on a worker in a process of its own, listed and stopped.");

        app.MapDelete("/api/mcp-servers/{id}", async (
            [Description("The entry's id.")] string id,
            McpServers servers, McpStdio stdio, HttpContext context, CancellationToken ct) =>
        {
            if (await servers.Store.GetAsync(id, ct) is not { } server) return NoServer(id);

            var actor = ConnectionEndpoints.Actor(context);
            IReadOnlyList<string>? teams;
            try
            {
                teams = await servers.Store.RemoveAsync(id, from =>
                [
                    actor.Row(TenantActions.McpServerRemoved, server.Id, server.Name, McpServers.Detail(server, null, new { teams = from })),
                    .. from.Select(team => actor.Row(TenantActions.TeamMcpServerRemoved, server.Id, server.Name, new
                    {
                        team, server = server.Id, name = server.Name, reason = "removed from the instance",
                    })),
                ], ct);
            }
            catch (DbException exception)
            {
                return Unrecorded("The MCP server was not removed", exception);
            }

            if (teams is null) return NoServer(id);

            if (server.Kind == McpServerRecord.Runs) await stdio.StopEverywhereAsync(server.Id, ct);

            return Results.Ok(new { removed = server.Id, name = server.Name, teams });
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Remove an MCP server")
            .WithDescription(
                "Removes the entry and every team's assignment of it in one transaction, with an `mcp.server-removed` row "
                + "and one `team.mcp-server-removed` row per team that had it; an entry the platform runs is stopped on every "
                + "worker. `{ removed, name, teams }`; 404 for an unknown id.");

        // ---- Team settings → MCP servers ---------------------------------------------------------------

        app.MapGet("/api/teams/{team}/mcp-servers", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, McpServers servers, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

            return Results.Ok(new { team = stored, servers = (await TeamViewsAsync(stored, servers, ct)).Select(v => v.View) });
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("The MCP servers given to a team")
            .WithDescription(
                "`{ team, servers: [{ assignedAt, server }] }`, oldest first; `server` as `GET /api/mcp-servers` lists it. "
                + "A new team has none, and a team with none works as it always has.");

        app.MapPost("/api/teams/{team}/mcp-servers", async (
            [Description(Describe.Team)] string team,
            AssignTeamMcpServer request, TeamRegistry teams, McpServers servers, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

            var id = request.Server?.Trim();
            if (string.IsNullOrEmpty(id)) return Results.BadRequest(new { error = "`server` is required: the id of an MCP server." });
            if (await servers.Store.GetAsync(id, ct) is not { } server) return NoServer(id);

            AssignOutcome outcome;
            try
            {
                outcome = await servers.Store.AssignAsync(stored, id, ConnectionEndpoints.Actor(context).Row(
                    TenantActions.TeamMcpServerAssigned, server.Id, server.Name, new { team = stored, server = server.Id, name = server.Name }), ct);
            }
            catch (DbException exception)
            {
                return Unrecorded("The MCP server was not given to the team", exception);
            }

            if (outcome == AssignOutcome.NoConnection) return NoServer(id);

            var entry = (await TeamViewsAsync(stored, servers, ct)).FirstOrDefault(e => e.Id == id);
            return Results.Json(entry?.View, statusCode: outcome == AssignOutcome.Assigned ? StatusCodes.Status201Created : StatusCodes.Status200OK);
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Give an MCP server to a team")
            .WithDescription(
                "Body `{ server }`: an entry's id. 201 with the entry as `GET` lists it and a `team.mcp-server-assigned` row "
                + "(team, id, name) in the same transaction; 200 and nothing written when the team already has it. 404 for "
                + "an unknown team or entry. A person's act only.");

        app.MapDelete("/api/teams/{team}/mcp-servers/{id}", async (
            [Description(Describe.Team)] string team,
            [Description("The entry's id.")] string id,
            TeamRegistry teams, McpServers servers, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

            var server = await servers.Store.GetAsync(id, ct);
            bool removed;
            try
            {
                removed = await servers.Store.UnassignAsync(stored, id, ConnectionEndpoints.Actor(context).Row(
                    TenantActions.TeamMcpServerRemoved, id, server?.Name ?? id,
                    new { team = stored, server = id, name = server?.Name, reason = "removed from the team" }), ct);
            }
            catch (DbException exception)
            {
                return Unrecorded("The MCP server was not taken off the team", exception);
            }

            return removed ? Results.NoContent() : Results.NotFound(new { error = $"MCP server '{server?.Name ?? id}' is not given to team '{stored}'." });
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Take an MCP server off a team")
            .WithDescription(
                "Removes the assignment with a `team.mcp-server-removed` row in one transaction; the entry stays, and other "
                + "teams keep it. 204; 404 when the team did not have it.");
    }

    /// <summary>After an add or an edit: the entry is checked, and the answer is the entry with the check.</summary>
    private static async Task<IResult> SavedAsync(
        McpServers servers, string id, ConnectionActor actor, WorkerAsks asks, McpStdio stdio, int status, CancellationToken ct)
    {
        object? check = null;
        if (await servers.Store.GetAsync(id, ct) is { } saved)
        {
            try
            {
                check = (await servers.CheckAsync(actor, saved, asks, stdio, ct)).View;
            }
            catch (DbException exception)
            {
                check = new { outcome = ConnectionTested.Failed, sentence = $"Saved, but the check's result was not recorded ({exception.Message}).", tools = (string[]?)null, toolCount = (int?)null };
            }
        }

        return Results.Json(new { server = await ViewAsync(servers, id, ct), check }, statusCode: status);
    }

    private sealed record Entry(string Id, object View);

    private static async Task<IReadOnlyList<Entry>> TeamViewsAsync(string team, McpServers servers, CancellationToken ct)
    {
        var assigned = await servers.Store.AssignedAsync(team, ct);
        var views = (await ViewsAsync(servers, ct)).ToDictionary(v => v.Id, StringComparer.Ordinal);

        return [.. assigned
            .Where(a => views.ContainsKey(a.ServerId))
            .Select(a => new Entry(a.ServerId, new { assignedAt = a.AssignedAt, server = views[a.ServerId].View }))];
    }

    private static async Task<object?> ViewAsync(McpServers servers, string id, CancellationToken ct) =>
        (await ViewsAsync(servers, ct)).FirstOrDefault(v => v.Id == id)?.View;

    private static async Task<IReadOnlyList<Entry>> ViewsAsync(McpServers servers, CancellationToken ct)
    {
        var list = await servers.Store.ListAsync(ct);
        var assignments = await servers.Store.AllAssignmentsAsync(ct);
        var names = (await servers.Connections.Store.ListAsync(ct)).ToDictionary(c => c.Id, c => c.Name, StringComparer.Ordinal);

        return [.. list.Select(s => new Entry(s.Id, View(s, assignments[s.Id], names)))];
    }

    /// <summary>One entry as every route shows it: no value, no query string.</summary>
    public static object View(McpServerRecord s, IEnumerable<string> teams, IReadOnlyDictionary<string, string> connectionNames) => new
    {
        id = s.Id,
        name = s.Name,
        kind = s.Kind,
        url = s.UrlShown,
        connection = s.ConnectionId is { } id ? new { id, name = connectionNames.GetValueOrDefault(id) } : null,
        field = s.Field,
        header = s.HeaderName,
        template = s.ValueTemplate,
        command = s.Command,
        package = s.Package,
        version = s.Version,
        args = s.Args,
        env = s.Env.Select(e => new { name = e.Name, connection = e.Connection, connectionName = connectionNames.GetValueOrDefault(e.Connection), field = e.Field }),
        health = s.Status,
        healthReason = s.StatusReason,
        tools = s.Tools,
        toolCount = s.Tools?.Count,
        checkedAt = s.CheckedAt,
        teams,
        createdAt = s.CreatedAt,
        updatedAt = s.UpdatedAt,
    };

    private static async Task<string?> ConnectionNameAsync(McpServers servers, McpServerRecord server, CancellationToken ct) =>
        server.ConnectionId is { } id ? (await servers.Connections.Store.GetAsync(id, ct))?.Name : null;

    private static IResult NoServer(string id) => Results.NotFound(new { error = $"There is no MCP server '{id}'." });

    private static IResult Unrecorded(string what, DbException exception) => Results.Json(
        new { error = $"{what}: its record could not be written ({exception.Message})." },
        statusCode: StatusCodes.Status500InternalServerError);
}

/// <summary>Body of <c>POST /api/teams/{team}/mcp-servers</c>.</summary>
public sealed record AssignTeamMcpServer(string? Server);
