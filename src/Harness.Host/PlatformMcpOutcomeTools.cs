using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host.Auth;
using ModelContextProtocol.Server;

namespace Harness.Host;

public sealed partial class PlatformMcpTools
{
    private const string OutcomeActions = "list, show, set, or propose";

    [McpServerTool(Name = "outcome"), Description(
        "Outcomes: the result a workflow serves. action is " + OutcomeActions + ". list gives the active "
        + "and proposed outcomes, this team's used ones first with their workflow counts; show reads one; "
        + "set links your current workflow to one; propose creates a proposed outcome and links it, or "
        + "links the live one that already has the name. A Manager's and the Concierge's; a link a person "
        + "caused (a dispatch, a trigger, a person's choice) is never changed. This is harness outcome. "
        + "Do not request /api yourself.")]
    public async Task<string> Outcome(
        [Description(OutcomeActions + ".")] string action,
        [Description("Team id. Required for a Concierge. Omit for a member of a team.")] string? team = null,
        [Description("For show and set: the outcome's id, or its exact name.")] string? outcome = null,
        [Description("For propose: the result, named as a result (\"Maintain a current pipeline of qualified job openings\"), never an activity.")]
        string? name = null,
        [Description("For propose: what the result means, in a sentence.")] string? description = null,
        [Description("For set and propose: HARNESS_CAUSATION, the workflow to link. A Manager may omit it for the run it is in.")]
        string? causation = null,
        CancellationToken cancellationToken = default)
    {
        var verb = (action ?? "").Trim().ToLowerInvariant();

        if (verb is not ("list" or "show" or "set" or "propose"))
        {
            return $"Refused: the outcome tool's action is {OutcomeActions}.";
        }

        // NEVER A MEMBER'S: a member works a card and does not decide what the workflow is for.
        if (http.HttpContext is not { } context || PrincipalClaims.From(context.User) is not { } principal)
        {
            return "Refused: the outcome tool needs a signed-in caller.";
        }

        if (!principal.May(Permits.Outcomes))
        {
            return "Refused: the outcome tool is a Manager's and the Concierge's. A member does not choose "
                + "what a workflow is for; say what you are working on and your Manager links it.";
        }

        var resolved = await TeamAsync(team, cancellationToken);
        if (resolved is null) return "Refused: the outcome tool needs a team. A Concierge has no default team.";

        switch (verb)
        {
            case "list":
                return await SendAsync(
                    HttpMethod.Get,
                    "/api/outcomes" + Query(("status", "proposed,active"), ("team", resolved)),
                    null,
                    cancellationToken);

            case "show":
                if (string.IsNullOrWhiteSpace(outcome)) return "Refused: the outcome tool's show needs outcome, its id or exact name.";
                return await ShowOutcomeAsync(outcome.Trim(), cancellationToken);

            case "set":
            {
                if (string.IsNullOrWhiteSpace(outcome)) return "Refused: the outcome tool's set needs outcome, its id or exact name.";
                if (WorkflowOf(principal, causation) is not { } workflow) return NoWorkflow("set");

                return Relay("set", await SendAsync(
                    HttpMethod.Put,
                    $"/api/teams/{Uri.EscapeDataString(resolved)}/workflows/{workflow.ToString(CultureInfo.InvariantCulture)}/outcome",
                    new { outcome = outcome.Trim() },
                    cancellationToken));
            }

            default:
            {
                if (string.IsNullOrWhiteSpace(name)) return "Refused: the outcome tool's propose needs name, the result the work produces.";
                if (WorkflowOf(principal, causation) is not { } workflow) return NoWorkflow("propose");

                return Relay("propose", await SendAsync(
                    HttpMethod.Post,
                    "/api/outcomes/propose",
                    new { name = name.Trim(), description, team = resolved, correlation = workflow },
                    cancellationToken));
            }
        }
    }

    /// <summary>
    /// The workflow set and propose act on, resolved as <c>tell</c> resolves causation: the one named,
    /// or - for a team's container that names none - the run it is in.
    /// </summary>
    private long? WorkflowOf(Principal principal, string? causation)
    {
        if (!string.IsNullOrWhiteSpace(causation))
        {
            return long.TryParse(causation.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var seq) ? seq : null;
        }

        if (principal.Kind != PrincipalKind.Container || !ContainerId.TryParse(principal.Id, out var id)) return null;

        var host = http.HttpContext?.RequestServices?.GetService(typeof(ContainerHost)) as ContainerHost;
        return TellCausation.Resolve(null, principal.Kind, host?.Find(id)?.CurrentCausation) is { } current
            && long.TryParse(current, NumberStyles.Integer, CultureInfo.InvariantCulture, out var running)
                ? running
                : null;
    }

    private static string NoWorkflow(string verb) =>
        $"Refused: the outcome tool's {verb} needs a workflow. Pass causation (HARNESS_CAUSATION, or the "
        + "number in STEERING.md for a Concierge); a Manager may omit it inside the run it is in.";

    /// <summary>The route's answer on success; its sentence, as the tool's refusal, otherwise.</summary>
    private static string Relay(string verb, string answer)
    {
        var split = answer.Split(Environment.NewLine, 2);
        if (split[0].StartsWith("HTTP 2", StringComparison.Ordinal)) return answer;

        var sentence = split.Length == 2 ? split[1] : "";
        try
        {
            using var document = JsonDocument.Parse(sentence);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String)
            {
                sentence = error.GetString()!;
            }
        }
        catch (JsonException)
        {
        }

        return $"Refused: the outcome tool's {verb}: {sentence}";
    }

    private async Task<string> ShowOutcomeAsync(string outcome, CancellationToken ct)
    {
        var byId = await SendAsync(HttpMethod.Get, "/api/outcomes/" + Uri.EscapeDataString(outcome), null, ct);
        if (byId.StartsWith("HTTP 200", StringComparison.Ordinal)) return byId;

        // THE EXACT NAME, among the live ones the list gives.
        var list = await SendAsync(HttpMethod.Get, "/api/outcomes" + Query(("status", "proposed,active")), null, ct);
        var split = list.Split(Environment.NewLine, 2);
        if (split.Length == 2 && split[0] == "HTTP 200")
        {
            using var document = JsonDocument.Parse(split[1]);
            foreach (var item in document.RootElement.GetProperty("outcomes").EnumerateArray())
            {
                if (item.TryGetProperty("name", out var named) && named.GetString() == outcome
                    && item.TryGetProperty("id", out var id))
                {
                    return await SendAsync(HttpMethod.Get, "/api/outcomes/" + Uri.EscapeDataString(id.GetString()!), null, ct);
                }
            }
        }

        return $"Refused: the outcome tool's show: no outcome '{outcome}'. Use list to see the active and proposed ones.";
    }
}
