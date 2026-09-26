using Harness.Contracts;

namespace Harness.Host.Auth;

/// <summary>
/// The Agent catalog: what a person edits and what a machine principal needs a redacted slice of.
///
/// Modelled on UserEndpoints - same refusal envelope (<c>new { error = ... }</c>), same
/// <c>.HumansOnly()</c> marker for the write side. Carries no <c>{team}</c> route value,
/// so TeamGate's rule 1 passes both routes through untouched; the write is gated here instead, the
/// same way UserEndpoints gates account administration.
/// </summary>
public static class AgentEndpoints
{
    private const string Area = "Agents";

    /// <summary>What a catalog save carries: the custom presets. Built-ins come from the build.</summary>
    internal sealed record CatalogSubmission(
        [property: System.ComponentModel.Description(
            "Every CUSTOM Agent this tenant has, in full. REPLACES that list rather than merging "
            + "into it. A built-in preset may be sent back unchanged, and is ignored; one sent back "
            + "changed is refused, because built-in presets change only with the product.")]
        List<AgentDefinition>? Agents);

    private const string PeopleOnly =
        "\n\n**A person's action.** `env` is where an outside Agent's key lives, so the full "
        + "record - including every command line - is a credential surface.";

    public static void Map(WebApplication app, string dataRoot)
    {
        app.MapGet("/api/agents", async (
            HttpContext context, AgentCatalog catalog, ITeamStore teams,
            AgentInstallProbe probe, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } caller) return Results.Unauthorized();

            // Two shapes from one route, deliberately. `env` is where an outside Agent's key lives,
            // so the full record goes to a person only - but a redacted list must reach a machine
            // principal too, because adding a member is gated by TEAM access and its dialog cannot
            // offer a choice it may not read.
            var referenced = await AgentReferences.OfAsync(teams, ct);
            var installations = probe.ProbeAll(catalog.Definitions, referenced);

            if (caller.Kind == PrincipalKind.User)
            {
                // THE PROBE RIDES THIS ROUTE - no new route and no new marker. Its results sit
                // BESIDE the catalog rather than inside each definition, and that is not
                // presentation: `PUT /api/agents` REPLACES the catalog wholesale from a body the
                // Agents dialog composes out of what this handed it, so a field that looks like
                // part of a definition is a field a save tries to write back into agents.json. This
                // is a measurement of the machine, not configuration.
                //
                // The resolved path is disclosed here for the same reason `env` is: this is already
                // a person's screen. The redacted arm below carries the probe WITHOUT
                // `command` or `resolvedPath`.
                // `tagsFromOperator` and `buildTags` ride beside each definition for the same reason:
                // they are not fields of a preset, and a save that sends them back writes nothing.
                var json = context.RequestServices
                    .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
                    .Value.SerializerOptions;

                return Results.Ok(new
                {
                    agents = catalog.Definitions.Select(a => WithTagSource(a, catalog, json)),
                    installations,
                    ignoredTagOverrides = catalog.IgnoredTagOverrides,
                });
            }

            // `name`, `mode`, `hidden` and `tags`, plus the probe without a Host disk path.
            // `env` and a Prompt's TEXT stay off: those are the people-only fields.
            return Results.Ok(new
            {
                agents = catalog.Definitions.Select(a => new
                {
                    a.Name, a.Mode, a.Hidden, a.Tags, a.BuiltIn,
                    tagsFromOperator = catalog.TagsFromOperator(a.Name),
                    buildTags = catalog.BuildTags(a.Name),
                }),
                installations = installations.Select(i => new
                {
                    i.Agent,
                    i.State,
                    i.Referenced,
                    i.Message,
                    i.Install,
                }),
                ignoredTagOverrides = catalog.IgnoredTagOverrides,
            });
        })
            .WithTags(Area)
            .RequirePermit(Permits.Read)
            .WithSummary("The Agent catalog")
            .WithDescription(
                "A person reads the whole definition, command lines and `env` included. A machine "
                + "principal reads `name`, `mode`, `hidden`, `tags` and `builtIn`, plus "
                + "`installations` without `command` or `resolvedPath` - enough to say what is "
                + "ready without disclosing a Host disk path, a command line, or an outside "
                + "Agent's credentials. `env` stays people-only.\n\n"
                + "**`builtIn`** is true for a preset compiled into this build: it is listed "
                + "read-only, and only a custom preset can be added, edited or deleted.\n\n"
                + "**`tags`** of a built-in are the operator's when the tenant setting `agents.tags` "
                + "names it: then `tagsFromOperator` is true, and `buildTags` holds the build's "
                + "tags either way (null for a custom preset). `ignoredTagOverrides` lists the entries "
                + "of `agents.tags` that name no built-in preset, which are ignored.\n\n"
                + "**`installations` is one entry per preset**, "
                + "saying whether the command it names resolves on this machine's PATH. It is "
                + "answered ON DEMAND and cached only for a few seconds, because the recovery for "
                + "'not installed' is to install it and look again - a result cached for the "
                + "process lifetime would go on reporting a problem the person had just fixed.\n\n"
                + "**RESOLUTION IS NOT EXECUTION.** No candidate binary is run: a name is resolved "
                + "to a path and that is all. A command that resolves may still be broken, the "
                + "wrong version, or unauthenticated, and `state` claims none of those. The PATH "
                + "searched is the Host's, which is what launches every agent.\n\n"
                + "`state` is null when the command resolves and `AgentNotInstalled` when it does "
                + "not. It is a distinct fact from a member's `missingAgent`, which means the "
                + "CATALOG has no such entry: that one is fixed here, this one is fixed in a "
                + "terminal.");

        app.MapPut("/api/agents", async (
            CatalogSubmission submitted, AgentCatalog catalog, ITeamStore teams,
            EffectiveSubscriptions effective,
            TenantLogging audit, HttpContext context, CancellationToken ct) =>
        {
            var submittedAgents = submitted.Agents ?? [];

            if (BuiltInRefusalFor(submittedAgents, catalog) is { } builtIn)
            {
                return Results.BadRequest(new { error = builtIn });
            }

            var custom = submittedAgents.Where(a => !AgentCatalogFile.IsBuiltIn(a.Name)).ToList();

            if (ValidityRefusalFor(custom) is { } invalid)
            {
                return Results.BadRequest(new { error = invalid });
            }

            var agents = AgentCatalogFile.BuiltIns().Concat(custom).ToList();

            if (await ReferenceRefusalFor(agents, teams, effective, ct) is { } orphaned)
            {
                return Results.BadRequest(new { error = orphaned });
            }

            AgentCatalogFile.Save(dataRoot, custom);

            catalog.Replace(agents);

            await audit.WriteAsync(
                context, TenantActions.AgentsSaved, null, null,
                new
                {
                    count = custom.Count,
                    names = custom.Select(a => a.Name).ToArray(),
                },
                ct);

            return Results.NoContent();
        })
            .WithTags(Area)
            .HumansOnly()
            .WithSummary("Replace the custom Agents")
            .WithDescription(
                "REPLACES every custom preset - not a per-row editor - so an operator sees exactly "
                + "what will run rather than a diff against what was already there. Built-in presets "
                + "are not stored and cannot be changed: one sent back unchanged is ignored, one sent "
                + "back changed is refused with a sentence, and leaving one out removes nothing.\n\n"
                + "400 for a changed built-in, an illegal or duplicated name, a preset with no launch, "
                + "a launch with no executable or no arguments list, an `env` key beginning "
                + "`HARNESS_` (reserved for the platform), or removing - or RE-MODING - a preset "
                + "a member, a team's new members or a team's Concierge still names: those "
                + "first two need a Headless preset and a team's Concierge needs an "
                + "Interactive one, so changing a preset's mode breaks the reference exactly as "
                + "deleting it would. 204 on success."
                + PeopleOnly);
    }

    /// <summary>
    /// A built-in preset sent back changed. Unchanged is fine - a client may send the whole list
    /// it was given - and compared as the file would store it, because a launch's lists compare by
    /// reference.
    /// </summary>
    /// <para>
    /// Its tags may be the build's or the operator's (<c>agents.tags</c>) - the list this
    /// was handed shows the operator's - and are compared apart from the rest, so a changed tag
    /// is told where tags are changed and anything else is the built-in refusal sentence.
    /// </para>
    private static string? BuiltInRefusalFor(IReadOnlyList<AgentDefinition> submitted, AgentCatalog catalog)
    {
        foreach (var agent in submitted)
        {
            var builtIn = AgentCatalogFile.BuiltIns().FirstOrDefault(
                b => string.Equals(b.Name, agent.Name, StringComparison.OrdinalIgnoreCase));

            if (builtIn is null) continue;

            if (System.Text.Json.JsonSerializer.Serialize(agent with { Name = builtIn.Name, Tags = null }, AgentCatalogFile.JsonOptions)
                != System.Text.Json.JsonSerializer.Serialize(builtIn with { Tags = null }, AgentCatalogFile.JsonOptions))
            {
                return $"'{builtIn.Name}' is a built-in preset. Built-in presets change only with the "
                    + "product, so it cannot be edited here; add a custom preset under another name.";
            }

            var current = catalog.Definition(builtIn.Name)?.Tags ?? [];
            if (!SameTags(agent.Tags, builtIn.Tags) && !SameTags(agent.Tags, current))
            {
                return $"'{builtIn.Name}' is a built-in preset, and its tags are changed through the "
                    + $"{TenantSettings.AgentTagsName} setting rather than by saving the catalog.";
            }
        }

        return null;
    }

    private static bool SameTags(IReadOnlyList<string>? a, IReadOnlyList<string>? b) =>
        (a ?? []).SequenceEqual(b ?? [], StringComparer.OrdinalIgnoreCase);

    /// <summary>A definition as the person's arm sends it, with where its tags came from.</summary>
    private static System.Text.Json.Nodes.JsonNode? WithTagSource(
        AgentDefinition agent, AgentCatalog catalog, System.Text.Json.JsonSerializerOptions json)
    {
        var node = System.Text.Json.JsonSerializer.SerializeToNode(agent, json);

        if (node is System.Text.Json.Nodes.JsonObject entry)
        {
            entry["tagsFromOperator"] = catalog.TagsFromOperator(agent.Name);
            entry["buildTags"] = System.Text.Json.JsonSerializer.SerializeToNode(catalog.BuildTags(agent.Name), json);
        }

        return node;
    }

    private static string? ValidityRefusalFor(IReadOnlyList<AgentDefinition> catalog)
    {

        foreach (var agent in catalog)
        {
            // The same allowlist container names use: an Agent name reaches this file, a database
            // column and a command line.
            if (!ContainerId.IsLegalName(agent.Name))
            {
                return $"'{agent.Name}' is not a legal Agent name.";
            }

            // A `Launch` is required now - a preset with neither kind is impossible by shape - but
            // System.Text.Json does not enforce a non-nullable reference-type constructor parameter
            // at runtime, so a hand-edited file or a raw API call omitting `launch` still deserialises
            // to null here rather than throwing.
            if (agent.Launch is null)
            {
                return $"'{agent.Name}' has no launch, so nothing could ever launch it.";
            }

            // A launch that is PRESENT but half-built. Reachable from a hand-edited agents.json and
            // from a raw API call - the dialog cannot produce either - and it must be caught here,
            // because ProcessAgentRunner iterates Arguments to fill ArgumentList OUTSIDE its try
            // block, so a null list escapes as a container.failed reading "Object reference not set
            // to an instance of an object" with nothing naming the Agent that caused it.
            if (RefusalForLaunch(agent.Name, agent.Launch) is { } launchReason)
            {
                return launchReason;
            }

            // The Host reads this file for a person, so it stays under the agent's home and
            // in a format something renders.
            if (agent.LiveView is { } view && LiveView.Refusal(view) is { } liveReason)
            {
                return $"'{agent.Name}' cannot be saved: {liveReason}.";
            }

            foreach (var key in agent.Env?.Keys ?? [])
            {
                // REFUSED, not dropped. Silently ignoring it looks like it worked, and what it
                // would have done is hand a container another principal's credential.
                if (key.StartsWith("HARNESS_", StringComparison.OrdinalIgnoreCase))
                {
                    return $"'{agent.Name}' sets {key}. HARNESS_ variables belong to the "
                        + "platform and are injected last; a definition cannot set them.";
                }
            }
        }

        var byName = catalog
            .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        if (byName.Count != catalog.Count)
        {
            return "Two Agents share a name. Names are compared without regard to case, because "
                + "that is how a stored member's Agent is looked up.";
        }

        return null;
    }

    /// <summary>
    /// What this catalog would ORPHAN, or null: a member's Agent, a team's allowed Agents for new
    /// members, and the Concierge's Agent. See <see cref="ValidityRefusalFor"/> for why these are not one method.
    /// </summary>
    private static async Task<string?> ReferenceRefusalFor(
        IReadOnlyList<AgentDefinition> catalog,
        ITeamStore teams, EffectiveSubscriptions effective, CancellationToken ct)
    {
        // Rebuilt here rather than handed over: this method is called on its own by the PUT, right
        // after the validity half has already proven there are no duplicates.
        var byName = catalog
            .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        // BOTH references: a team stores which Agent its Concierge runs, so a wholesale replace
        // could strip an Agent from under a team's Concierge while every member check passed.
        //
        // Each reference is checked against the LAUNCH KIND IT NEEDS rather than against the set of
        // names, because narrowing an Agent breaks a reference exactly as removing it does. A check
        // on names alone would let unchecking "Interactive command" on `claude` answer 204, and
        // every team's Concierge would then paint "'claude' has no interactive command".
        // The headless half is worse for being delayed: a live container keeps running off its own
        // per-container entry, which nothing here rewrites, and only breaks after the next restart.
        var withConsoles = await teams.TeamsAsync(ct);
        var labels = withConsoles.ToDictionary(
            t => t.Id, t => t.Name ?? t.Id, StringComparer.OrdinalIgnoreCase);

        var members = await teams.MembersAsync(ct);
        var orphanedMember = members.FirstOrDefault(
            m => byName.GetValueOrDefault(m.Agent) is not { Mode: AgentMode.Headless });

        if (orphanedMember is { } member)
        {
            // By the names a person reads, on both halves: the stored Label is what the board and
            // the member list show, and a qualified `Alpha/Manager` is an identity rather than a
            // label, and showing one here would be wrong.
            var team = labels.GetValueOrDefault(member.Team, member.Team);

            // NAME THE WAY OUT, and note what the way out is: point that member at another Agent
            // from its own settings, then send this again. A refusal that denies the one
            // escape hatch that exists is worse than a terse one: it does not merely fail to help,
            // it talks the reader out of the thing that would have worked.
            return $"'{member.Agent}' is what {member.Label ?? member.Name} in {team} runs, and "
                + "this catalog leaves it with no headless command. Either send it back with its "
                + "command, or point that member at another Agent from its settings first and then "
                + "remove this one.";
        }

        var concierge = await teams.ConciergeSettingsAsync(ct);

        var orphanedHiringAgent = withConsoles
            .SelectMany(team =>
            {
                var allowlist = team.MemberAgents ?? [];

                return allowlist.Select(agent => new { Team = team, Agent = agent });
            })
            .FirstOrDefault(entry =>
                byName.GetValueOrDefault(entry.Agent) is not { Mode: AgentMode.Headless });

        if (orphanedHiringAgent is { } hiringAgentRef)
        {
            // NAME THE WAY OUT, the same rule the five refusals around it follow: this one is fixed
            // under Dynamic members, beside the Prompt whose refusal says the same thing.
            return $"'{hiringAgentRef.Agent}' is in the allowed Agent list for new members of "
                + $"{hiringAgentRef.Team.Name ?? hiringAgentRef.Team.Id}, and this catalog leaves it with no "
                + "headless command. Either send it back with its command, or choose another Agent "
                + "under Dynamic members in that team's settings first and then remove this one.";
        }

        // NULL is nobody having chosen, which references no preset at all.
        if (concierge.Agent is { } conciergeAgent
            && byName.GetValueOrDefault(conciergeAgent) is not { Mode: AgentMode.Interactive })
        {
            return $"'{conciergeAgent}' is still the Concierge, and this catalog leaves it with no "
                + "interactive command. Either send it back with its interactive command, or point "
                + "the Concierge at another one first and then remove this.";
        }

        // THERE WERE TWO MORE REFERENCES HERE - a tenant agent's Agent and a tenant agent's
        // Prompt. They went with `tenant_agents` itself, and nothing is left unchecked by their
        // going: a tenant agent that meant anything is an ordinary team's Manager now, whose Agent
        // and Prompt are exactly the first two references this method already refuses to orphan.

        // THE NINTH CHECK. Flipping a preset to `languageModel: true` strands every high-volume
        // subscription its members hold - no error, no log line, and the runaway is back.
        //
        // It sits WITH the other eight rather than in its own pass, for the reason the hiring Agent
        // check sits beside the hiring Prompt check: two halves of one question checked in two
        // places is how one stays undefended. The REVERSE direction - a language model becoming a
        // program - is never refused here: it takes a subscription away from nothing, and a preset
        // narrowing FROM headless-with-no-command TO a runnable one is never the shape this method
        // refuses.
        foreach (var holder in members)
        {
            if (!byName.TryGetValue(holder.Agent, out var preset)) continue;
            if (preset.Launch is not { LanguageModel: true }) continue;

            // EFFECTIVE SUBSCRIPTIONS, NOT ONLY `holder.Subscribes`. An event trigger contributes a
            // subscription of its own - see `EffectiveSubscriptions` - so a member holding
            // `container.progress` only through a trigger was flippable to `languageModel: true`
            // with nothing refusing it if this loop read the base set alone, because it could not
            // see what the trigger adds.
            var holds = await effective.EffectiveTypesAsync(
                new ContainerId(holder.Team, holder.Name), holder.Subscribes, ct);

            foreach (var type in holds)
            {
                if (EventCatalog.HighVolumeTypes.Contains(type))
                {
                    var team = labels.GetValueOrDefault(holder.Team, holder.Team);

                    return $"'{holder.Agent}' cannot become a language model: "
                        + $"{holder.Label ?? holder.Name} in {team} subscribes to '{type}', which "
                        + "is high-volume. Remove that subscription first.";
                }
            }
        }

        return null;
    }

    /// <summary>A launch that is present but unusable, or null when it is good.</summary>
    private static string? RefusalForLaunch(string agent, AgentLaunch launch)
    {
        if (string.IsNullOrWhiteSpace(launch.FileName))
        {
            return $"'{agent}' has a launch with no executable to run.";
        }

        // fileName must be a NAME, never a rooted path. The operator whose CLI cannot go on PATH
        // uses a shim that is on PATH. A slash makes it a path; backslash and colon are refused
        // too, so a catalog entry carrying a drive-letter path is refused rather than looked up.
        if (launch.FileName.IndexOfAny(['\\', '/', ':']) >= 0)
        {
            return $"'{agent}' has a rooted file name {launch.FileName}. The executable must be a "
                + "name on PATH, not a rooted path. Add a shim on PATH if the executable is somewhere "
                + "else.";
        }

        // Null rather than empty. `"launch": { "fileName": "claude" }` deserialises to exactly
        // this, and an Agent that takes no arguments is a real thing - so the fix is an empty list,
        // not removing the field.
        if (launch.Arguments is null)
        {
            return $"'{agent}' has a launch with no arguments list. Send an empty list rather "
                + "than omitting it.";
        }

        return null;
    }
}
