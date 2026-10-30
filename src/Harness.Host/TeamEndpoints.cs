using System.ComponentModel;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.SignalR;
using ModelContextProtocol.AspNetCore;
using Harness.Containers;
using Harness.Backlog;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Harness.Host.Capacity;
using Harness.Host.Solutions;
using Harness.Identity;
using Harness.Kanban;
using Harness.Messaging;
using Harness.Skills;

namespace Harness.Host;

internal static class TeamEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/teams", async (
            CreateTeam request, TeamRegistry teams, TenantLogging audit, HttpContext context,
            AgentInstallProbe probe, AgentCatalog catalog, TeamListPush listPush, TeamRepoSetup repoSetup,
            DetachedWork detached, Connections connections, CancellationToken ct) =>
        {
            var name = (request.Name ?? "").Trim();

            // WHICH CONNECTIONS THE TEAM MAY USE is a person's choice: an agent's create naming any is
            // refused, and every id must name a connection, before anything is created.
            var assign = (request.Connections ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim())
                .Distinct(StringComparer.Ordinal).ToList();
            if (assign.Count > 0)
            {
                if (PrincipalClaims.From(context.User) is { Kind: not PrincipalKind.User })
                {
                    return Results.Json(
                        new { error = "Only a person assigns a connection to a team. Create the team without `connections`; a person assigns them in Team settings, Connections." },
                        statusCode: StatusCodes.Status403Forbidden);
                }

                foreach (var id in assign)
                {
                    if (await connections.Store.GetAsync(id, ct) is null) return Results.BadRequest(new { error = $"There is no connection '{id}'." });
                }
            }

            if (name.Length == 0) return Results.BadRequest(new { error = "A team needs a name." });

            if (name.Length > TeamRegistry.MaximumLabelLength)
            {
                return Results.BadRequest(new
                {
                    error = $"A team name cannot be longer than {TeamRegistry.MaximumLabelLength} characters.",
                });
            }

            // REFUSED ON THIS ROUTE TOO, not only on `PUT /api/teams/{team}/budget`: a caller that can
            // reach one can reach the other, so refusing only on the setter would close one instance
            // rather than the class. Checked BEFORE `CreateAsync`, beside the name
            // checks above - a refused create must leave nothing behind, where applying the value only
            // AFTER creation would mean a team already exists by the time a negative figure is found.
            if (request.BudgetTokens is < 0)
            {
                return Results.BadRequest(new
                {
                    error = "budgetTokens cannot be negative. Use 0 for unlimited, or omit it to use the "
                        + "instance figure.",
                });
            }

            // EVERY URL IS READ BEFORE ANYTHING IS CREATED: one `git ls-remote` cannot read is
            // refused with the choices this caller may take, and no team, row, folder or clone is left.
            // What can be refused without the network is refused first: a URL or upstream that is not one,
            // with the 400 it always had. The same path as backlog dispatch-to-new.
            NewTeamRepos newRepos;
            try
            {
                newRepos = await repoSetup.PlanNewTeamAsync(
                    request.Repos, request.Upstreams, request.RepoChoices,
                    person: PrincipalClaims.From(context.User) is { Kind: PrincipalKind.User }, request.LocalRepository, ct);
            }
            catch (RepoSetupRefusedException refused)
            {
                return Results.Json(refused.Body, statusCode: refused.Status);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            // WHO ACTED, READ NOW: the create below outlives the request when its caller goes away, and
            // nothing in it may read the request after that.
            var (actorId, actorEmail) = ActorClaims.Of(context.User);

            try
            {
                // FROM HERE THE CREATE RUNS TO COMPLETION ON THE HOST'S LIFETIME, not the request's. A large
                // clone outlasts a browser; stopping at the abort left a team with an empty clone and no
                // `team.created` row. A create that fails still undoes what it made, as it always did.
                var (created, retiredDocuments) = await detached.RunAsync("Creating a team", async work =>
                {
                    var teamCreated = false;
                    try
                    {
                        var made = await teams.CreateAsync(
                            // VERBATIM, no `??`. Both defaults named a catalog entry a person may rename,
                            // and `CreateAsync` refuses a blank one by name rather than substituting anything.
                            name, request.Agent ?? "",
                            request.AdditionalInstructions,
                            request.MemberAgent, request.MemberAgents, request.Root, newRepos.Repos,

                            // CARRIED IN THIS SAME REQUEST rather than by a client-side follow-up call to the PUT
                            // route. A create that answered 201 and then failed its second call would leave a team
                            // running under a figure nobody chose, for the window between them - and the window is
                            // exactly when the Manager's first wake happens.
                            budgetTokens: request.BudgetTokens,
                            ct: work,
                            upstreams: request.Upstreams,

                            // ONE UNIT WITH THE TEAM: made once every check on the team has passed and before
                            // anything of it is written, so a repository that cannot be made refuses the create.
                            addRepo: newRepos.AddRepoAsync);
                        teamCreated = true;

                        await newRepos.LogAsync(audit, actorId, actorEmail, made.Id, work);

                        // THE DISPLACED DOCUMENTS FOLDER, WHEN THERE WAS ONE, and this row is what makes
                        // "reported, not discovered" durable. Team ids are reusable and documents outlive their
                        // team, so a team created with a dead team's identifier meets its predecessor's folder;
                        // the create moves that folder aside rather than handing it over, and the only way anybody
                        // finds out later is a row that says so.
                        var retired = teams.RetiredDocumentsFor(made.Id);

                        await audit.WriteAsAsync(
                            actorId, actorEmail, TenantActions.TeamCreated, made.Id, made.Name,
                            new
                            {
                                manager = made.Containers.FirstOrDefault()?.Agent,
                                Concierge = made.Concierge,
                                retiredDocuments = retired,
                                localRepository = newRepos.LocalRepository is not { } local
                                    ? null
                                    : new { name = local.Name, reference = local.Reference, created = local.Created },
                                createdOnGitHub = newRepos.CreatedOnGitHub.Count == 0 ? null : newRepos.CreatedOnGitHub,
                            },
                            work);

                        // THE CONNECTIONS THE PERSON CHOSE in New team → Connections, each with its row.
                        foreach (var id in assign)
                        {
                            var (_, assignError) = await TeamConnectionEndpoints.AssignAsync(
                                made.Id, id, "new team", new ConnectionActor(actorId ?? "", actorEmail), connections, work);
                            if (assignError is not null) throw new InvalidOperationException(assignError);
                        }

                        await listPush.AnnounceCreatedAsync(made.Id, work);
                        return (made, retired);
                    }
                    finally
                    {
                        await newRepos.ForgetUnlessCreatedAsync(teamCreated);
                    }
                }, ct);

                var localRepository = newRepos.LocalRepository;
                var createdOnGitHub = newRepos.CreatedOnGitHub;

                var unresolvedList = new List<object>();

                // Probe manager (in Containers)
                foreach (var container in created.Containers)
                {
                    var definition = catalog.Definition(container.Agent);
                    if (definition is not null)
                    {
                        var installation = probe.Probe(definition);
                        if (installation.State == AgentInstallStates.NotInstalled)
                        {
                            unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                        }
                    }
                }

                // Probe the Concierge -- WHICH THIS TEAM DID NOT CHOOSE. It is the tenant's, one for the
                // whole instance, and the summary carries it because that is what this team will open. Worth
                // probing all the same: the answer is about the machine, and the moment a person is looking
                // at a team they just made is a reasonable moment to learn the door is not installed.
                if (!string.IsNullOrEmpty(created.Concierge))
                {
                    var definition = catalog.Definition(created.Concierge);
                    if (definition is not null)
                    {
                        var installation = probe.Probe(definition);
                        if (installation.State == AgentInstallStates.NotInstalled)
                        {
                            unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                        }
                    }
                }

                // Probe member-agents allowlist
                if (created.MemberAgents is not null)
                {
                    foreach (var agent in created.MemberAgents)
                    {
                        var definition = catalog.Definition(agent);
                        if (definition is not null)
                        {
                            var installation = probe.Probe(definition);
                            if (installation.State == AgentInstallStates.NotInstalled)
                            {
                                unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                            }
                        }
                    }
                }

                // Serialize created to JsonNode and add unresolvedAgents
                var node = JsonSerializer.SerializeToNode(created, HostJson.WebEnums);
                if (node is JsonObject obj)
                {
                    obj["unresolvedAgents"] = JsonSerializer.SerializeToNode(unresolvedList, HostJson.WebEnums);

                    // Present only when something WAS displaced, so a client can say it once rather than
                    // render an empty row on every create. See the tenant log line above for why it is
                    // reported at all.
                    if (retiredDocuments is not null) obj["retiredDocuments"] = retiredDocuments;

                    if (localRepository is not null)
                    {
                        obj["localRepository"] = new JsonObject
                        {
                            ["name"] = localRepository.Name,
                            ["reference"] = localRepository.Reference,
                            ["created"] = localRepository.Created,
                        };
                    }

                    if (createdOnGitHub.Count > 0) obj["createdOnGitHub"] = new JsonArray([.. createdOnGitHub.Select(u => JsonValue.Create(u))]);
                }
                return Results.Json(node, statusCode: 200);
            }
            catch (RepoSetupRefusedException refused)
            {
                return Results.Json(refused.Body, statusCode: refused.Status);
            }
            catch (TeamNameTakenException taken)
            {
                // 409 naming the existing team by its LABEL. Two different names can derive to one
                // identifier ("Platform Engineering" and "Platform-Engineering" both give
                // `PlatformEngineering`), and that ambiguity is refused rather than resolved with a
                // suffix - two teams a person cannot tell apart is worse than being asked for another name.
                return Results.Conflict(new { error = taken.Message });
            }
            catch (NoSuchAgentException exception)
            {
                // CreateAsync builds the team's Manager THROUGH AddContainerAsync, so an unknown agent here
                // is the same refusal a member create meets - without this catch it would surface as an
                // unmapped 500 rather than a 400 naming the agent.
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        })
            // Takes its team from the BODY, so TeamGate cannot see it - this route is covered only by this
            // filter. The standing rule for any NEW team-scoped route is to take its team from the path
            // instead, so TeamGate covers it by existing rather than needing its own filter.
            //
            // Team creation authority is inherited, never held: a machine principal may create only while
            // acting as an owner who still exists. The permit names the verb; this filter binds whose
            // authority is allowed to exercise it.
            .RequireTeamCreationAuthority()
            .WithTags("Teams")
            .RequirePermit(Permits.CreateTeam)
            .WithSummary("Create a team")
            .WithDescription(
                "Creates a team and, with it, a Manager container - so a team never exists without a door "
                + "into it - and a documents folder on disk.\n\n"
                + "**Nobody types an identifier.** Send the name a person would say; the identifier every "
                + "other route addresses is derived from it here and is never asked for. Derivation is "
                + "lossy on purpose: \"R&D / Tooling\" becomes `RDTooling`, and a label with no usable "
                + "ASCII at all gets a generated identifier rather than a refusal, because refusing a name "
                + "for its script would be the machinery leaking out.\n\n"
                + "Hyphens and underscores survive derivation, so \"Platform-Engineering\" and \"Platform "
                + "Engineering\" are different teams while \"Platform / Engineering\" collides with the "
                + "latter. A collision is refused with 409 naming the existing team, never resolved with a "
                + "suffix - two teams a person cannot tell apart is worse than being asked for another "
                + "name. 400 for an empty or over-long name.\n\n"
                + "Requires team-creation authority: a person directly, or a machine principal acting "
                + "for an owner who still exists.\n\n"
                + "**Repositories.** Every URL in `repos` is read with `git ls-remote` first (a github.com "
                + "URL with `GH_TOKEN`, as Fetch). One that cannot be read is refused with 422 `{ error, code: "
                + "\"repo-check-failed\", repos: [{ url, failure, reason, choices }] }` and nothing is created; send "
                + "it again with `repoChoices` naming a choice per URL: `use-local`, and for a person only "
                + "`create-on-github` and `attach-anyway` (network failure only). An agent sending a person's "
                + "choice is refused with 403. A team left with no repository gets none: its local repository is "
                + "made when its work first needs it. With `localRepository: true` it gets one now, named after "
                + "it, `local:<team id>` (an unused one of that name is reused; `-2`, `-3` when a team uses it); "
                + "when it cannot be made, no team is created.\n\n"
                + "so a refused value leaves nothing behind.");

        // A NEW TEAM CARRYING AN EXISTING ONE'S CONFIGURATION.
        //
        // SERVER-SIDE, AND THAT IS THE DESIGN RATHER THAN A CONVENIENCE. Two of the things a clone must
        // carry are not available to a browser at all: the stored `teams.root` PARENT is never sent -
        // `TeamSummary.Root` is the RESOLVED team folder and is filled only for a person - and
        // `DeriveName` lives in C#. A dialog doing this by calling `POST /api/teams` could therefore only
        // ever drop a placed team's location silently, and would need a second copy of the derivation rule
        // to pick a free identifier: the id-versus-name defect this codebase has a branded `TeamId` type to
        // prevent, re-entered from the other side.
        //
        // Same authority as creation, because that is what it does. `{team}` is in the template so TeamGate
        // covers the SOURCE structurally, and the creation-authority filter is what says the caller may
        // make a team at all.
        app.MapPost("/api/teams/{team}/clone", async (
            [Description(Describe.Team)] string team, CloneTeam request, TeamRegistry teams,
            TenantLogging audit, TeamListPush listPush, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            var name = (request.Name ?? "").Trim();

            if (name.Length == 0) return Results.BadRequest(new { error = "A team needs a name." });

            if (name.Length > TeamRegistry.MaximumLabelLength)
            {
                return Results.BadRequest(new
                {
                    error = $"A team name cannot be longer than {TeamRegistry.MaximumLabelLength} characters.",
                });
            }

            try
            {
                var done = await teams.CloneAsync(stored, name, ct);
                var sourceSummary = teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase));
                var sourceManager = sourceSummary.Containers.Single(
                    c => string.Equals(c.Id, TeamRegistry.DefaultManagerName, StringComparison.OrdinalIgnoreCase));
                var carriedEnvKeys = teams.EnvFor(stored)
                    .Keys
                    .Where(key => key != TeamEnv.AdminEmail && key != TeamEnv.AdminPassword)
                    .OrderBy(key => key, StringComparer.Ordinal)
                    .ToArray();

                // THE FAILURE COUNT BELONGS HERE, or a log asked "did this clone come out whole" cannot
                // answer - a row recording only what was carried reads identically for a clone that carried
                // everything and one that lost half its roster.
                //
                // SAFE VALUES ONLY. This table is kept forever and read by every person, so the
                // carried env is named by KEY only - never by value - while the other travelled settings
                // can be recorded verbatim. Failures stay a COUNT, never the raw exception text, for the
                // same reason: this row must never promise that arbitrary step output contains no secret.
                await audit.WriteAsync(
                    context, TenantActions.TeamCloned, done.Team.Id, done.Team.Name,
                    new
                    {
                        source = stored,
                        repos = done.Repos,
                        envKeys = done.EnvKeys,
                        members = done.Members,
                        failures = done.Failures.Count,
                        carried = new
                        {
                            agent = sourceManager.Agent,
                            additionalInstructions = done.Team.AdditionalInstructions is not null,
                            memberAgentAllowlist = done.Team.MemberAgents,
                            repos = done.Team.Repos,

                            // The source's CHOICE and not the figure in force for it - see `CloneAsync`.
                            // A clone of a team that chose nothing goes on tracking the instance figure
                            // rather than being frozen at today's value of it.
                            budgetTokens = done.Team.BudgetTokens,
                            env = done.EnvKeys > 0 ? carriedEnvKeys : Array.Empty<string>(),
                            roster = done.Team.Containers
                                .Select(container => new
                                {
                                    id = container.Id,
                                    name = container.Name,
                                    agent = container.Agent,
                                    hired_for = container.HiredFor,
                                })
                                .ToArray(),
                        },
                        notCarried = new
                        {
                            paused = sourceSummary.Paused,
                            adminCredentials = "minted fresh",
                        },
                    },
                    ct);

                // EVERY OPEN WINDOW HEARS OF THE CLONE, as of any other new team.
                await listPush.AnnounceCreatedAsync(done.Team.Id, ct);

                return Results.Created($"/api/teams/{done.Team.Id}", new
                {
                    team = done.Team,
                    repos = done.Repos,
                    envKeys = done.EnvKeys,
                    members = done.Members,
                    failures = done.Failures,
                });
            }
            catch (TeamNameTakenException taken)
            {
                // 409 NAMING THE EXISTING TEAM BY ITS LABEL, exactly as creation refuses: a collision is on
                // the DERIVED IDENTIFIER, which is why a name that looks free can still be refused, and the
                // person needs to be told which team they already have rather than that they typed
                // something invalid.
                //
                // The three arms below are unrelated sealed types with no `catch (InvalidOperationException)`
                // beneath them, so their ORDER carries nothing and none of them shadows another. Adding a
                // broad arm is what would make that untrue.
                return Results.Conflict(new { error = taken.Message, existing = taken.ExistingLabel });
            }
            catch (NoSuchAgentException agent)
            {
                return Results.BadRequest(new { error = agent.Message, available = agent.Available });
            }
            catch (ArgumentException exception)
            {
                // A SOURCE THAT HAS CHOSEN NOTHING CANNOT BE CLONED INTO A VALID TEAM, and this is where
                // that becomes a sentence rather than a 500. `CreateAsync` refuses a null manager Prompt,
                // member Prompt or member Agent BY NAME - null means nobody has chosen, an error condition
                // and not a default - and `CloneAsync` deliberately passes the source's nulls straight
                // through rather than manufacturing a reference nothing wrote down. The exception's own
                // message names the setting; a 500 would read as a platform fault over a team that is
                // simply not finished.
                //
                // IT ALSO CARRIES THE REFUSAL FOR A SOURCE WITH NO MANAGER AT ALL - a reachable state, and
                // one whose sentence has to name the MANAGER. Falling through to `CreateAsync`'s
                // empty-agent guard would answer "A team needs an Agent for its manager", which reads as a
                // field the caller left blank on a request that has no such field.
                //
                // It also catches a root the allowlist does not permit any more, which is the one moment a placed
                // root is re-examined at all.
                return Results.BadRequest(new { error = exception.Message });
            }
        })
            // BOTH MARKERS, exactly as `POST /api/teams` carries both, and they are different kinds. The
            // permit names the VERB; the filter binds whose authority may exercise it.
            .RequireTeamCreationAuthority()
            .WithTags("Teams")
            .RequirePermit(Permits.CreateTeam)
            .WithSummary("Clone a team")
            .WithDescription(
                "Creates a new team carrying this one's configuration: its manager Agent, prompts, member "
                + "allowlist, repository URLs, `env` except the seeded admin credentials, its "
                + "roster - each member's Agent, Prompt, added instructions and the tag it was hired for - "
                + "and its roster.\n\n"
                + "**Files are not copied.** The clone gets an empty skeleton and its own Manager clones "
                + "the repositories into it. **History is not copied** either - a clone's members floor at "
                + "the head of the message log like any new container, so it starts work rather than "
                + "remembering somebody else's.\n\n"
                + "The seeded `TEST_ADMIN_EMAIL` and `TEST_ADMIN_PASSWORD` are the CLONE'S OWN and are not "
                + "carried: every team mints a per-team address and a fresh random password at creation, "
                + "and copying them would hand two teams one credential under an address naming the wrong "
                + "team. `paused` is not carried either: the clone starts unpaused regardless of the "
                + "source's queue state.\n\n"
                + "**The name is checked as an IDENTIFIER.** Derivation is lossy, so \"Alpha 2\" and "
                + "\"Alpha  2\" are one team; a collision is 409 naming the existing team. 400 for an "
                + "empty or over-long name, and 400 naming the setting when the SOURCE has chosen no "
                + "Prompt or no Agent for its members - a team that could not be created by hand cannot be "
                + "cloned into one either.\n\n"
                + "Answers 201 with what it carried and a `failures` list naming any member it could not "
                + "hire. It does NOT roll back: a reported half-clone is recoverable, where an automatic "
                + "rollback would run the most destructive operation in the product unattended, on a team "
                + "a person can already see.\n\n"
                + "Requires team-creation authority.");

        // Renames the LABEL and nothing else. The identifier stays fixed for the reasons TeamSummary
        // states: it keys the documents folder on disk and half of every container's identity, and
        // moving both atomically is a different job. So this route deliberately has no
        // way to express "change the id" - there is one field and it is the display name.
        //
        // People only: a console child agent has no business renaming the team it was handed. It carries
        // {team} in its template, so TeamGate covers it as well.
        // Deleting a team, and everything that names it. PEOPLE ONLY: this removes files, and no agent
        // credential is consent to destroy a team. Refused unless the team is quiet (TeamArchive), so it
        // never ends running work.
        //
        // The ORDER the work happens in is TeamDeletion's, and its doc comment is where the reasoning lives.
        // This route's job is authority, the 404, and reporting what actually went.
        app.MapDelete("/api/teams/{team}", async (
            [Description(Describe.Team)] string team,
            [Description(
                "Exact confirmation string required only when deletion would discard commits that are on "
                + "no remote.")]
            string? confirm,
            [Description(
                "A `local:<name>` repository of this team's to delete after the team, repeated for each one "
                + "the person ticked. Only a local repository the team uses; a URL is refused with 400.")]
            string[]? deleteLocalRepository,
            TeamDeletion deletion, LocalRepoDeletion localRepoDeletion, TenantLogging audit, ITenantLog tenantLog,
            TeamRegistry teams, TeamListPush listPush, TeamArchive archive,
            HttpContext context,
            CancellationToken ct) =>
        {
            // Null means no such team, which is a 404 rather than a failure: asking twice is not a fault,
            // and a caller retrying after a timeout should not be told something broke.
            // The LABEL, read before the deletion - afterwards there is nothing left to read it from, and
            // an audit row naming an identifier a person never saw is a row they cannot match to what they
            // did.
            var label = teams.ExistingName(team) is { } present ? teams.LabelFor(present) : team;
            var viewers = teams.ExistingName(team) is null
                ? Array.Empty<string>()
                : (await listPush.EntitledViewerIdsAsync(team, ct)).ToArray();

            // KEPT, NEVER DELETED WITH THE TEAM: its local repositories live under
            // `<dataRoot>/repos`, outside the team's root, and show as unused in Admin -> Repositories,
            // where a person may delete them. Read before the deletion, which forgets the list.
            var localRepositories = teams.ExistingName(team) is { } named
                ? teams.ReposFor(named).Where(LocalRepos.IsLocal).Select(r => r.Trim()).ToArray()
                : [];

            // DELETED WITH THE TEAM ONLY WHEN TICKED: each a `local:<name>` this team uses. Anything
            // else - a URL, another team's repository - is refused before a single thing is deleted.
            var ticked = new List<string>();
            foreach (var asked in deleteLocalRepository ?? [])
            {
                if (localRepositories.FirstOrDefault(r => string.Equals(r, asked?.Trim(), StringComparison.Ordinal)) is not { } own)
                {
                    return Results.BadRequest(new
                    {
                        error = $"'{asked}' is not a local repository this team uses, so nothing was deleted. Only a "
                            + "team's own local:<name> repositories can be deleted with it.",
                    });
                }

                if (!ticked.Contains(own, StringComparer.Ordinal)) ticked.Add(own);
            }

            // QUIET FIRST, for an active and an archived team alike: a delete never stops running work. A
            // member with a run going or an instruction accepted and not started refuses it with the same
            // sentence an archive is refused with, before anything is written or removed.
            if (teams.ExistingName(team) is { } quietCheck && await archive.NotQuietAsync(quietCheck, ct) is { } busy)
            {
                return Results.Conflict(new { error = busy });
            }

            TeamDeleted? removed;
            try
            {
                // TEAM.DELETING COMES FIRST, and is not swallowed: it is written after the confirmation
                // refusal and before anything is removed, so a row that cannot be written leaves the whole
                // team in place. The team.deleted row after names what went and what remains.
                removed = await deletion.DeleteAsync(team, confirm, ct, (plan, token) => tenantLog.WriteAsync(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                    context.User.FindFirstValue(ClaimTypes.Email),
                    TenantActions.TeamDeleting, plan.Team, label,
                    JsonSerializer.Serialize(new
                    {
                        containers = plan.Containers,
                        root = plan.Root,
                        deleteLocalRepositories = ticked,
                        confirmed = !string.IsNullOrWhiteSpace(confirm),
                    }, JsonSerializerOptions.Web),
                    token));
            }
            catch (TeamDeletionNotRecordedException unrecorded)
            {
                return Results.Json(new { error = unrecorded.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
            catch (TeamDeletionConfirmationRequiredException refusal)
            {
                return Results.Conflict(new
                {
                    error = refusal.Message,
                    confirmation = refusal.Confirmation,
                    losses = refusal.Losses,
                });
            }

            if (removed is null) return Refusals.NoTeam(team);

            // AFTER THE TEAM, and through the one delete Admin -> Repositories uses: its own
            // `local-repo.deleted` row first, the same refusal while another team uses it. One that fails
            // leaves the team deleted - never the reverse - and is named with its reason and kept.
            var localRepositoriesDeleted = new List<string>();
            var localRepositoriesKept = new List<string>();
            var localRepositoryFailures = new List<LocalRepositoryNotDeleted>();
            foreach (var reference in localRepositories)
            {
                if (!ticked.Contains(reference, StringComparer.Ordinal))
                {
                    localRepositoriesKept.Add(reference);
                    continue;
                }

                var result = await localRepoDeletion.DeleteAsync(LocalRepos.NameOf(reference), context.User, ct);
                if (result.Deleted)
                {
                    localRepositoriesDeleted.Add(reference);
                    continue;
                }

                // Incomplete: its name is gone, so it is not kept, and what is left is retried, not
                // deleted again from Admin -> Repositories.
                if (result.Outcome == LocalRepoDeleteOutcome.Incomplete)
                {
                    localRepositoryFailures.Add(new LocalRepositoryNotDeleted(reference, result.Error!));
                    continue;
                }

                if (result.Outcome != LocalRepoDeleteOutcome.NotFound) localRepositoriesKept.Add(reference);
                localRepositoryFailures.Add(new LocalRepositoryNotDeleted(
                    reference, $"{result.Error} Delete it from Admin → Repositories."));
            }

            // Exactly what went, because this is the one act in the system with nothing to inspect
            // afterwards. The AFTER-row, carrying the result; the team.deleting row before the act is the
            // one that must land, and this one stays on the swallowing path so a team already gone is
            // never reported as a failure.
            await audit.WriteAsync(
                context, TenantActions.TeamDeleted, removed.Team, label,
                // Named explicitly rather than by shorthand: a shorthand property keeps its C# casing and
                // nothing camel-cases this detail on the way out, so the whole grid reads one way.
                new
                {
                    containers = removed.Containers,
                    pendingDeliveries = removed.PendingDeliveries,
                    schedules = removed.Schedules,
                    directories = removed.Directories,
                    failures = removed.Failures,

                    // Every path still on disk, one by one: the root keeps its marker and is retried.
                    remaining = removed.Remaining,
                    localRepositoriesDeleted,
                    localRepositoriesKept,
                    localRepositoryFailures = localRepositoryFailures
                        .Select(f => new { reference = f.Reference, reason = f.Reason }).ToArray(),

                    // The agent CLIs' session folders for its workspaces, and any still there.
                    sessionFolders = removed.SessionFolders,
                    sessionFoldersRemaining = removed.SessionFoldersRemaining,

                    // Its recorded file changes and agent steps, which went with it.
                    fileChanges = removed.FileChanges,
                    runSteps = removed.RunSteps,
                },
                ct);

            await listPush.AnnounceDeletedAsync(removed.Team, viewers, ct);

            // 200 with a body rather than 204. A deletion that could not remove a directory is still a
            // deletion - the team is gone from every list - and a caller that is told only "no content"
            // cannot say which files are still on disk.
            return Results.Ok(removed with
            {
                LocalRepositoriesKept = localRepositoriesKept,
                LocalRepositoriesDeleted = localRepositoriesDeleted,
                LocalRepositoryFailures = localRepositoryFailures,
            });
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Delete a team and everything that names it")
            .WithDescription(
                "**REFUSED UNLESS THE TEAM IS QUIET**, active or archived alike: 409 with a sentence naming "
                + "each member that has a run going or an instruction accepted and not started (the same "
                + "sentence `GET /api/teams/{team}/archive-check` reads). A delete never stops running work; "
                + "open workflows do not block.\n\n"
                + "Removes every Agent Container on the team, their "
                + "cursors, subscriptions, outstanding deliveries and credentials, deletes the team row - "
                + "which cascades to its members - and removes its ROOT: one "
                + "directory holding its skills, repos, member workspaces, transcripts and Concierge "
                + "folders.\n\n"
                + "**ITS DOCUMENTS ARE KEPT**, and `documentsKept` says where they are; it is null when the "
                + "team wrote none, and its empty documents folder is removed with it. They live under the "
                + "tenant documents root rather than inside the team, so this cannot reach them: a team is "
                + "deleted as soon as its work is merged, which is exactly when its reports become the only "
                + "record of how that work was checked. They stay readable - by any person, once the "
                + "team that shared them is gone - through `GET /api/documents` and the documents routes "
                + "under this team's name.\n\n"
                + "**ITS LOCAL REPOSITORIES ARE KEPT** unless named in `deleteLocalRepository` (`localRepositoriesKept`, "
                + "the `local:<name>` references it had and still has on disk): they are listed as unused in "
                + "`GET /api/local-repos`, where a person may delete them. One named in `deleteLocalRepository` is "
                + "deleted AFTER the team through the same delete `DELETE /api/local-repos/{name}` does - its "
                + "`local-repo.deleted` row first, refused while another team uses it - and listed in "
                + "`localRepositoriesDeleted`; one that could not be deleted is kept and named with its reason in "
                + "`localRepositoryFailures`, and the team is deleted either way.\n\n"
                + "**The agent CLIs' session folders** keyed to the team's own workspaces are removed from the shared "
                + "agent home (Claude's `~/.claude/projects/<workspace, dashed>` and `~/.cache/claude-cli-nodejs/...`, "
                + "each built-in preset's `sessionFolders`), counted in `sessionFolders`; any path left is in "
                + "`sessionFoldersRemaining` and `failures`. Nothing else in the home, and nothing of a live team's.\n\n"
                + "**The message log is not touched.** It is append-only, and a team's messages are the "
                + "history of what happened rather than a property of the team; deleting them would take "
                + "other teams' causally-linked messages with them. Its recorded file changes (the Monitor's "
                + "replay of its worktrees) and agent steps are removed, counted in `fileChanges` and `runSteps` on the response and the "
                + "`team.deleted` row.\n\n"
                + "**A `team.deleting` row is written to the tenant log BEFORE anything is removed** (after the "
                + "confirmation refusal), naming the containers and root. When it cannot be written the delete "
                + "answers 500 with a sentence saying so and removes nothing. The `team.deleted` row after the "
                + "act carries the result.\n\n"
                + "Answers 200 with what was removed, including any directory that could NOT be deleted - "
                + "usually a file still held by a child process that has not finished exiting. Those are "
                + "named rather than swallowed, and the team is gone either way.\n\n"
                + "**A root that does not carry the platform's `.harness-team` marker is LEFT ALONE and "
                + "named in `failures`.** A team's root can be a path a person typed, so the marker - "
                + "written when the platform created the folder - is the only evidence it made it. The "
                + "database rows go either way; the files are a person's to remove.\n\n"
                + "If the team's clone or any linked git worktree still holds commits that are on no "
                + "remote, deletion REFUSES with 409 before it stops a process or removes a row. Repeat "
                + "the delete only by sending back the exact confirmation string the refusal names, so the "
                + "loss being accepted is the one the server actually found.\n\n"
                + "404 for an unknown team; asking twice is not an error.\n\n"
                + "**A person's action.**");

        // Handing a team a clean slate WITHOUT deleting it: keep the Agent Containers, reset the substrate
        // underneath them.
        //
        // It declares {team}, so TeamGate covers it, and it is HumansOnly (see below): a member able to
        // reset its own team could erase its own evidence, and Reset repositories removes code.
        //
        // The ORDER the work happens in is TeamReset's, and its doc comment is where the reasoning lives.
        // This route's job is authority, the two refusals, and reporting what actually happened.
        app.MapPost("/api/teams/{team}/reset", async (
            [Description(Describe.Team)] string team,
            TeamResetOptions options,
            TeamReset reset, TenantLogging audit, ITenantLog tenantLog, TeamRegistry teams, HttpContext context,
            CancellationToken ct) =>
        {
            // The LABEL, read before the reset - an audit row naming an identifier a person has never seen
            // is a row they cannot match to what they did.
            var label = teams.ExistingName(team) is { } present ? teams.LabelFor(present) : team;

            TeamWasReset? done;

            try
            {
                // RESET REPOSITORIES' ROW COMES FIRST, and is not swallowed: a tree or branch removed with
                // no record of who asked is the one outcome refused, so a row that cannot be written stops
                // the whole reset before anything moves. It names what may go; the team.reset row after
                // names what went and what was kept.
                done = await reset.ResetAsync(team, options, ct, (plan, token) => tenantLog.WriteAsync(
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                    context.User.FindFirstValue(ClaimTypes.Email),
                    TenantActions.TeamResetRepositories, teams.ExistingName(team) ?? team, label,
                    JsonSerializer.Serialize(new
                    {
                        members = options.Members,
                        worktrees = plan.Worktrees,
                        branches = plan.Branches,
                        teamBranch = plan.TeamBranch,
                    }, JsonSerializerOptions.Web),
                    token));
            }
            catch (TeamBusyException busy)
            {
                // 409, not 400. The request is well formed and the team is right there; it is the team's
                // STATE that refuses, and waiting is the fix. The WHOLE reset is refused rather than the
                // idle half - a team where some members remember the last task and others do not is a worse
                // state than either.
                return Results.Conflict(new { error = busy.Message, busy = busy.Busy });
            }
            catch (ResetDefaultBranchNotKnownException unknown)
            {
                // The team branch is reset to the STORED default branch or not at all, and the existing
                // sentence says which. Nothing else changes: the refusal comes before anything moves.
                await audit.WriteAsync(
                    context, TenantActions.TeamResetRepositories, teams.ExistingName(team) ?? team, label,
                    new { refused = true, reason = unknown.Message }, ct);
                return Results.Conflict(new { error = unknown.Message, repo = unknown.Repo });
            }
            catch (ResetNotRecordedException unrecorded)
            {
                return Results.Json(new { error = unrecorded.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }

            if (done is null) return Refusals.NoTeam(team);

            // NAMES AND COUNTS, NEVER CONTENT. Every person can read this table and it is kept
            // forever; `payload.output` carries a bounded excerpt of real agent output, so a purge records
            // THAT it happened and how much went, and nothing of what was in it.
            await audit.WriteAsync(
                context, TenantActions.TeamReset, done.Team, label,
                // Named explicitly rather than by shorthand, matching the deletion row beside it: a
                // shorthand property keeps its C# casing and nothing camel-cases this detail on the way
                // out, so the whole grid reads one way.
                new
                {
                    floor = done.Floor,
                    floored = done.Floored,
                    purged = done.Purged,
                    retained = done.Retained,

                    // The ticked members' recorded file changes and agent steps Delete memory removed.
                    fileChanges = done.FileChanges,
                    runSteps = done.RunSteps,
                    cleared = done.Cleared,
                    failures = done.Failures,
                    remaining = done.Remaining,

                    // Each tree and branch removed, and each kept with its reason. Absent when not asked.
                    repositories = done.Repositories is null
                        ? (JsonElement?)null
                        : JsonSerializer.SerializeToElement(done.Repositories, JsonSerializerOptions.Web),
                },
                ct);

            // 200 with a body rather than 204, for TeamDeletion's reason: a directory that could not be
            // emptied is still a completed reset, and a caller told only "no content" cannot say which.
            return Results.Ok(done);
        })
            .WithTags("Teams")
            // HUMANS ONLY, and this one is load-bearing rather than bookkeeping. RequirePermit would make
            // this reachable by a machine principal - and a member able to reset its own team is an agent
            // that can erase its team's memory, including its own evidence of having done so.
            .HumansOnly()
            .WithSummary("Reset a team's substrate, keeping its Agent Containers")
            .WithDescription(
                "Advances every named member's FLOOR to the head of the message log - the value a member is "
                + "already created with, which is both where its cursor starts (so nothing older is "
                + "delivered) and where its ledger starts (so nothing older is remembered). The members "
                + "themselves are untouched: identity, label, Agent, Prompt, override, permits and "
                + "subscriptions all survive, as do the team's name and its documents.\n\n"
                + "`forgetHistory` defaults to **true** - naming members and saying nothing else is a "
                + "reset. Send it false to clear folders and leave the log alone; it is the only field here "
                + "whose default is true.\n\n"
                + "Optionally clears the named members' working folders and transcripts, the team's shared "
                + "documents, and resets the team's Concierge - ending **every session on the "
                + "team, including other people's**, "
                + "since sessions are keyed per person and a reset is a team-level act.\n\n"
                + "`purge` PERMANENTLY DELETES the named members' messages. Irreversible. A row still cited "
                + "by a message that is NOT being deleted is RETAINED rather than failing the purge, and "
                + "counted - nulling its causation would destroy the one field that answers what woke "
                + "what. Which message that is, is NOT reported and must not be guessed: it is frequently "
                + "another member of the SAME team, one since removed, or the team's Concierge, "
                + "which is not a member and is never targeted.\n\n"
                + "409 when any named member is running or holds accepted work: the whole reset is refused, "
                + "never the idle half.\n\n"
                + "`resetRepositories` removes each named member's worktrees (never forced) and its merged or "
                + "empty branches, and with EVERY member named resets `team/<id>` to the stored default "
                + "branch; `repositories` in the answer names each tree and branch removed and each kept, "
                + "with the reason. Its `team.reset-repositories` tenant row is written before anything "
                + "moves: 500 and nothing reset when it cannot be. 409 with the existing sentence, and "
                + "nothing reset, when every member is named and a repository's default branch is not known.\n\n"
                + "404 for an unknown team. A member the team does not hold is skipped rather than "
                + "refused.\n\n"
                + "**A person's action.**");

        // WHAT RESET REPOSITORIES WOULD REMOVE, read before the person confirms: the dialog lists the
        // ticked members' trees and branches, and the team branch when every member is ticked. HumansOnly
        // for the reason the reset itself is.
        app.MapGet("/api/teams/{team}/reset/repositories", async (
            [Description(Describe.Team)] string team,
            TeamReset reset, CancellationToken ct) =>
            await reset.PreviewRepositoriesAsync(team, ct) is { } preview
                ? Results.Ok(preview)
                : Refusals.NoTeam(team))
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("List what Reset repositories would remove")
            .WithDescription(
                "Every member's worktrees and local branches in every team repository, each with its "
                + "member, and the team branch `team/<id>` that is reset when every member is ticked. "
                + "`defaultBranchNotKnown` names each repository whose default branch is not known, for "
                + "which that reset is refused. Reads only; changes nothing. 404 for an unknown team.");

        app.MapPost("/api/teams/{team}/pause", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            // The flag and its tenant_events row are one transaction: a change with no record of who
            // made it does not land.
            await teams.SetPausedAsync(
                stored, paused: true, ct,
                id => TenantLogging.Row(context, TenantActions.TeamPaused, id, teams.LabelFor(id), new { paused = true }));

            return Results.NoContent();
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Pause this team")
            .WithDescription(
                "Stops this team from being offered new work, and stops its containers from taking their "
                + "next queued batch. A run already in flight is left alone and finishes normally. "
                + "Idempotent: pausing an already-paused team still answers 204.");

        app.MapPost("/api/teams/{team}/resume", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            // The flag and its tenant_events row are one transaction: a change with no record of who
            // made it does not land.
            await teams.SetPausedAsync(
                stored, paused: false, ct,
                id => TenantLogging.Row(context, TenantActions.TeamResumed, id, teams.LabelFor(id), new { paused = false }));

            return Results.NoContent();
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Resume this team")
            .WithDescription(
                "Lets this team start receiving new work again. Queued work remains queued while paused and "
                + "delivers in order after resume. Idempotent: resuming an already-running team still "
                + "answers 204.");

        // ARCHIVING A TEAM: kept, hidden, doing no work. A person always; the person's Concierge only with
        // the Archive permit AND `concierge.mayArchive` on (ConciergeArchiveGate), marked on both routes as
        // the merge routes are. Refused unless the team is quiet; open workflows do not block. The flag and
        // its tenant row land in one transaction, or nothing changes. See TeamArchive.
        app.MapGet("/api/teams/{team}/archive-check", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, TeamArchive archive, CancellationToken ct) =>
            teams.ExistingName(team) is { } stored
                ? Results.Ok(await archive.CheckAsync(stored, ct))
                : Refusals.NoTeam(team))
            .WithTags("Teams")
            .RequirePermit(Permits.Read)
            .WithSummary("Whether this team is quiet enough to archive or delete, and its open workflows")
            .WithDescription(
                "`quiet` is true when no member has a run going and none has an instruction accepted and not "
                + "started. Otherwise `reason` is the sentence an archive or a delete is refused with, member "
                + "by member. `openWorkflows` lists each open workflow (`workflow`, `title`): they do not "
                + "block, and the archive confirmation shows them. Reads only. 404 for an unknown team.");

        app.MapPost("/api/teams/{team}/archive", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, TeamArchive archive, ConciergeArchiveGate gate, IUserStore users,
            HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            var caller = await gate.CheckAsync(context, users, ct);
            if (caller.Refusal is { } refusal) return refusal;

            var label = teams.LabelFor(stored);
            if (teams.IsArchived(stored)) return Results.Conflict(new { error = TeamArchive.AlreadyArchived(label) });
            if (await archive.NotQuietAsync(stored, ct) is { } busy) return Results.Conflict(new { error = busy });

            var archivedBy = caller.ViaConcierge
                ? $"the Concierge for {caller.ActorEmail ?? "a person"}"
                : caller.ActorEmail ?? "a person";
            var now = DateTimeOffset.UtcNow;

            try
            {
                await teams.SetArchivedAsync(stored, archivedBy, now, id => ArchiveRow(context, caller.ActorEmail, TenantActions.TeamArchived, id, label, new
                {
                    archived = true,
                    archivedBy,
                    paused = true,
                    viaConcierge = caller.ViaConcierge,
                }), ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(
                    new { error = $"'{label}' was not archived: its tenant log row could not be written ({exception.Message}). Nothing was changed." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(new { team = stored, archived = true, archivedAt = now, archivedBy, paused = true });
        })
            .WithTags("Teams")
            .HumansOrConcierge(Permits.Archive)
            .WithSummary("Archive this team")
            .WithDescription(
                "Keeps the team and every row, member, document, repository, site and workflow it has, and "
                + "puts it away: it is paused, every trigger of it skips its fire with the reason \"team "
                + "archived\", and a tell, a backlog dispatch, a planned card or a site action for it is refused "
                + "until it is unarchived. Its sites stay readable, and it can still be cloned and deleted.\n\n"
                + "409 with a sentence naming each member still at work when the team is not quiet (a run "
                + "going, or an instruction accepted and not started); open workflows do not block. 409 for a "
                + "team already archived. The flag and its `team.archived` tenant row land together, or "
                + "nothing changes (500 with a sentence).\n\n"
                + "A person's action, which the person's Concierge may also take only while a person has "
                + "turned on `concierge.mayArchive`.");

        app.MapPost("/api/teams/{team}/unarchive", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, ConciergeArchiveGate gate, IUserStore users,
            HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            var caller = await gate.CheckAsync(context, users, ct);
            if (caller.Refusal is { } refusal) return refusal;

            var label = teams.LabelFor(stored);
            if (!teams.IsArchived(stored)) return Results.Conflict(new { error = TeamArchive.NotArchived(label) });

            try
            {
                await teams.SetArchivedAsync(stored, archivedBy: null, DateTimeOffset.UtcNow, id => ArchiveRow(context, caller.ActorEmail, TenantActions.TeamUnarchived, id, label, new
                {
                    archived = false,
                    paused = true,
                    viaConcierge = caller.ViaConcierge,
                }), ct);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return Results.Json(
                    new { error = $"'{label}' was not unarchived: its tenant log row could not be written ({exception.Message}). Nothing was changed." },
                    statusCode: StatusCodes.Status500InternalServerError);
            }

            return Results.Ok(new { team = stored, archived = false, paused = true });
        })
            .WithTags("Teams")
            .HumansOrConcierge(Permits.Archive)
            .WithSummary("Unarchive this team")
            .WithDescription(
                "Clears the archived flag and LEAVES THE TEAM PAUSED: nothing restarts until a person presses "
                + "Resume, which then runs its queued work. 409 for a team that is not archived. The change "
                + "and its `team.unarchived` tenant row land together, or nothing changes.\n\n"
                + "A person's action, which the person's Concierge may also take only while a person has "
                + "turned on `concierge.mayArchive`.");

        // A team's own named values - its shared test credentials, and anything else every member must
        // agree on. Its own route for the repos route's reason below.
        //
        // .HumansOnly() is the WHOLE of the restriction here, in both directions. A member that could WRITE
        // this would give itself variables or overwrite the team's credentials; a member that could READ
        // it would have a way to enumerate values it was never given. Every member and every
        // Concierge runs with permissions bypassed, so there is nothing else standing in the way.
        app.MapGet("/api/teams/{team}/env", (
            [Description(Describe.Team)] string team, TeamRegistry teams) =>
            teams.ExistingName(team) is { } stored
                ? Results.Ok(teams.EnvFor(stored))
                : Refusals.NoTeam(team))
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Read a team's environment")
            .WithDescription(
                "The named values every member of this team is launched with. "
                + "**Values are returned in full and are NOT redacted.** Redaction here would be theatre: "
                + "anyone who can reach this route can already read any of these by putting `{env:NAME}` "
                + "into a member's prompt override and reading the transcript. A control that looks like a "
                + "boundary and is not is worse than an honest absence of one. "
                + "**This is coordination, not confinement.** These sit in the database in plaintext and "
                + "every agent runs with permissions bypassed. Only put values every person in the "
                + "tenant is already entitled to - never a production key.");

        // ITS OWN ROUTE, like env and repos beside it. A budget is one number and a PATCH field carrying
        // it would have to distinguish absent from cleared, which is exactly the three-state trap
        // `PATCH .../containers/{name}` already documents - here a body whose only field IS the budget
        // says which of the three it means without a convention.
        //
        // THE THREE STATES ARE THE POINT. Null is "I have chosen nothing", which returns the team to the
        // instance `WorkflowSpendLimit`; 0 is an explicit "unlimited"; a positive value is the figure, and
        // it APPLIES EVEN WHEN IT IS ABOVE the instance figure - the team's figure wins, rather than the
        // lower of the two.
        //
        // A BOUND A TEAM CAN RAISE IS NOT A BACKSTOP, and this route is `{team}`-gated so anyone who
        // reaches the team can raise their own. That is deliberate: a limit nobody can see is one nobody
        // can reason about, and a field that silently refuses what a person typed is a worse lie than a
        // number they can change. `WorkflowSpendLimit` stays the thing an unset team is bounded by.
        app.MapPut("/api/teams/{team}/budget", async (
            [Description(Describe.Team)] string team,
            TeamBudget? body, TeamRegistry teams, HttpContext context,
            CancellationToken ct) =>
        {
            // REFUSED HERE, ON THE ROUTE, not left to the input control: "a boundary a route call can
            // bypass is not a boundary" - the same rule `max-concurrent` states beside it. Left unrefused
            // a negative value would be STORED, because the setter's job is to store what it
            // is given, and it would then read as a bound of less than zero that nothing can ever be
            // under. 0 is NOT refused: it is a meaningful choice here.
            if (body?.BudgetTokens is < 0)
            {
                return Results.BadRequest(new
                {
                    error = "budgetTokens cannot be negative. Use 0 for unlimited, or null to use the "
                        + "instance figure.",
                });
            }

            // The figure and its tenant_events row are one transaction: a change with no record of who
            // made it does not land.
            try
            {
                await teams.SetBudgetAsync(
                    team, body?.BudgetTokens, ct,
                    stored => TenantLogging.Row(
                        context, TenantActions.TeamBudgetChanged, stored, teams.LabelFor(stored),
                        new { budgetTokens = body?.BudgetTokens }));
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }

            var stored = teams.ExistingName(team)!;

            // THE WHOLE SUMMARY, not the single field back. The caller needs `effectiveWorkflowBudget`
            // alongside the choice it just made - it is what the KPI bar measures against, and a browser
            // that re-derived it would be the second place that knows 0 means unlimited.
            return Results.Ok(teams.All().First(t => string.Equals(t.Id, stored, StringComparison.Ordinal)));
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Set what this team may spend on ONE workflow")
            .WithDescription(
                "Tokens, input and output together, and the figure bounds **one workflow** rather than "
                + "the team's total: a team with three workflows open has three budgets of this size, one "
                + "each, and nothing is shared between them.\n\n"
                + "**Three states.** `null` (or an absent body) means the team has chosen nothing and the "
                + "instance `WorkflowSpendLimit` applies. `0` means UNLIMITED. A positive value is stored "
                + "exactly as typed and applies **even when it is above the instance figure** - this is "
                + "not a ceiling a team may only go under. 400 for a negative value.\n\n"
                + "A workflow that reaches its figure PAUSES rather than going quiet, and a person - and "
                + "only a person - resumes that one workflow through "
                + "`POST /api/teams/{team}/workflows/{correlation}/resume`.\n\n"
                + "It takes effect on the next WAKE, which is where spending happens, not only on the "
                + "next dispatch.\n\n"
                + "Answers the whole team, so the caller gets `effectiveWorkflowBudget` - the figure "
                + "actually in force, with null meaning unlimited - beside the choice it just made.");

        app.MapPut("/api/teams/{team}/env", async (
            [Description(Describe.Team)] string team,
            IReadOnlyDictionary<string, string>? env, TeamRegistry teams, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                // THE ROW NAMES THE VARIABLES AND NEVER A VALUE: a team's env is where a secret goes.
                await teams.SetEnvAsync(
                    team, env, ct,
                    stored => TenantLogging.Row(
                        context, TenantActions.TeamEnvChanged, stored, teams.LabelFor(stored),
                        new { names = (env?.Keys ?? []).Order(StringComparer.Ordinal).ToArray() }));
                return Results.Ok(teams.EnvFor(teams.ExistingName(team)!));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Replace a team's environment")
            .WithDescription(
                "Replaces the whole map; an empty object clears it. Names must look like environment "
                + "variables and may not begin `HARNESS_`, which is the platform's own prefix and "
                + "carries a container's credential. "
                + "Containers already running are updated in place, so a rotated value reaches a live "
                + "agent on its next invocation rather than after a restart.");

        // Its OWN route rather than folded into a wider team-settings write: the list is the whole body, so
        // clearing it is an explicit empty array rather than a special third state.
        app.MapPut("/api/teams/{team}/repos", async (
            [Description(Describe.Team)] string team,
            JsonElement body, TeamRegistry teams, TeamRepoSetup repoSetup, LocalRepos localRepos, HttpContext context,
            CancellationToken ct) =>
        {
            // THE BARE ARRAY AS BEFORE, or `{ repos, repoChoices }` to answer a refused check.
            SetTeamRepos request;
            try
            {
                request = body.ValueKind switch
                {
                    JsonValueKind.Array => new SetTeamRepos(body.Deserialize<List<string>>(JsonSerializerOptions.Web), null),
                    JsonValueKind.Object => body.Deserialize<SetTeamRepos>(JsonSerializerOptions.Web)!,
                    JsonValueKind.Null => new SetTeamRepos(null, null),
                    _ => throw new JsonException(),
                };
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "Send the repository list as an array, or as { \"repos\": [...], \"repoChoices\": {...} }." });
            }

            if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

            TeamLocalRepository? localRepository = null;
            var attached = false;
            try
            {
                RepoUrls.Validate(request.Repos, localRepos.Exists);

                // Only a URL the team does not have yet is read: one already attached was checked then.
                var plan = await repoSetup.PlanAsync(
                    request.Repos ?? [], request.RepoChoices, person: true, alreadyAttached: teams.ReposFor(stored), ct);
                await repoSetup.CreateOnGitHubAsync(plan, ct);

                var repos = plan.Kept.ToList();
                if (plan.UseLocal && !repos.Any(LocalRepos.IsLocal))
                {
                    localRepository = await repoSetup.EnsureLocalAsync(stored, ct);
                    repos.Add(localRepository.Reference);
                }

                await teams.SetReposAsync(
                    stored, repos, ct,
                    audit: id => TenantLogging.Row(context, TenantActions.TeamReposChanged, id, teams.LabelFor(id), new { repos }));
                attached = true;
                return Results.Ok(teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase)));
            }
            catch (RepoSetupRefusedException refused)
            {
                return Results.Json(refused.Body, statusCode: refused.Status);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
            finally
            {
                if (!attached) await repoSetup.ForgetAsync(localRepository, CancellationToken.None);
            }
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Replace a team's Git repository list")
            .WithDescription(
                "Takes the complete ordered list of absolute http or https repository URLs, or `local:<name>` "
                + "for one of the instance's local repositories (`GET /api/local-repos`). An empty array "
                + "clears it. A `local:` name that is not legal or names no local repository is refused naming it. Every URL is validated before anything is written; repository names are "
                + "derived from the final path segment and unsafe or colliding names are refused. 404 for "
                + "an unknown team, 400 for an invalid URL or derived folder name.\n\n"
                + "**Every URL the team does not have yet is read with `git ls-remote` first.** One that "
                + "cannot be read is refused with 422 `{ error, code: \"repo-check-failed\", repos: [{ url, failure, "
                + "reason, choices }] }` and the list is unchanged. To answer it, send `{ \"repos\": [...], "
                + "\"repoChoices\": { \"<url>\": \"create-on-github\" | \"use-local\" | \"attach-anyway\" } }` "
                + "instead of the bare array; `use-local` puts the team's local repository in place of the URL.");

        // "Create a local repository" in Team settings: the team's own, by the same naming as a new team's.
        // And the `repo` tool's `create-local`: an agent of the team (TeamGate binds it to its own) makes the
        // team's local repository when its work first needs one, only while the team has none.
        app.MapPost("/api/teams/{team}/local-repo", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, TeamRepoSetup repoSetup, LocalRepoOnNeed onNeed, TenantLogging audit, HttpContext context,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored) return Refusals.NoTeam(team);

            // NOT A PERSON: a team's Manager, a member or the Concierge, and only for a team with no repository.
            // Every other machine principal is refused as the person's action this route otherwise is.
            if (PrincipalClaims.From(context.User) is { Kind: not PrincipalKind.User } principal)
            {
                if (LocalRepoOnNeed.AgentOf(principal) is not { } agent)
                {
                    return Results.Content(PermitGate.HumansOnlyBody, "application/json", statusCode: StatusCodes.Status403Forbidden);
                }

                try
                {
                    var (made, refusal) = await onNeed.CreateAsync(
                        stored,
                        context.User.FindFirstValue(ClaimTypes.NameIdentifier),
                        context.User.FindFirstValue(ClaimTypes.Email),
                        agent);
                    if (made is null) return Results.Conflict(new { error = refusal });

                    return Results.Created($"/api/local-repos/{made.Repository.Name}", new
                    {
                        team = teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase)),
                        localRepository = new { name = made.Repository.Name, reference = made.Repository.Reference, created = made.Repository.Created },
                        clone = new { path = made.ClonePath, cloned = made.Cloned, defaultBranch = made.DefaultBranch, teamBranch = made.TeamBranch },
                    });
                }
                catch (ArgumentException exception)
                {
                    return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
                }
            }

            var current = teams.ReposFor(stored);
            if (current.FirstOrDefault(LocalRepos.IsLocal) is { } existing)
            {
                return Results.Conflict(new { error = $"{stored} already has a local repository, {existing}." });
            }

            TeamLocalRepository? localRepository = null;
            var attached = false;
            try
            {
                localRepository = await repoSetup.EnsureLocalAsync(stored, ct);
                await teams.SetReposAsync(stored, [.. current, localRepository.Reference], ct);
                attached = true;
            }
            catch (ArgumentException exception)
            {
                return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status500InternalServerError);
            }
            finally
            {
                if (!attached) await repoSetup.ForgetAsync(localRepository, CancellationToken.None);
            }

            await audit.WriteAsync(
                context, TenantActions.LocalRepoCreated, localRepository.Name, localRepository.Name,
                new
                {
                    reference = localRepository.Reference,
                    defaultBranch = LocalRepos.InitialBranch,
                    team = stored,
                    created = localRepository.Created,
                },
                ct);

            return Results.Created($"/api/local-repos/{localRepository.Name}", new
            {
                team = teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase)),
                localRepository = new { name = localRepository.Name, reference = localRepository.Reference, created = localRepository.Created },
            });
        })
            .WithTags("Teams")
            // NOT HumansOnly: a team's own agent may call it for a team with no repository, and the handler
            // refuses every other machine principal with the humans-only sentence. TeamGate bounds the team.
            .NoPermitRequired()
            .WithSummary("Create the team's local repository")
            .WithDescription(
                "No body. Creates the team's local repository and adds it to the team's list, which clones it: "
                + "named after the team, `local:<team id>`; an existing one of that name that no team uses is "
                + "reused, and `-2`, `-3` are tried when a team uses it. Answers 201 `{ team, localRepository: "
                + "{ name, reference, created } }`. 409 when the team already has a local repository, 404 for an "
                + "unknown team, 500 naming the reason when the repository cannot be made (nothing is attached). "
                + "Appends `local-repo.created` to the tenant log.\n\n**A person's action**, and the `repo` tool's "
                + "`create-local` for the team's own Manager, members and the Concierge: only while the team has "
                + "no repository at all, refused otherwise with 409 `{ error }` naming the one it has, and nothing "
                + "changes. An agent's 201 also carries `clone: { path, cloned, defaultBranch, teamBranch }`, and "
                + "its row names the `agent`. A second call while the first is making it waits and answers the "
                + "same. Any other machine principal is refused with 403.");

        // A person's choice of one repository's default branch. `branch` null or blank clears it,
        // and the host goes back to what origin's HEAD named on the last clone or successful Fetch (or not
        // known). The choice is stored apart from the remote's value, so no Fetch ever replaces it.
        app.MapPut("/api/teams/{team}/repos/{repo}/default-branch", async (
            [Description(Describe.Team)] string team,
            [Description("The repository name (derived from the URL in the team's repos list)")] string repo,
            SetRepoDefaultBranch request, TeamRegistry teams, HttpContext context,
            CancellationToken ct) =>
        {
            try
            {
                // One transaction with its tenant_events row.
                await teams.SetPersonDefaultBranchAsync(
                    team, repo, request.Branch, ct,
                    next => TenantLogging.Row(
                        context, TenantActions.RepoDefaultBranchSet, $"{next.Team}/{next.Repo}", null,
                        new { setByPerson = next.SetByPerson, fromRemote = next.FromRemote }));
                var stored = teams.ExistingName(team)!;

                return Results.Ok(teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase)));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Set or clear a repository's default branch")
            .WithDescription(
                "Body `{ \"branch\": \"trunk\" }` sets the branch every Git action on this repository treats "
                + "as main; `null` or blank clears it. A value set here is kept across every later Fetch and "
                + "used until it is cleared; once cleared, the branch origin's HEAD named on the last clone "
                + "or successful Fetch is used, or it is not known. Answers the team. 404 for an unknown "
                + "team or repository, 400 for a name git would not accept as a branch.");

        // A repository's contributor settings: its upstream (contributor mode), the fork's owner,
        // DCO sign-off and the CLA note. Stored first, then the clone is brought in line - the upstream
        // remote added, re-pointed or removed, and nothing else; the sign-off hook written or removed.
        // Turning DCO on is refused with the fix when the instance has no git identity, and when a
        // commit-msg hook the platform did not write is in the way; neither refusal stores anything.
        app.MapPut("/api/teams/{team}/repos/{repo}/contributor", async (
            [Description(Describe.Team)] string team,
            [Description("The repository name (derived from the URL in the team's repos list)")] string repo,
            SetRepoContributor request, TeamRegistry teams, TeamPaths paths, InstanceGitIdentity identity,
            ContributorClone contributorClone, TenantLogging audit, HttpContext context, CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            var name = teams.ReposFor(stored).Select(RepoUrls.DeriveName)
                .FirstOrDefault(n => string.Equals(n, repo, StringComparison.OrdinalIgnoreCase));
            if (name is null)
            {
                return Results.NotFound(new { error = $"No repo '{repo}' on team '{team}'." });
            }

            var clonePath = Path.Combine(paths.ReposFor(stored), name, "main");
            var turningOn = request.DcoSignOff && !teams.ContributorFor(stored, name).DcoSignOff;
            if (turningOn && identity.Current is null)
            {
                return Results.Conflict(new { error = InstanceGitIdentity.Missing });
            }

            if (request.DcoSignOff && File.Exists(DcoHook.PathFor(clonePath)) && !DcoHook.IsOurs(clonePath))
            {
                return Results.Conflict(new
                {
                    error = $"A commit-msg hook the platform did not write is already at {DcoHook.PathFor(clonePath)}, "
                        + "so sign-off was not turned on. Move it away, then turn the setting on again.",
                });
            }

            RepoContributor saved;
            try
            {
                saved = await teams.SetContributorAsync(
                    stored, name, request.UpstreamUrl, request.ForkOwner, request.DcoSignOff, request.ClaSignedNote, ct);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            var applied = await contributorClone.ApplyAsync(clonePath, saved, ct);

            await audit.WriteAsync(
                context, TenantActions.RepoContributorSet,
                subject: $"{stored}/{name}",
                subjectName: null,
                new
                {
                    upstream = GitOutputRedaction.RedactUserInfo(saved.UpstreamUrl),
                    forkOwner = saved.ForkOwner,
                    dcoSignOff = saved.DcoSignOff,
                    claSignedNote = saved.ClaSignedNote,
                    clone = applied,
                },
                ct);

            if (applied is not null)
            {
                return Results.Json(
                    new { error = $"The settings were saved, but the clone was not brought in line: {applied}" },
                    statusCode: StatusCodes.Status502BadGateway);
            }

            return Results.Ok(teams.All().Single(t => string.Equals(t.Id, stored, StringComparison.OrdinalIgnoreCase)));
        })
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Set a repository's contributor settings: upstream, fork owner, DCO, CLA note")
            .WithDescription(
                "Body `{ \"upstreamUrl\": \"https://github.com/project/Widget.git\", \"forkOwner\": null, "
                + "\"dcoSignOff\": true, \"claSignedNote\": \"signed 2026-09-20\" }`. An upstream URL puts the "
                + "repository in contributor mode: its URL is the fork (origin) and the clone gets an `upstream` "
                + "remote beside it; Fetch fetches both, and Bring current and Rebase use upstream/<default>. "
                + "`null` or blank makes it owned again and removes that remote and nothing else. A blank fork "
                + "owner is read from the repository URL. `dcoSignOff` installs a commit-msg hook in the clone "
                + "(its worktrees share it) that adds `Signed-off-by:` with the instance's git identity, once; "
                + "turning it on answers 409 when GIT_AUTHOR_NAME / GIT_AUTHOR_EMAIL are not configured. The CLA "
                + "note is a record, never a signature. Answers the team. 404 for an unknown team or repository, "
                + "400 for an invalid URL or account name.");

        app.MapPut("/api/teams/{team}/member-agent", async (
            [Description(Describe.Team)] string team,
            SetMemberAgent request, TeamRegistry teams, AgentInstallProbe probe,
            AgentCatalog catalog, HttpContext context, CancellationToken ct) =>
        {
            try
            {
                // One transaction with its tenant_events row.
                await teams.SetMemberAgentsAsync(
                    team, request.Agents ?? [request.Agent ?? ""], ct,
                    agents => TenantLogging.Row(
                        context, TenantActions.TeamMemberAgentChanged,
                        teams.ExistingName(team), teams.LabelFor(teams.ExistingName(team) ?? team),
                        new { agents }));

                var unresolvedList = new List<object>();
                var memberAgents = teams.MemberAgentsFor(team) ?? [];
                foreach (var agent in memberAgents)
                {
                    var definition = catalog.Definition(agent);
                    if (definition is not null)
                    {
                        var installation = probe.Probe(definition);
                        if (installation.State == AgentInstallStates.NotInstalled)
                        {
                            unresolvedList.Add(new { agent = installation.Agent, command = installation.Command, message = installation.Message });
                        }
                    }
                }

                var teamData = teams.All().Single(t => string.Equals(
                    t.Id, teams.ExistingName(team), StringComparison.OrdinalIgnoreCase));

                // Serialize teamData to JsonNode and add unresolvedAgents
                var node = JsonSerializer.SerializeToNode(teamData, HostJson.WebEnums);
                if (node is JsonObject obj)
                {
                    obj["unresolvedAgents"] = JsonSerializer.SerializeToNode(unresolvedList, HostJson.WebEnums);
                }
                return Results.Json(node, statusCode: 200);
            }
            catch (NoSuchAgentException exception)
            {
                // 400, not 404: the TEAM is there, it is the Agent that is missing or is interactive.
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        })
            // Gated by TEAM access, and covered by TeamGate through declaring `{team}` rather than
            // through anyone remembering: choosing what your own team's new members run is the team's
            // business, exactly as adding one of them is.
            //
            // Its OWN route rather than a field on `PATCH /api/teams/{team}`, mirroring concierge
            // above: that PATCH refuses an empty name, so folding this in would force a caller to resend a
            // name it does not care about - and a slip there renames the team.
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Choose which Agent this team's new members run")
            .WithDescription(
                "The ordered allowlist this team's member create uses when it names no `agent` of its own, "
                + "and what a manager hiring through the `member` tool gets.\n\n"
                + "When a hire names no `for` tag, selection starts at the first allowed Agent.\n\n"
                + "The preset must be HEADLESS - a member is woken by a message and never typed at.\n\n"
                + "It is a setting rather than a literal in the manager's own PROMPT, which would not be a "
                + "default at all: a manager copies what it is shown, so a team put deliberately on one CLI "
                + "would hire workers on another.\n\n"
                + "400 when the Agent is unknown or is interactive; 404 for an unknown team.");

        app.MapPut("/api/teams/{team}/additional-instructions", async (
            [Description(Describe.Team)] string team,
            SetAdditionalInstructions request, TeamRegistry teams, HttpContext context,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            // One transaction with its tenant_events row. WHETHER they are set, never the words: the log is
            // read by every person and kept forever.
            await teams.SetAdditionalInstructionsAsync(
                stored, request.AdditionalInstructions, ct,
                TenantLogging.Row(
                    context, TenantActions.TeamInstructionsChanged, stored, teams.LabelFor(stored),
                    new { set = !string.IsNullOrWhiteSpace(request.AdditionalInstructions) }));

            return Results.Ok(teams.All().Single(t => string.Equals(
                t.Id, stored, StringComparison.OrdinalIgnoreCase)));
        })
            // Gated by TEAM access, and covered by TeamGate through declaring `{team}`.
            .WithTags("Teams")
            .HumansOnly()
            .WithSummary("Set this team's additional instructions")
            .WithDescription(
                "Free text appended after the built-in role prompt for this team's Manager and members, "
                + "under the heading \"Instructions from the person for this team\". It never replaces "
                + "the role prompt, which is chosen by role and cannot be set. Null or blank clears it. "
                + "Every container on the team is re-prompted, so the next wake of each reads it. Answers "
                + "the team, carrying `additionalInstructions`; 404 for an unknown team.");

        // Token totals across every completed/failed run this team published.
        //
        // Summed on the server with json_extract, not in the browser over the twenty-message feed. A card
        // that summed the retained slice would count DOWN as the team got busier, which is the blocked-mark
        // failure wearing a spend number. Historical rows without the keys are missing, not zeros.
        app.MapGet("/api/teams/{team}/tokens", async (
            [Description(Describe.Team)] string team,
            TeamRegistry teams, IUsageLedger ledger, ContainerHost host, AgentCatalog catalog,
            CancellationToken ct) =>
        {
            if (teams.ExistingName(team) is not { } stored)
            {
                return Refusals.NoTeam(team);
            }

            // THROUGH `TeamRegistry.FloorFor`, WHICH IS THE ONE STORE OF THIS RULE. The kanban board needs
            // the identical figure - without it a recreated team's board shows the cards of the team it
            // replaced - and a second copy is how the next reader ends up being one that does not ask.
            var floor = teams.FloorFor(stored);

            // FROM THE LEDGER, NOT A SCAN OF EVERY TERMINAL ROW'S PAYLOAD: the same figures the log's own
            // sum gives (LedgerParityTests pins the two against each other), over columns that are indexed
            // and that neither retention nor a Reset ever touch.
            var rows = await ledger.SumForTeamAsync(stored, floor, ct);

            var tokensIn = rows.Sum(r => r.TokensIn);
            var tokensOut = rows.Sum(r => r.TokensOut);
            var with = rows.Sum(r => r.RunsWithUsage);
            var without = rows.Sum(r => r.RunsWithoutUsage);
            var available = with > 0 || without == 0;
            var partial = available && without > 0;

            // WHETHER A BRAND CAN ANSWER AT ALL, resolved per request and never cached against a
            // container - the drift this codebase already paid for three times. Null means the Agent could
            // not be resolved, which is "we could not tell" and not "it reports nothing": the same
            // not-measured-is-not-a-failure rule the run probe follows.
            bool? ReportsUsage(string brand) =>
                string.IsNullOrEmpty(brand)
                    ? null
                    : catalog.Definition(brand) is { } definition
                        ? definition.Launch.UsageFormat is not null
                        : null;

            var members = new List<MemberTokenTotals>();
            var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows.OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase))
            {
                string member;
                string brand;

                try
                {
                    var id = ContainerId.Parse(row.Source);
                    member = id.Name;
                    brand = host.Find(id)?.Snapshot().Agent ?? "";
                }
                catch (FormatException)
                {
                    member = row.Source;
                    brand = "";
                }

                listed.Add(member);

                members.Add(MemberTokenTotals.FromUsage(member, brand, row, ReportsUsage(brand)));
            }

            // EVERY MEMBER THE TEAM HOLDS, NOT ONLY THE ONES WITH ROWS. A member mid-first-run has
            // published no completed or failed row, so without this it would be absent from a dialog that
            // claims to say who consumed what - and absence reads as "nothing to report about this member" when the truth is
            // "nothing yet". `runs: 0` is what says which.
            //
            // ADDED AFTER the loop above and guarded on `listed`, so a member that DOES have rows keeps
            // its numbers rather than being overwritten by an empty line. Nothing here touches the team's
            // own totals: those are summed from `rows`, which is the log.
            foreach (var id in teams.ContainerIdsOf(stored)
                .OrderBy(container => container.Name, StringComparer.OrdinalIgnoreCase))
            {
                if (!listed.Add(id.Name)) continue;

                var brand = host.Find(id)?.Snapshot().Agent ?? "";

                members.Add(new MemberTokenTotals(id.Name, brand, null, null, ReportsUsage(brand), 0));
            }

            return Results.Ok(new TeamTokenTotals(
                available,
                tokensIn,
                tokensOut,
                partial,
                with,
                without,
                available
                    ? null
                    : "Completed and failed payloads on the message log do not carry tokensIn/tokensOut",
                members,
                rows.Sum(r => r.TokensCachedIn),
                rows.Sum(r => r.TokensCacheCreation),
                rows.Sum(r => r.TokensBillable)));
        })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("Token totals across a team's completed and failed runs")
            .WithDescription(
                "A TEAM TOTAL from the message log: json_extract of tokensIn/tokensOut on every "
                + "agentContainer.completed and agentContainer.failed this team published, grouped by member.\n\n"
                + "Not a slice of the board's twenty-message feed, and not a guess. Rows that predate "
                + "capture (no keys) are counted as missing rather than as zero; if every run on the "
                + "team is one of those, the answer is unavailable rather than 0.\n\n"
                + "partial is true when some runs on this team carry no usage - either their Agent reports "
                + "none, or they predate capture. Those runs are counted, never summed as zeros. "
                + "partial is true when some runs on the team have usage and earlier ones do not.\n\n"
                + "tokensIn is uncached input. tokensCachedIn (cache reads) and tokensCacheCreation (cache "
                + "writes) are summed beside it, never into it. tokensBillable weights a cache read at 1/10 "
                + "and a cache write at 5/4 and counts a combined total (codex) as reported - the weights "
                + "a workflow budget uses.\n\n"
                + "Brand is the member's current Agent, not a historical one. 404 for an unknown team.");

        // EVERY REACHABLE TEAM'S PROJECTION, in one read, for the Teams table.
        //
        // It declares no `{team}`, so TeamGate does not cover it and it resolves access itself - exactly
        // the shape `/api/overview` above uses, and for the same reason. There is NO second authority
        // question to ask here: `EffectiveTeamsAsync` already answers "everything that exists" for a
        // person, so the ordering trap that `/api/me/current-team` has to be careful about (asking
        // the effective set alone answers a person 403 for a typo, and everybody 403 for the moments
        // after a restart) cannot arise for a route that names no team.
        //
        // NOT folded into `/api/overview`: that is what the `status` tool reads, and every status call
        // would then pay a log projection per team for data it never shows.
        app.MapGet("/api/teams/rollup", async (
            HttpContext context, TeamRegistry teams, TeamAccess access, RollupCache rollup, ContainerHost host,
            WipLedger wip, IBacklogStore backlog, CancellationToken ct) =>
        {
            if (PrincipalClaims.From(context.User) is not { } principal) return Results.Unauthorized();

            var effective = await access.EffectiveTeamsAsync(principal, ct);
            var rows = new List<TeamRollupRow>();

            // ONE READ OF THE LEDGER FOR EVERY ROW, so two teams' counts are from the same instant.
            var slots = wip.View();

            // ACTIVE TEAMS ONLY: an archived team does no work, so it has no row to roll up.
            foreach (var team in teams.All().Where(t => !t.Archived && effective.Contains(t.Id)))
            {
                // THE SAME FLOOR THE PER-TEAM ROUTES COMPUTE, and it must be the same: without it a team
                // deleted and recreated under the same names would otherwise report its PREDECESSOR'S
                // workflow, because members share their qualified ids with whatever came before.
                var floor = teams.ContainerIdsOf(team.Id)
                    .Select(host.Find)
                    .Where(container => container is not null)
                    .Select(container => container!.Snapshot().SinceSeq)
                    .DefaultIfEmpty(0)
                    .Min();

                // THE PLURAL PROJECTION, BESIDE THE SINGULAR ONE, ON THE SAME FLOOR - the Teams table needs
                // every team's workflows, not only the team a person happens to have made active, and this
                // is the one place that already loops every reachable team. `WorkflowsForTeamAsync` runs
                // four queries plus one `TimingForAsync` per workflow in the capped list; this roughly
                // DOUBLES the per-team cost of a route the SPA already polls, and the list is every workflow
                // a team has run since its floor, so the per-workflow projections run up to the
                // fifty-entry cap rather than up to the open count. Accepted: the alternative is a Workflows column that only ever answers for one
                // team per page load, which is not a working feature. Named here so a later profiler finds
                // the reasoning rather than a surprise.
                // KEPT UNTIL THE TEAM'S ROWS MOVE: see RollupCache. A push from one team re-reads that team.
                var (elapsed, open) = await rollup.ReadAsync(team.Id, floor, ct);

                var waiting = slots.Waiting
                    .Where(hold => string.Equals(hold.Team, team.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var held = waiting.Select(hold => hold.Member).ToList();
                var running = slots.Running
                    .Count(hold => string.Equals(hold.Team, team.Id, StringComparison.OrdinalIgnoreCase));

                rows.Add(new TeamRollupRow(
                    team.Id, elapsed, open, running, held.Count, held,
                    // What the first of them waits for: a slot, or measured headroom ("waiting for memory: ...").
                    held.Count > 0 ? waiting[0].Reason ?? TeamRollupRow.WaitingForASlot : null));
            }

            // EVERY TEAM'S ITEMS WITH THEIR CURRENT TITLES, in one backlog read for the whole rollup - the
            // same overlay the per-team routes apply, so a row here names its work as they do.
            return Results.Ok(new TeamRollup(await WorkflowItems.OverlayAsync(rows, backlog, ct)));
        })
            .WithTags("Activity")
            .RequirePermit(Permits.Read)
            .WithSummary("Every reachable team's current or last workflow")
            .WithDescription(
                "One row per ACTIVE team the caller reaches (an archived team has none), each carrying BOTH projections: `workflow`, the same "
                + "one `GET /api/teams/{team}/workflow` answers for that team, and `openWorkflows`, the "
                + "same one `GET /api/teams/{team}/workflows` answers - every workflow that team has run "
                + "since its floor, open and closed alike, not only the newest. Both are computed from the "
                + "same floor, in the same loop, so a team's two figures here can never name a different "
                + "predecessor than each other.\n\n"
                + "`running` and `waiting` count this team's holders of, and waiters for, the "
                + "instance-wide run slots (`GET /api/wip`); `held` names the waiting members in queue "
                + "order, and `slotStatus` reads what the first of them waits for while any are waiting - \"waiting for a slot\", or \"waiting for memory: ...\" when measured headroom holds it - a team "
                + "whose Manager has a delivered wake held by the limit is waiting, not idle.\n\n"
                + "It carries NOTHING ELSE - no name, no containers. Those are on "
                + "`/api/overview`, which a console already holds, and a second source for a team's name is "
                + "a second answer waiting to disagree with the first.\n\n"
                + "An empty list means the caller reaches no team. It is not a refusal.");
    }

    // An archive or unarchive's tenant row: the caller's id, and the person's email - for the
    // Concierge, the person it acts for.
    static TriggerAudit ArchiveRow(
        HttpContext context, string? actorEmail, string action, string team, string label, object detail) =>
        new(
            context.User.FindFirstValue(ClaimTypes.NameIdentifier),
            actorEmail,
            action,
            team,
            label,
            JsonSerializer.Serialize(detail, JsonSerializerOptions.Web));
}
