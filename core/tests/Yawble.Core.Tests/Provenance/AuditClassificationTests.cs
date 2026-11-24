using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Yawble.Core.Provenance;
using Yawble.Core.Storage;
using Yawble.Core.Tests.Gateway;
using Xunit;

namespace Yawble.Core.Tests.Provenance;

/// <summary>
/// The audit keeps changes only. Every call the core dispatches is either a read (AuditLog.IsReadOnly) or one of the
/// changes listed here; a new call that is neither fails this test, so whoever adds it decides. Without that, a screen's
/// poll whose name did not end in a read verb filled the audit (monitor.codeEvents wrote about one row a second).
/// </summary>
public class AuditClassificationTests
{
    /// <summary>The calls that change something, and so are audited.</summary>
    private static readonly HashSet<string> Changes = new(StringComparer.Ordinal)
    {
        "agentProfiles.delete",
        "agentProfiles.duplicate",
        "agentProfiles.setDefaultModel",
        "agentProfiles.update",
        "agents.install",
        "agents.setDefaultModel",
        "agents.stopTest",
        "agents.test",
        "backlog.add",
        "backlog.archive",
        "backlog.delete",
        "backlog.dispatch",
        "backlog.dispatchNew",
        "backlog.edit",
        "backlog.move",
        "backlog.position",
        "backlog.restore",
        "board.clearDone",
        "board.comment",
        "board.edit",
        "board.move",
        "board.restore",
        "bus.answer",
        "bus.closeWorkflow",
        "bus.nudgeWorkflow",
        "bus.stopWorkflow",
        "bus.tellManager",
        "companion.decideAction",
        "companion.decidePairing",
        "companion.revoke",
        "concierge.delete",
        "concierge.rename",
        "concierge.send",
        "concierge.setDefaults",
        "concierge.setTeam",
        "concierge.setWatch",
        "concierge.stop",
        "concierge.unwatch",
        "credentials.remove",
        "credentials.setAnthropicApiKey",
        "credentials.setClaudeSetupToken",
        "credentials.setMode",
        "credentials.setXaiApiKey",
        "docs.delete",
        "docs.folder",
        "docs.mkdir",
        "docs.rename",
        "docs.upload",
        "docs.write",
        "folder.answer",
        "git.askTeam",
        "git.branchRealign",
        "git.bringCurrent",
        "git.deleteBranches",
        "git.fetch",
        "git.install",
        "git.merge",
        "git.sync",
        "guardrail.start",
        "guardrail.stop",
        "hostTerminal.start",
        "integrations.apiDeleteRule",
        "integrations.apiEnable",
        "integrations.apiRemove",
        "integrations.apiSave",
        "integrations.approve",
        "integrations.emailEnable",
        "integrations.emailRemove",
        "integrations.emailSave",
        "integrations.connectionRemove",
        "integrations.connectionSave",
        "integrations.connectionTest",
        "integrations.decline",
        "integrations.deleteRule",
        "integrations.deleteWatch",
        "integrations.discover",
        "integrations.discoverCancel",
        "integrations.enable",
        "integrations.mcpDeleteRule",
        "integrations.mcpEnable",
        "integrations.mcpImport",
        "integrations.mcpRemove",
        "integrations.mcpSave",
        "integrations.mcpSaveRule",
        "integrations.mcpSignIn",
        "integrations.oauthDiscover",
        "integrations.oauthSignIn",
        "integrations.openLogin",
        "integrations.remove",
        "integrations.runWatch",
        "integrations.saveChecklist",
        "integrations.saveRule",
        "integrations.saveWatch",
        "integrations.websiteSignIn",
        "integrations.websiteSignInDone",
        "link.cancel",
        "link.forget",
        "link.pair",
        "link.setAllowHostTerminals",
        "link.setShowCode",
        "logs.prune",
        "logs.setRetention",
        "members.add",
        "members.remove",
        "members.update",
        "network.answer",
        "ops.backupNow",
        "ops.restore",
        "ops.setKeepDaily",
        "policy.removeTeamHost",
        "policy.saveGlobal",
        "policy.saveLoosen",
        "policy.saveOverride",
        "runs.stop",
        "skills.delete",
        "skills.restore",
        "skills.save",
        // B131: the script library, schemas, Test run and script members.
        "schemas.import", "schemas.save", "scripts.import", "scripts.memberAdd", "scripts.memberResetVariable", "scripts.memberUpdate",
        "scripts.save", "scripts.testRun",
        "tasks.approve",
        "tasks.cancel",
        "tasks.decline",
        "tasks.delete",
        "tasks.revoke",
        "tasks.runNow",
        "tasks.save",
        "teams.addApproval",
        "teams.addRepo",
        "teams.archive",
        "teams.create",
        "teams.delete",
        "teams.generateNames",
        "teams.localhost",
        "teams.pause",
        "teams.push",
        "teams.removeApproval",
        "teams.removeRepo",
        "teams.reset",
        "teams.resume",
        "teams.setDefaults",
        "teams.setEnv",
        "teams.setRepo",
        "teams.syncToBase",
        "teams.unarchive",
        "teams.workflowExpiry",
        "terminal.openInteractive",
        "terminal.stop",
        "tools.checkAgain",
        "tools.install",
        "tools.notNow",
        "triggers.delete",
        "triggers.runNow",
        "triggers.save",
        "updates.download",
        "updates.installer",
        "updates.setAuto",
    };

