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

internal sealed partial class HostComposition
{
    internal void Agents()
    {
        // BUILT-IN PRESETS FROM THE BUILD, custom ones from agents.json. Nothing built-in is
        // written to the volume, so a fix to a built-in preset reaches this instance on its next start.
        loadedCatalog = AgentCatalogFile.BuiltIns()
            .Concat(AgentCatalogFile.LoadCustom(dataRoot, Console.Out))
            .ToList();
        unmeasuredHeadlessPresets = Program.UnmeasuredHeadlessPresets(loadedCatalog);

        // THE INSTALLED PLUGINS, read from `<dataRoot>/plugins` - data, not code: nothing is compiled in and
        // nothing is registered by DI per plugin, so `POST /api/plugins/rescan` registers a new one with no
        // restart. A refused manifest is named here, once, with the field that is wrong.
        // In control a plugin's runtime is the workers' to have, never looked up on control's own PATH
        // (PluginCatalog.OnWorkers).
        pluginCatalog = new PluginCatalog(Path.Combine(dataRoot, "plugins"), control ? PluginCatalog.OnWorkers : null);
        PluginEndpoints.Report(pluginCatalog.Rescan(), Console.Out);
        builder.Services.AddSingleton(pluginCatalog);

        // WHAT INSTALLED SOLUTION TEAMS NEED OF EACH PLUGIN: an install refuses a version older than that.
        builder.Services.AddSingleton(sp => new PluginMinimums(
            sp.GetRequiredService<ITeamSolutionStore>(), sp.GetRequiredService<TeamRegistry>()));

        // INSTALLING FROM A FOLDER INSIDE THE INSTANCE, for a person's route and the operator CLI's request
        // file alike. Files are given the agent's group, which runs every plugin, when the Host switches to it.
        builder.Services.AddSingleton(sp =>
        {
            var runAs = sp.GetRequiredService<AgentLaunchUser>();
            return new PluginInstaller(
                pluginCatalog, dataRoot, runAs.Switches ? runAs.Gid : -1, sp.GetRequiredService<PluginMinimums>());
        });

        // REMOVING A PLUGIN OR ONE VERSION, from the web app: `plugin remove`'s rules, the installer's
        // gate, and its tenant row in the same transaction as the move.
        builder.Services.AddSingleton(sp => new PluginRemover(
            pluginCatalog, sp.GetRequiredService<PluginInstaller>(), sp.GetRequiredService<TeamRegistry>(), database));

        // THE EVENTS THE INSTALLED PLUGINS DECLARE join the platform's in every lookup - triggers, filters,
        // `{event.*}` tokens, the high-volume rule, `GET /api/events` - read off the catalog on each call,
        // so a rescan is seen at once. Released when this Host stops.
        pluginEvents = EventCatalog.Register(pluginCatalog);

        // A PLUGIN MEMBER'S SETTINGS: configuration and secret BINDINGS in `team_member_config`, and the values
        // resolved by logical key at each run: a key a person set from the screen (`plugin_secrets`, Data
        // Protection ciphertext) first, then the Host's own environment, where `secret set` values arrive.
        builder.Services.AddSingleton(sp => new PluginSecretStore(
            database, sp.GetRequiredService<IDataProtectionProvider>(), new EnvironmentSecretStore()));
        builder.Services.AddSingleton<ISecretStore>(sp => sp.GetRequiredService<PluginSecretStore>());
        builder.Services.AddSingleton<IPluginMemberSettingsStore>(new SqlitePluginMemberSettings(database));

        // EACH PLUGIN MEMBER OWNS ITS SECRETS: the values a person typed for one member's fields
        // (`plugin_member_secrets`, Data Protection ciphertext under a purpose of its own), resolved
        // before any key binding. The start step copies each STORED key onto the members bound to it,
        // once, and never an environment value.
        builder.Services.AddSingleton(sp => new PluginMemberSecretStore(database, sp.GetRequiredService<IDataProtectionProvider>()));
        builder.Services.AddSingleton<IPluginMemberSecrets>(sp => sp.GetRequiredService<PluginMemberSecretStore>());
        builder.Services.AddSingleton(sp => new MemberSecretReads(
            sp.GetRequiredService<PluginMemberSecretStore>(), sp.GetRequiredService<PluginSecretStore>(), sp.GetService<Marketplace>()));
        builder.Services.AddHostedService(sp => new MemberSecretsCopyAtStart(
            sp.GetRequiredService<PluginMemberSecretStore>(), sp.GetRequiredService<IDataProtectionProvider>(),
            sp.GetRequiredService<ILogger<MemberSecretsCopyAtStart>>()));

        // CONNECTIONS: OAuth accounts the Host holds for plugins (see Connections). The client secret and
        // every token are Data Protection ciphertext under `<dataRoot>/keys`; the provider is reached only
        // through IOAuthEndpoints, which the tests replace.
        builder.Services.AddSingleton(sp => new ConnectionStore(database, sp.GetRequiredService<IDataProtectionProvider>()));
        builder.Services.AddHttpClient(nameof(HttpOAuthEndpoints), client => client.Timeout = TimeSpan.FromSeconds(30));

        // THE RELEASE CHECK: whether a newer release is out, for the version chip. It reads the release
        // list of the repository the operator CLI names (Updates:Repository, or HARNESS_RELEASE_REPOSITORY);
        // with none it reads nothing and says so. It never updates anything (see ReleaseCheck).
        builder.Services.AddHttpClient(nameof(GitHubReleaseFeed), client => client.Timeout = TimeSpan.FromSeconds(20));
        builder.Services.AddSingleton<IReleaseFeed>(sp => new GitHubReleaseFeed(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(GitHubReleaseFeed)),
            builder.Configuration[ReleaseCheck.RepositorySetting] ?? builder.Configuration[ReleaseCheck.RepositoryVariable]));
        builder.Services.AddSingleton(sp => new ReleaseCheck(
            sp.GetRequiredService<IReleaseFeed>(),
            () => sp.GetRequiredService<TenantSettings>().UpdatesCheck,
            () => DateTimeOffset.UtcNow,
            sp.GetRequiredService<ILogger<ReleaseCheck>>()));
        builder.Services.AddHostedService(sp => new ReleaseCheckLoop(sp.GetRequiredService<ReleaseCheck>()));

