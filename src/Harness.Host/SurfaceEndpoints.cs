using System.Globalization;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;

namespace Harness.Host;

public static class SurfaceEndpoints
{
    public static void Map(WebApplication app, string dataRoot)
    {
        app.MapGet("/api/wip", (WipLedger wip) => Results.Ok(wip.View()))
            .RequirePermit(Permits.Read)
            .WithTags("Admission")
            .WithSummary("Who holds the instance-wide run slots, and who is waiting");

        app.MapGet("/api/agents/auth", async (
                AgentAuthProbe probe, ITeamStore teams, CancellationToken ct) =>
                Results.Ok(AgentAuthReport.MarkReferenced(
                    await probe.ReportsAsync(ct), await AgentReferences.OfAsync(teams, ct))))
            .RequirePermit(Permits.Read)
            .WithTags("Agents")
            .WithSummary("Whether each agent CLI is installed and authenticated")
            .WithDescription(
                "One report per preset. `referenced` is true when a team's member, a team's hiring "
                + "allowlist or the Concierge uses the preset; a warning about sign-in belongs only "
                + "on those.");

        app.MapPut("/api/me/steering", (
            SteeringBody body,
            HttpContext context,
            TeamPaths paths) =>
        {
            if (PrincipalClaims.From(context.User) is not { Kind: PrincipalKind.User } principal)
            {
                return Results.Json(
                    new { error = "A person has to do this." },
                    statusCode: StatusCodes.Status403Forbidden);
            }

            SteeringFile.Write(dataRoot, principal.Id, body.CorrelationId);

            // A running Concierge reads this file. Do not create a workspace to hold it.
            var existing = ConciergeWorkspaces.TryExisting(paths, principal.Id);
            if (existing is not null)
            {
                var causation = body.CorrelationId?.ToString(CultureInfo.InvariantCulture);
                try
                {
                    File.WriteAllText(Path.Combine(existing, "STEERING.md"), SteeringFile.Note(causation));
                }
                catch (IOException)
                {
                    // The data-root copy still stands for the next launch.
                }
            }

            return Results.NoContent();
        })
            .HumansOnly()
            .WithTags("Concierge")
            .WithSummary("Point the Concierge at the workflow the person is looking at");
    }

    public sealed record SteeringBody(long? CorrelationId);
}
