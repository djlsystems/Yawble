using System.Text.Json;
using Yawble.Core.Launch;
using Yawble.Core.Storage;
using Yawble.Core.Agents;
using Yawble.Core.Policy;

namespace Yawble.Core.Guardrail;

/// <summary>
/// <c>yawble-core guardrail-check --data-dir &lt;app root&gt;/data [--install-dir &lt;install&gt;] [--team T --member M | --cli claude|grok] [--json]</c>.
/// Runs one check in a real sandbox and prints the report (a table, or the JSON the IPC returns). Exit code: 0 when
/// it passed, 1 when any attempt was not as expected or the sandbox refused the request, 2 for a usage or setup problem.
/// Do not run it against a data folder a running core is using at the same moment (one SQLite writer at a time is fine,
/// but two cores are not supported).
/// </summary>
public static class GuardrailCli
{
    public const string Usage =
        "usage: yawble-core guardrail-check --data-dir <app root>/data [--install-dir <install>] [--team <team> --member <member> | --cli claude|grok] [--json]\n" +
        "       (the install folder is --install-dir, else YAWBLE_INSTALL_DIR, else the folder above the core when it has bin/;\n" +
        "        it must hold bin/yawble-guardcheck. The agent CLI is the one installed on this computer, detected now;\n" +
        "        --cli picks it for the check without a member, default claude)";

    public static async Task<int> RunAsync(IReadOnlyList<string> args, Func<string, string?> env, TextWriter stdout, TextWriter stderr,
        ICliDetector? clis = null)
    {
        string? data = null, installDir = null, team = null, member = null, cli = null;
        var json = false;
        for (var i = 0; i < args.Count; i++)
        {
            string? Next() => i + 1 < args.Count ? args[++i] : null;
            switch (args[i])
            {
                case "--data-dir": data = Next(); break;
                case "--install-dir": installDir = Next(); break;
                case "--cli": cli = Next(); break;
                case "--team": team = Next(); break;
                case "--member": member = Next(); break;
                case "--json": json = true; break;
                default:
                    await stderr.WriteLineAsync($"unknown argument: {args[i]}\n{Usage}");
                    return 2;
            }
        }
        if (data is null || (team is null) != (member is null) || (cli is not null && team is not null))
        {
            await stderr.WriteLineAsync(Usage);
            return 2;
        }
        Directory.CreateDirectory(data);
        var cs = $"Data Source={Path.Combine(data, "yawble.db")}";
        SchemaMigrator.Migrate(cs);
        // --cli is a built-in's id or a custom agent's name (the check runs on its base CLI).
        AgentCli syntheticCli = AgentCli.Claude;
        if (cli is not null)
        {
            try { syntheticCli = Agents.Profiles.AgentResolvers.Resolve(Agents.Profiles.AgentProfileStore.ForReading(cs), cli).Cli; }
            catch (ArgumentException)
            {
                await stderr.WriteLineAsync(Usage);
                return 2;
            }
        }
        // The core's own launcher (not a running core's): the run is recorded in run_records like any other. No Grok
        // keeper is started for a one-shot check; the running core's keeper renews logins.
        await using var launch = LaunchWiring.Create(data, cs, clis ?? CliDetectorRegistry.ForThisHost(), installDir, new RunRegistry(), env,
            line => stderr.WriteLine("launch: " + line), startKeeper: false);
        var service = GuardrailService.Create(launch.Launcher, launch.Problem, cs, line => stderr.WriteLine(line));
        if (service.Check is null)
        {
            await stderr.WriteLineAsync("the guardrail check cannot run: " + service.Problem);
            return 2;
        }
        GuardrailReport report;
        try
        {
            report = await service.Check.RunAsync(team, member, syntheticCli: syntheticCli);
        }
        catch (GuardrailException e)
        {
            await stderr.WriteLineAsync($"{e.Code}: {e.Message}");
            return 2;
        }
        await stdout.WriteAsync(json ? JsonSerializer.Serialize(report, GuardrailReport.Json) + "\n" : report.ToText());
        return report.Passed ? 0 : 1;
    }
}