        // THE PACKAGE CATALOG: what may be added from Marketplace > Browse. It reads the catalog address
        // the operator CLI names (Marketplace:Catalog, or HARNESS_MARKETPLACE_CATALOG); with none it reads
        // nothing and says so. A fetch only brings a package into the documents (see Marketplace).
        builder.Services.AddHttpClient(nameof(HttpMarketplaceFeed), client => client.Timeout = TimeSpan.FromMinutes(10));
        builder.Services.AddSingleton<IMarketplaceFeed>(sp => new HttpMarketplaceFeed(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HttpMarketplaceFeed)),
            builder.Configuration[Marketplace.CatalogSetting] ?? builder.Configuration[Marketplace.CatalogVariable]));
        builder.Services.AddSingleton(sp => new Marketplace(
            sp.GetRequiredService<IMarketplaceFeed>(),
            () => sp.GetRequiredService<TenantSettings>().MarketplaceCheck,
            () => DateTimeOffset.UtcNow,
            () => sp.GetRequiredService<TeamRegistry>().All()
                .Where(team => team.Solution is not null)
                .Select(team => (team.Id, team.Solution!)),
            id => sp.GetRequiredService<PluginCatalog>().For(id)?.Manifest.Version,
            sp.GetRequiredService<ILogger<Marketplace>>(),
            // A custom-<id> provider in the catalog's needs is said by the name a person gave it here.
            async ct => (await sp.GetRequiredService<ConnectionStore>().ProvidersAsync(ct))
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .ToDictionary(p => p.Id, p => p.Name!, StringComparer.Ordinal)));
        builder.Services.AddHostedService<MarketplaceLoop>();
        builder.Services.AddSingleton<IOAuthEndpoints>(sp => new HttpOAuthEndpoints(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(HttpOAuthEndpoints))));
        // A MAILBOX's login test: IMAP and SMTP over a socket in the Host, no process. Tests replace it with
        // one pointed at a fake server in the test process.
        builder.Services.AddSingleton<IMailLogin>(new SocketMailLogin());
        builder.Services.AddSingleton(sp => new Connections(
            sp.GetRequiredService<ConnectionStore>(), sp.GetRequiredService<IOAuthEndpoints>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IUserStore>(), mail: sp.GetRequiredService<IMailLogin>(),
            // THE INSTANCE'S MICROSOFT CLIENT: the public client the operator CLI names (Connections:MicrosoftClientId,
            // or HARNESS_MICROSOFT_CLIENT_ID), used while no Microsoft client is saved; with none, Microsoft
            // waits for a person's own app.
            microsoftClientId: builder.Configuration[Connections.MicrosoftClientSetting] ?? builder.Configuration[Connections.MicrosoftClientVariable]));
        builder.Services.AddSingleton(sp => new ConnectRequests(
            sp.GetRequiredService<Connections>(), dataRoot, sp.GetRequiredService<ILogger<ConnectRequests>>(),
            sp.GetRequiredService<TeamRegistry>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<ConnectRequests>());

        if (unmeasuredHeadlessPresets.Count > 0)
        {
            Console.WriteLine(
                $"No usage format for Headless presets {string.Join(", ", unmeasuredHeadlessPresets)} - "
                + "runs on them stay (unknown) until usageFormat is set in the Agents screen.");
        }

        // Check for rooted file names and warn about them - still serve them.
        foreach (var agent in loadedCatalog)
        {
            var fileName = agent.Launch.FileName;
            if (fileName is not null && fileName.IndexOfAny(['\\', '/', ':']) >= 0)
            {
                Console.WriteLine(
                    $"WARNING: Agent '{agent.Name}' has a rooted fileName '{fileName}' - put the executable on PATH and name it. Serving anyway.");
            }
        }

        // The operator's tags for a built-in, and whether a member's CLI may sign its commits, are read
        // through their settings on every use.
        var agentCatalog = new AgentCatalog(
            loadedCatalog, () => tenantSettings.AgentTags, () => tenantSettings.AgentsCommitAttribution);
        builder.Services.AddSingleton(agentCatalog);

        // SOLUTION PACKAGES are checked against this Host's own catalogs - its Agent presets, its events
        // (installed plugins' included) and its runtimes - through one service the route, the install and the
        // board's notice share. See docs/solutions.md.
        builder.Services.AddSingleton(sp => new SolutionService(
            new SolutionChecker(SolutionPlatform.For(sp.GetRequiredService<AgentCatalog>(), pluginCatalog)), dataRoot,

            // A LINK opens only the instance's documents and the teams' folders.
            () =>
            {
                var registry = sp.GetRequiredService<TeamRegistry>();
                var paths = sp.GetRequiredService<TeamPaths>();
                return registry.All().Select(t => paths.RootFor(t.Id)).Prepend(paths.TenantDocuments);
            }));
    }
}