    /// <summary>Quoted dotted strings in a dispatch switch that are not calls (hosts in a lookup).</summary>
    private static readonly HashSet<string> NotCalls = new(StringComparer.Ordinal) { "api.nuget.org", "registry.npmjs.org" };

    /// <summary>Every name the core's sources dispatch on: <c>"a.b" =&gt;</c> switch arms and <c>case "a.b":</c> labels.</summary>
    private static IReadOnlyList<string> DispatchedCalls()
    {
        var src = Path.Combine(Binaries.RepoRoot, "core", "src", "Yawble.Core");
        var arm = new Regex("""(?:^\s*|case\s+)"([a-z][A-Za-z]*(?:\.[a-z][A-Za-z]*)+)"\s*(?:=>|:)""", RegexOptions.Multiline);
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .SelectMany(f => arm.Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(n => !NotCalls.Contains(n))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
    }

    [Fact]
    public void EveryDispatchedCall_IsEitherARead_OrAListedChange()
    {
        var calls = DispatchedCalls();
        Assert.True(calls.Count > 200, $"the scan found only {calls.Count} calls; has the dispatch syntax changed?");
        var unclassified = calls.Where(c => !AuditLog.IsReadOnly(c) && !Changes.Contains(c)).ToList();
        Assert.True(unclassified.Count == 0,
            "Calls that are neither a read nor a listed change: " + string.Join(", ", unclassified)
            + ". If the call only reads, add it to AuditLog.ReadMethods (and delete its rows in a migration); if it changes something, add it to Changes here.");
        var bothWays = Changes.Where(AuditLog.IsReadOnly).ToList();
        Assert.True(bothWays.Count == 0, "Listed as changes but read-only by name: " + string.Join(", ", bothWays));
    }

    [Theory]
    [InlineData("monitor.codeEvents")]
    [InlineData("logs.tail")]
    [InlineData("logs.untail")]
    [InlineData("org.summary.watch")]
    [InlineData("org.summary.unwatch")]
    [InlineData("integrations.calls")]
    [InlineData("integrations.taskRuns")]
    [InlineData("integrations.mcpParse")]
    [InlineData("integrations.mcpImportScan")]
    [InlineData("integrations.mcpCheck")]
    [InlineData("agents.resolveModel")]
    [InlineData("workflows.list")]
    [InlineData("workflows.get")]
    public void TheScreensPollsAndLookups_AreNotAudited(string method)
    {
        var dir = Directory.CreateTempSubdirectory("yawble-audit-").FullName;
        try
        {
            var cs = $"Data Source={Path.Combine(dir, "yawble.db")};Pooling=False";
            SchemaMigrator.Migrate(cs);
            var audit = new AuditLog(cs);
            audit.Record(new Actor("app", "person"), method, new System.Text.Json.Nodes.JsonObject { ["team"] = "web" }, "done");
            audit.Record(new Actor("app", "person"), "teams.pause", new System.Text.Json.Nodes.JsonObject { ["team"] = "web" }, "done");
            using var c = new SqliteConnection(cs);
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT group_concat(method) FROM audit_log;";
            Assert.Equal("teams.pause", cmd.ExecuteScalar() as string);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }
}
