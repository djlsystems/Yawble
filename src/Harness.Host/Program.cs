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

// THE FIRST STATEMENT IN THE HOST, and it has to be: everything printed before it reaches a
// terminal and nowhere else. The host's own stdout is captured to a FILE, because a
// diagnostics TABLE cannot record the failure that stops the process writing to a table. Without
// it the only host output on the machine is a window: close it and the evidence is gone, leave it
// open and the scrollback is bounded.
//
// It COPIES the console rather than replacing it - every write reaches the terminal first and the
// file second - and it cannot open the file yet, because the data root is not known for another
// hundred lines. So it holds these lines in memory and `OpenIn` replays them. See HostLog.
//
// BEFORE CreateBuilder as well, so the console logging provider - constructed with whatever
// `Console.Out` is at the time - is built on the copy and an unhandled exception logged to the
// console lands in the file too.
var hostLog = HostLog.Tee();

using var composition = new HostComposition(args, hostLog);
if (!composition.Boot()) return;
if (!await composition.OperatorAsync()) return;
if (!await composition.StoresAsync()) return;
composition.Auth();
composition.Agents();
await composition.SolutionsAsync();
composition.Admission();
composition.Containers();
composition.Concierge();
composition.Sweeps();
composition.Build();
if (!await composition.StartupAsync()) return;
composition.Pipeline();
composition.Workers();
composition.Push();
composition.MapEndpoints();
composition.Run();

/// <summary>
/// Public so WebApplicationFactory&lt;Program&gt; can find it. Note the asymmetry with
/// Harness.Cli, whose entry point is a differently-named class for exactly this reason: a project
/// referencing both must be able to resolve which Program it meant.
/// </summary>
public partial class Program
{
    /// <summary>What the skill migration did, said once at the start that did it.</summary>
    public static void ReportSkillMigration(SkillMigration.Report report, string backups)
    {
        if (report.ReplacedByBuiltIn.Count > 0)
        {
            Console.WriteLine(
                $"NOTE: {report.ReplacedByBuiltIn.Count} skill file(s) on disk are now built-in skills "
                + $"from this build: {string.Join(", ", report.ReplacedByBuiltIn)}. The old files are in {backups}.");
        }

        if (report.Imported.Count > 0)
        {
            Console.WriteLine(
                $"NOTE: {report.Imported.Count} skill(s) on disk became custom skills: "
                + $"{string.Join(", ", report.Imported)}. The old files are in {backups}.");
        }

        if (report.NotMigrated.Count > 0)
        {
            Console.WriteLine(
                $"NOTE: {string.Join(", ", report.NotMigrated)} was retired and not migrated. The old "
                + $"files are in {backups}.");
        }

        foreach (var warning in report.Warnings)
        {
            Console.WriteLine($"WARNING: skill migration {warning}");
        }
    }

    /// <summary>
    /// Headless presets whose runs will report `(unknown)` usage, for the startup line.
    ///
    /// NON-MODEL PRESETS ARE EXEMPT. A program has no tokens by construction, so naming it here is a
    /// non-problem stated on every start forever - and a report that always fires is one people stop
    /// reading, which costs the real entries their only reader.
    /// </summary>
    public static IReadOnlyList<string> UnmeasuredHeadlessPresets(IReadOnlyList<AgentDefinition> agents) =>
        agents
            .Where(a => a.Mode == AgentMode.Headless
                        && a.Launch.LanguageModel
                        && a.Launch.UsageFormat is null)
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(a => a.Name)
            .ToList();

    /// <summary>
    /// Existing members holding a high-volume subscription on a preset that is now a language
    /// model, for the startup line - reachable only from data written before the rule existed, or
    /// hand-edited in. WARNED ABOUT AND SERVED, never refused: a guard fails in whichever direction
    /// is recoverable, and there is nothing wrong with this data, only with what someone may do
    /// next - the same reasoning `RestoreAsync` follows for an illegal `teams` row.
    /// </summary>
    public static IReadOnlyList<string> FirehoseHolders(
        IReadOnlyList<AgentDefinition> agents, IReadOnlyCollection<TeamSummary> teams)
    {
        // DistinctBy first: two presets whose names differ only by case must not throw out of
        // ToDictionary here. This is a hand-edited `agents.json`'s one door `PUT /api/agents`
        // cannot close, and the method's own contract is WARN AND SERVE, never refuse - an
        // unhandled ArgumentException at startup would stop the Host before the Agents screen
        // that could fix the duplicate is ever served.
        var byName = agents
            .DistinctBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(a => a.Name, StringComparer.OrdinalIgnoreCase);

        return teams
            .SelectMany(t => t.Containers.Select(c => (Team: t.Name, Container: c)))
            .Where(entry => byName.GetValueOrDefault(entry.Container.Agent) is { Launch.LanguageModel: true })
            .SelectMany(entry => entry.Container.Subscribes
                .Where(EventCatalog.IsHighVolume)
                .Select(type => (entry.Team, entry.Container, Type: type)))
            .OrderBy(entry => entry.Team, StringComparer.OrdinalIgnoreCase)
            .ThenBy(entry => entry.Container.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => $"{entry.Team}/{entry.Container.Name} ({entry.Type})")
            .ToList();
    }
}
