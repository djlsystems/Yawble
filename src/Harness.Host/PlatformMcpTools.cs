using System.ComponentModel;
using System.Net.Http.Json;
using Harness.Contracts;
using Harness.Host.Auth;
using ModelContextProtocol.Server;

namespace Harness.Host;

/// <summary>
/// The agent-facing tools. Each one calls the existing HTTP route with the caller's own API key,
/// so the route's checks stay the only implementation of tell, progress, and the rest.
/// </summary>
[McpServerToolType]
public sealed partial class PlatformMcpTools(
    IHttpContextAccessor http, IPrincipalStore principals, AgentCatalog catalog, IHttpClientFactory clients)
{
    /// <summary>
    /// The named client every tool calls the platform through. Its base address is
    /// <see cref="MemberBaseAddress"/> - the address this Host told its members to call - and
    /// never the request's <c>Host</c> header, which the caller writes.
    /// </summary>
    public const string ClientName = "platform-mcp";

    [McpServerTool(Name = "skills_get"), Description(
        "Load a skill by name. Your system prompt lists the skills offered to your role; a skill "
        + "outside your role is refused with a sentence naming the role. Do not fetch an /api URL "
        + "yourself if this returns an error.")]
    public Task<string> SkillsGet(
        [Description("The skill name, such as manager, member, concierge, or worktrees.")] string name,
        CancellationToken cancellationToken = default) =>
        // One route for every caller: the server reads the caller's role from its credential, so
        // there is no team to name and no second path to choose between.
        SendAsync(
            HttpMethod.Get,
            $"/api/me/skills/{Uri.EscapeDataString(name.Trim())}",
            null,
            cancellationToken);

    [McpServerTool(Name = "skills_search"), Description(
        "Find skills offered to your role by text: name, description and body are searched. Each "
        + "result names a skill to load with skills_get. Omit text to list every skill offered to you.")]
    public Task<string> SkillsSearch(
        [Description("Words to search for, such as browser, credentials or wrap up.")] string? text = null,
        CancellationToken cancellationToken = default) =>
        SendAsync(
            HttpMethod.Get,
            string.IsNullOrWhiteSpace(text)
                ? "/api/me/skills"
                : $"/api/me/skills?q={Uri.EscapeDataString(text.Trim())}",
            null,
            cancellationToken);

    [McpServerTool(Name = "tell"), Description(
        "Tell a member to do something. Pass causation from HARNESS_CAUSATION, or from STEERING.md "
        + "when you are the Concierge and the person is looking at a workflow, so this joins that "
        + "workflow instead of starting a new one. A container's team is its own; a Concierge must pass team.")]
    public async Task<string> Tell(
        [Description("The member to address. The manager is named Manager unless relabelled.")] string member,
        [Description("The full instruction.")] string instruction,
        [Description("Team id. Required for a Concierge. Omit for a member of a team.")] string? team = null,
        [Description("Optional card title, at most 80 characters.")] string? subject = null,
        [Description("Message seq this instruction answers. A team member that omits it dispatches inside the run it is in; a Concierge that omits it starts a new workflow.")] string? causation = null,
        [Description("Optional planned card id this instruction claims.")] string? card = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await TeamAsync(team, cancellationToken);
        if (resolved is null) return "Refused: name a team. A Concierge has no default team.";

        return await SendAsync(
            HttpMethod.Post,
            $"/api/teams/{Uri.EscapeDataString(resolved)}/containers/{Uri.EscapeDataString(member)}/tell",
            new { instruction, subject, causation, card },
            cancellationToken);
    }

    [McpServerTool(Name = "progress"), Description(
        "Say what you are doing right now. This resets your idle clock. Call it while you work.")]
    public Task<string> Progress(
        [Description("One short line of current state. It replaces the previous line.")] string status,
        CancellationToken cancellationToken = default) =>
        OwnAsync("progress", new { status }, cancellationToken);

    [McpServerTool(Name = "blocked"), Description("Stop without finishing, and say why. Use this instead of exiting quietly.")]
    public Task<string> Blocked(
        [Description("Why you stopped, in one or two sentences.")] string reason,
        CancellationToken cancellationToken = default) =>
        OwnAsync("blocked", new { reason }, cancellationToken);

    [McpServerTool(Name = "handback"), Description("Hand finished work back to your manager.")]
    public Task<string> Handback(
        [Description("What you finished, in one short sentence.")] string delivered,
        CancellationToken cancellationToken = default) =>
        OwnAsync("handback", new { delivered }, cancellationToken);

    [McpServerTool(Name = "needs_decision"), Description("Ask for a decision you cannot make yourself.")]
    public Task<string> NeedsDecision(
        [Description("The question that needs an answer.")] string question,
        CancellationToken cancellationToken = default) =>
        OwnAsync("needs-decision", new { question }, cancellationToken);

    [McpServerTool(Name = "workflow_complete"), Description(
        "Declare the workflow you own complete. Refused while that workflow still has work in "
        + "flight, and while any of its cards is unfinished (to do, running, interrupted, failed or "
        + "blocked); the refusal names them. Finish or re-send those cards, or pass dropped.")]
    public Task<string> WorkflowComplete(
        [Description("What the workflow delivered, in one short sentence.")] string delivered,
        [Description("Only when leaving cards unfinished on purpose: which, and why. Recorded on the declaration.")]
        string? dropped = null,
        CancellationToken cancellationToken = default) =>
        OwnAsync("workflow-complete", new { delivered, dropped }, cancellationToken);

    [McpServerTool(Name = "workflow_show"), Description("Read one workflow, oldest message first.")]
    public Task<string> WorkflowShow(
        [Description("The workflow correlation id.")] long correlationId,
        CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, $"/api/workflows/{correlationId}", null, cancellationToken);

    [McpServerTool(Name = "team_list"), Description("List the teams on this instance.")]
    public Task<string> TeamList(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "/api/teams", null, cancellationToken);

    [McpServerTool(Name = "team_current"), Description(
        "The team the person is looking at in the browser. A proposal, never a team to assume.")]
    public Task<string> TeamCurrent(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "/api/me/current-team", null, cancellationToken);

    [McpServerTool(Name = "team_create"), Description(
        "Create a team. For a Concierge asked to spin up a team for some work: the team arrives "
        + "with its Manager, and you then tell that Manager what to do. Omit agent to use the "
        + "instance's first headless preset. Every Manager and member is told its job by the "
        + "platform; additionalInstructions only adds to that. The reply carries the team id to use "
        + "on tell. With no repos, the team gets a local repository named after it (local:<team id>) "
        + "unless you pass localRepository: false. Name a repository URL only when the person says it "
        + "already exists: a URL that cannot be read refuses the create, nothing is made, and the "
        + "refusal names the choices. Creating it on GitHub and attaching it anyway are the person's; "
        + "offer them a local repository (call again without repos) or ask them to create the remote.")]
    public async Task<string> TeamCreate(
        [Description("The team's name as a person would write it.")] string name,
        [Description("The headless agent preset the Manager and new members run, such as claude-headless or grok-headless. Omit for the first headless preset in the catalog.")]
        string? agent = null,
        [Description("Repository URLs the team clones, https only, that the person says already exist, or local:<name> for one of this instance's local repositories. Omit for a team on its own local repository.")]
        string[]? repos = null,
        [Description(
            "Instructions from the person for this team, appended after the Manager's and members' "
            + "own instructions. Pass only words the person gave you; omit for none.")]
        string? additionalInstructions = null,
        [Description("Pass false for a team with no repository at all. Omitted, a team with no repos gets a local repository named after it.")]
        bool? localRepository = null,
        CancellationToken cancellationToken = default)
    {
        var chosen = string.IsNullOrWhiteSpace(agent)
            ? catalog.Definitions.FirstOrDefault(d => d.Mode == AgentMode.Headless && !d.Hidden && d.Launch.LanguageModel)?.Name
            : agent.Trim();

        if (chosen is null) return "Refused: no headless agent preset exists to run the team's Manager.";

        return await SendAsync(
            HttpMethod.Post,
            "/api/teams",
            new
            {
                name,
                agent = chosen,
                additionalInstructions = string.IsNullOrWhiteSpace(additionalInstructions)
                    ? null
                    : additionalInstructions.Trim(),
                memberAgents = new[] { chosen },
                repos = repos is { Length: > 0 } ? repos : null,
                localRepository = localRepository ?? true,
            },
            cancellationToken);
    }

    [McpServerTool(Name = "wip"), Description("Who holds the instance-wide run slots, and who is waiting.")]
    public Task<string> Wip(CancellationToken cancellationToken = default) =>
        SendAsync(HttpMethod.Get, "/api/wip", null, cancellationToken);

    [McpServerTool(Name = "status"), Description(
        "The roster and what each member is doing, including who they were hired for and why a run "
        + "failed. This is harness status. Do not request /api/overview or /api/teams yourself.")]
    public async Task<string> Status(
        [Description(
            "Omit for the whole roster. The member's identifier, not the spaced label: "
            + "DeveloperRowan, not Developer Rowan.")]
        string? member = null,
        [Description("Team id. Required for a Concierge. Omit for a member of a team.")] string? team = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await TeamAsync(team, cancellationToken);
        if (!string.IsNullOrWhiteSpace(member) && resolved is null)
            return "Refused: name a team. A Concierge has no default team.";

        var roster = await SendAsync(HttpMethod.Get, "/api/overview", null, cancellationToken);
        if (string.IsNullOrWhiteSpace(member) || resolved is null) return roster;

        var tail = await SendAsync(
            HttpMethod.Get,
            "/api/teams/" + Uri.EscapeDataString(resolved)
                + "/containers/" + Uri.EscapeDataString(member.Trim())
                + "/messages?take=20",
            null,
            cancellationToken);

        return roster + Environment.NewLine + Environment.NewLine + tail;
    }

    [McpServerTool(Name = "hiring"), Description(
        "The mix you read before a hire: this team's allowed agents, their tags, and how many "
        + "members already carry each tag. uncoveredTags lists the roles no allowed agent carries. "
        + "plugins lists the plugins installed on this Host - each id, one line, and the skill "
        + "(skills_get) that says how to use it - which the member tool can hire with `plugin`. "
        + "This is harness hiring. Do not GET the hiring URL yourself.")]
    public async Task<string> Hiring(
        [Description("Team id. Required for a Concierge. Omit for a member of a team.")] string? team = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await TeamAsync(team, cancellationToken);
        if (resolved is null) return "Refused: name a team. A Concierge has no default team.";

        return await SendAsync(
            HttpMethod.Get,
            $"/api/teams/{Uri.EscapeDataString(resolved)}/hiring",
            null,
            cancellationToken);
    }

    [McpServerTool(Name = "member"), Description(
        "Hire one member onto your team. This is harness member. You do not choose which agent "
        + "they run. The reply's id is who you tell; the spaced name is what the board shows. "
        + "When the reply carries hiringNotice, no allowed agent had your tag and it says who was "
        + "hired instead. "
        + "To hire an installed PLUGIN instead of an agent, pass `plugin` with its id from the "
        + "hiring tool, its `config` fields, and `secrets` binding each secret it names to a "
        + "LOGICAL KEY a person has already bound on a member of your team - never a value. "
        + "Read the plugin's "
        + "skill (skills_get) first. "
        + "Do not POST /api/teams/.../containers yourself.")]
    public async Task<string> Hire(
        [Description("The name a person would write, for example Developer Rowan.")] string name,
        [Description("Team id. Required for a Concierge. Omit for a member of a team.")] string? team = null,
        [Description("The work tag: developer, tester, or researcher. This is the skill's --for.")]
        string? @for = null,
        [Description(
            "Who they are, as a role, not the task they are about to do. Added after the built-in "
            + "Member prompt. Omit for none.")]
        string? prompt = null,
        [Description("An installed plugin's id, as the hiring tool lists it, to hire that plugin. Omit for an agent.")]
        string? plugin = null,
        [Description("A plugin's configuration: field name to value, as its manifest declares them. Only with `plugin`.")]
        Dictionary<string, System.Text.Json.JsonElement>? config = null,
        [Description(
            "A plugin's secret bindings: each secret it names, to a LOGICAL KEY a person has already "
            + "bound on your team (for example MAILER_TOKEN). Never a secret's value. Only with `plugin`.")]
        Dictionary<string, string>? secrets = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = await TeamAsync(team, cancellationToken);
        if (resolved is null) return "Refused: name a team. A Concierge has no default team.";

        if (string.IsNullOrWhiteSpace(plugin))
        {
            if (config is not null || secrets is not null)
            {
                return "Refused: `config` and `secrets` belong to a plugin member. Pass `plugin` too, or leave them out.";
            }

            return await SendAsync(
                HttpMethod.Post,
                $"/api/teams/{Uri.EscapeDataString(resolved)}/containers",
                new
                {
                    name,
                    @for = string.IsNullOrWhiteSpace(@for) ? null : @for.Trim(),
                    systemPrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim(),
                },
                cancellationToken);
        }

        // A PLUGIN RUNS NO MODEL: it reads no prompt and is not chosen by a work tag.
        if (!string.IsNullOrWhiteSpace(prompt) || !string.IsNullOrWhiteSpace(@for))
        {
            return "Refused: a plugin member has no prompt and no work tag. Leave `prompt` and `for` out.";
        }

        return await SendAsync(
            HttpMethod.Post,
            $"/api/teams/{Uri.EscapeDataString(resolved)}/containers",
            new
            {
                name,
                agent = MemberRef.ForPlugin(plugin.Trim()),
                config,
                secrets,
            },
            cancellationToken);
    }

    private async Task<string> OwnAsync(string action, object body, CancellationToken ct)
    {
        var context = http.HttpContext;
        if (context is null) return "Refused: no request.";

        if (PrincipalClaims.From(context.User) is not { } principal) return "Refused: not signed in.";

        if (!ContainerId.TryParse(principal.Id, out var id))
        {
            return "Refused: this action is the member's own report, and this credential is not a member.";
        }

        return await SendAsync(
            HttpMethod.Post,
            $"/api/teams/{Uri.EscapeDataString(id.Team)}/containers/{Uri.EscapeDataString(id.Name)}/{action}",
            body,
            ct);
    }

    private async Task<string?> TeamAsync(string? requested, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(requested)) return requested.Trim();

        var context = http.HttpContext;
        if (context is null) return null;
        if (PrincipalClaims.From(context.User) is not { } principal) return null;

        return await principals.TeamForAsync(principal.Id, ct);
    }

    private async Task<string> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var context = http.HttpContext;
        if (context is null) return "Refused: no request.";

        if (!context.Request.Headers.TryGetValue(ApiKeyAuthenticationHandler.Header, out var key)
            || string.IsNullOrWhiteSpace(key))
        {
            return "Refused: send the X-Api-Key header. A browser cookie is not a member credential.";
        }

        // From the factory, not one per call: a client per call is a socket per call, left in
        // TIME_WAIT. Not disposed here, for the same reason; the factory owns its handler.
        var client = clients.CreateClient(ClientName);
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ApiKeyAuthenticationHandler.Header, key.ToString());

        if (body is not null) request.Content = JsonContent.Create(body);

        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        return $"HTTP {(int)response.StatusCode}{Environment.NewLine}{text}";
    }
}
