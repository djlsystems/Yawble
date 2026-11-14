using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yawble.Core.Bus;
using Yawble.Core.Connections;
using Yawble.Core.Integrations;
using Yawble.Core.Ipc;
using Yawble.Core.Link;
using Yawble.Core.Provenance;
using Yawble.Core.Tasks;
using Yawble.Core.Tests.Bus;
using Xunit;

namespace Yawble.Core.Tests.Tasks;

/// <summary>
/// B023: a host task's environment variable from a Connection (GH_TOKEN for a release script). Fakes only: an in-memory credential store,
/// the temp checkout and script of <see cref="TaskFixture"/>; no real gh and no network.
/// </summary>
public class HostTaskEnvBindingTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Token = "release-token-value-0123456789";
    private const string ConnName = "GitHub release (Yawble)";

    private static long AddConnection(TaskFixture f, string name = ConnName, string value = Token) =>
        f.Conns!.Save(null, name, [new ConnectionField("token", "Token", true, null), new ConnectionField("user", "User", false, "release-bot")],
            new Dictionary<string, string> { ["token"] = value }).Id;

    private static void ReplaceToken(TaskFixture f, long id, string value) =>
        f.Conns!.Save(id, f.Conns.Get(id)!.Name, [new ConnectionField("token", "Token", true, null), new ConnectionField("user", "User", false, "release-bot")],
            new Dictionary<string, string> { ["token"] = value });

    private static EnvBinding Bind(long id, string name = "GH_TOKEN", string field = "token") => new(name, id.ToString(), field, "");

    /// <summary>The fixture's script, printing what the child got: the value's length (proof it is there) and the value itself (to be masked).</summary>
    private static void PrintTokenScript(TaskFixture f)
    {
        f.Write(f.ScriptPath, f.Win
            ? "Write-Output \"len=$($env:GH_TOKEN.Length)\"; Write-Output \"value=[$env:GH_TOKEN]\""
            : "echo \"len=${#GH_TOKEN}\"; echo \"value=[$GH_TOKEN]\"");
        f.Commit("print the token");
    }

    private static HostTaskDef BoundDef(TaskFixture f, long id, string name = "release") => f.Def(name) with { EnvFromConnections = [Bind(id)] };

    /// <summary>Every text cell of the tables a value must never reach: asks, the call log (audit), run records, tasks.</summary>
    private static string Dump(TaskFixture f)
    {
        using var c = new SqliteConnection(f.Db.ConnectionString);
        c.Open();
        var sb = new System.Text.StringBuilder();
        foreach (var table in new[] { "integration_asks", "integration_calls", "host_task_runs", "host_tasks", "host_task_approvals" })
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = $"SELECT * FROM {table};";
            using var r = cmd.ExecuteReader();
            while (r.Read())
                for (var i = 0; i < r.FieldCount; i++)
                    if (!r.IsDBNull(i)) sb.Append(r.GetValue(i)).Append('\n');
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ 1. validation

    [Fact]
    public async Task ValidationRefusesABadName_ADuplicate_ARuntimeName_AnUnknownConnection_AndAFieldThatIsNotASecret()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f);
        var ok = f.Def("release");

        HostTaskRules.Validate(ok with { EnvFromConnections = [Bind(id)] });
        void Refused(HostTaskDef d, string expect) => Assert.Contains(expect, Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(d)).Message);

        Refused(ok with { EnvFromConnections = [Bind(id, "GH-TOKEN")] }, "not an environment variable name");
        Refused(ok with { EnvFromConnections = [Bind(id, "1TOKEN")] }, "not an environment variable name");
        Refused(ok with { EnvNames = ["GH_TOKEN"], EnvFromConnections = [Bind(id)] }, "already passed");
        Refused(ok with { EnvFromConnections = [Bind(id), Bind(id)] }, "twice");
        foreach (var runtime in new[] { "PATH", "HOME", "TERM", "CI", "NO_COLOR", "SystemRoot", "TEMP", "USERPROFILE", "path" })
            Refused(ok with { EnvFromConnections = [Bind(id, runtime)] }, "set by the app");
        Refused(ok with { EnvFromConnections = [Bind(id, "YAWBLE_RUN_TOKEN")] }, "the app's own");
        Refused(ok with { EnvFromConnections = [new EnvBinding("GH_TOKEN", "abc", "token", "")] }, "which Connection");

        // The checks against the Connections: at save (and in the preview).
        var unknown = await Assert.ThrowsAsync<ArgumentException>(() => f.SaveAsync(ok with { EnvFromConnections = [Bind(id + 99)] }));
        Assert.Contains("no Connection", unknown.Message);
        var plain = await Assert.ThrowsAsync<ArgumentException>(() => f.SaveAsync(ok with { EnvFromConnections = [Bind(id, field: "user")] }));
        Assert.Contains("no secret field 'user'", plain.Message);
        Assert.DoesNotContain(Token, plain.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => f.SaveAsync(ok with { EnvFromConnections = [Bind(id, field: "nope")] }));
        Assert.Empty(f.Svc.Store.Tasks(f.TeamId));

        // Without Connections on the host there is nothing to bind to.
        using var bare = new TaskFixture();
        await Assert.ThrowsAsync<ArgumentException>(() => bare.SaveAsync(bare.Def("release") with { EnvFromConnections = [Bind(1)] }));

        // A good one is saved with the Connection's name for display.
        var saved = await f.SaveAsync(ok with { EnvFromConnections = [Bind(id)] });
        var b = Assert.Single(f.Svc.Store.TaskById(saved.Id)!.Def.EnvFromConnections);
        Assert.Equal(new EnvBinding("GH_TOKEN", id.ToString(), "token", ConnName), b);
        Assert.Equal($"GH_TOKEN ← Connection '{ConnName}' (token)", b.Shown);
    }

    // ------------------------------------------------------------------ 2. pinning

    [Fact]
    public async Task TheBindingIsPinned_ItsValueIsNot_AndATaskWithoutBindingsKeepsItsOldHash()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f);
        var other = AddConnection(f, "Another token", "another-token-value-987654321");
        var plain = f.Def("release");
        var p = await f.Svc.PreviewAsync(f.TeamId, plain, Ct);
        string H(HostTaskDef d) => TaskPinning.Definition(f.TeamId, d, p.ProgramResolved!).Hash;
        var withBinding = plain with { EnvFromConnections = [Bind(id) with { ConnectionName = ConnName }] };

        Assert.DoesNotContain("env_from_connections", TaskPinning.Definition(f.TeamId, plain, p.ProgramResolved!).Json); // tasks from before B023: same hash
        Assert.NotEqual(H(plain), H(withBinding)); // adding
        Assert.NotEqual(H(withBinding), H(plain with { EnvFromConnections = [Bind(other) with { ConnectionName = ConnName }] })); // re-pointing the Connection
        Assert.NotEqual(H(withBinding), H(plain with { EnvFromConnections = [Bind(id, field: "other") with { ConnectionName = ConnName }] })); // another field
        Assert.NotEqual(H(withBinding), H(plain with { EnvFromConnections = [Bind(id, "GITHUB_TOKEN") with { ConnectionName = ConnName }] })); // another name
        var json = TaskPinning.Definition(f.TeamId, withBinding, p.ProgramResolved!).Json;
        Assert.Contains("GH_TOKEN", json);
        Assert.DoesNotContain(Token, json);

        // The flow: approve Always, replace the expired token: no ask, the new value is the one used.
        PrintTokenScript(f);
        await f.SaveAsync(BoundDef(f, id));
        var ask = await f.Request("release");
        Assert.Equal(RequestKind.Asked, ask.Kind);
        var detail = JsonNode.Parse(f.Svc.Store.Ask(ask.AskId!.Value)!.DetailJson!)!;
        Assert.Equal($"GH_TOKEN ← Connection '{ConnName}' (token)", detail["env_from_connections"]![0]!.GetValue<string>());
        await f.Finish(await f.Svc.ApproveAsync(ask.AskId.Value, always: true, Ct));

        const string renewed = "renewed-token-value-abcdefghijklmnop";
        ReplaceToken(f, id, renewed);
        var again = await f.Request("release");
        Assert.Equal(RequestKind.Started, again.Kind); // the value is not pinned
        await f.Finish(again);
        Assert.Contains($"len={renewed.Length}", f.Wakes[^1].Text);

        // Re-pointing the binding is a changed definition: it asks again.
        await f.SaveAsync(f.Def("release") with { EnvFromConnections = [Bind(other)] });
        Assert.Equal(RequestKind.Asked, (await f.Request("release")).Kind);
        Assert.DoesNotContain(renewed, Dump(f));
    }

    // ------------------------------------------------------------------ 3-4. the run, masking, no value anywhere

    [Fact]
    public async Task TheRunGetsTheValue_TheLogAndTheWakeMaskIt_AndNoRecordHoldsIt()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f);
        PrintTokenScript(f);
        await f.SaveAsync(BoundDef(f, id));

        var ask = await f.Request("release");
        Assert.DoesNotContain(Token, ask.Text);
        var started = await f.Svc.ApproveAsync(ask.AskId!.Value, always: false, Ct);
        Assert.DoesNotContain(Token, started.Text);
        var run = await f.Finish(started);
        Assert.Equal(0, run.ExitCode);
        Assert.Null(run.MissingEnv);

        var wake = Assert.Single(f.Wakes).Text;
        Assert.Contains($"len={Token.Length}", wake); // the child had it
        Assert.DoesNotContain(Token, wake);
        Assert.Contains("value=[", wake);
        var log = File.ReadAllText(run.LogPath!);
        Assert.Contains($"len={Token.Length}", log);
        Assert.DoesNotContain(Token, log);

        // The agent's own surfaces and every record: names, never the value.
        var rpc = new HostTaskRpc(f.Svc, _ => ["main"]);
        JsonNode? list;
        using (Actor.Use(Actor.Window)) list = await rpc.DispatchAsync("tasks.list", new JsonObject { ["teamId"] = f.TeamId }, Ct);
        var row = list!["tasks"]![0]!;
        Assert.Equal(id.ToString(), row["def"]!["envFromConnections"]![0]!["connectionId"]!.GetValue<string>());
        Assert.Equal(ConnName, row["def"]!["envFromConnections"]![0]!["connectionName"]!.GetValue<string>());
        Assert.Equal($"GH_TOKEN ← Connection '{ConnName}' (token)", row["envFromConnectionsShown"]![0]!.GetValue<string>());
        Assert.DoesNotContain(Token, list.ToJsonString());
        Assert.DoesNotContain(Token, HostTaskRpc.RunJson(run).ToJsonString());
        Assert.DoesNotContain(Token, Dump(f));
    }

    [Fact]
    public async Task AMissingAnEmptyOrANeedsReconnectConnectionRefusesTheStart_NamingTheBindingWithoutTheValue()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f);
        PrintTokenScript(f);
        await f.SaveAsync(BoundDef(f, id));
        await f.Finish(await f.Svc.ApproveAsync((await f.Request("release")).AskId!.Value, always: true, Ct));
        f.Wakes.Clear();

        async Task Refused(string why)
        {
            var before = f.Svc.Store.Runs(1, 100).Count;
            var r = await f.Request("release");
            Assert.Equal(RequestKind.Refused, r.Kind);
            Assert.Equal(9, r.Precondition);
            Assert.Contains("GH_TOKEN", r.Text);
            Assert.Contains(ConnName, r.Text);
            Assert.Contains(why, r.Text);
            Assert.DoesNotContain(Token, r.Text);
            Assert.Empty(f.Svc.Store.PendingAsks());
            var runs = f.Svc.Store.Runs(1, 100);
            Assert.Equal(before + 1, runs.Count);
            Assert.Equal("refused", runs[0].State);
            Assert.Null(runs[0].ExitCode);
            Assert.Null(runs[0].LogPath); // nothing started
            Assert.Contains("GH_TOKEN", runs[0].MissingEnv);
            Assert.Contains(why, runs[0].MissingEnv);
            var now = await f.Svc.RunNowAsync(f.TeamId, "release", Ct); // the person's Run now is refused the same way
            Assert.Equal(9, now.Precondition);
        }

        f.Conns!.MarkStatus(id, "needs_reconnect", "the token expired");
        await Refused("needs reconnecting");
        f.Conns.MarkStatus(id, "works", null);

        foreach (var key in f.Creds!.Keys.Where(k => k.StartsWith("connection/", StringComparison.Ordinal)).ToList()) f.Creds.Delete(key);
        await Refused("field 'token' is empty");
        ReplaceToken(f, id, Token);
        Assert.Equal(RequestKind.Started, (await RequestAndFinish(f)).Kind); // back: runs on the standing approval

        Assert.True(f.Conns.Remove(id)); // removal is allowed...
        await Refused("removed"); // ...and the task then refuses to start
        Assert.DoesNotContain(f.Wakes, w => w.Text.Contains(Token));
        Assert.DoesNotContain(Token, Dump(f));
    }

    [Fact]
    public async Task AnAskApprovedAfterTheConnectionWentAwayRunsNothing()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f);
        await f.SaveAsync(BoundDef(f, id));
        var ask = await f.Request("release");
        f.Conns!.Remove(id);
        var r = await f.Svc.ApproveAsync(ask.AskId!.Value, always: true, Ct);
        Assert.Equal(9, r.Precondition);
        Assert.Null(f.Svc.Store.Approval(1));
        Assert.Equal("declined", f.Svc.Store.Ask(ask.AskId.Value)!.State);
        Assert.Contains(f.Wakes, w => w.Text.Contains("nothing ran") && w.Text.Contains("GH_TOKEN"));
        Assert.Equal("refused", f.Svc.Store.Runs(1)[0].State);
    }

    private static async Task<TaskRequestResult> RequestAndFinish(TaskFixture f)
    {
        var r = await f.Request("release");
        if (r.RunId is { } run) await f.Svc.WaitForRunAsync(run);
        return r;
    }

    // ------------------------------------------------------------------ 5. the Connection in use

    [Fact]
    public async Task TheConnectionsListSaysWhichTasksUseIt_AndRemovalAsksThenIsAllowed()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f);
        var unused = AddConnection(f, "Unused", "unused-token-value-5555555555");
        await f.SaveAsync(BoundDef(f, id, "release"));
        await f.SaveAsync(BoundDef(f, id, "publish"));
        await f.SaveAsync(f.Def("build"));

        Assert.Equal([new ConnectionTaskUse(f.TeamId, "publish"), new ConnectionTaskUse(f.TeamId, "release")], f.Conns!.UsedByTasks(id));
        Assert.Empty(f.Conns.UsedByTasks(unused));

        await using var engine = new BusEngine(f.Db.OpenStore(), new ScriptedRunner(), new BusOptions { IdleOffer = false });
        await using var integrations = new IntegrationService(f.Db.ConnectionString, engine, connections: f.Conns);
        var rows = (await integrations.DispatchAsync("integrations.connections", null, Ct))!.AsArray();
        var used = rows.Single(r => r!["id"]!.GetValue<long>() == id)!["usedByTasks"]!.AsArray();
        Assert.Equal(["publish", "release"], used.Select(u => u!["taskName"]!.GetValue<string>()));
        Assert.All(used, u => Assert.Equal(f.TeamId, u!["teamId"]!.GetValue<long>()));
        Assert.Empty(rows.Single(r => r!["id"]!.GetValue<long>() == unused)!["usedByTasks"]!.AsArray());
        Assert.DoesNotContain(Token, rows.ToJsonString());

        var first = (await integrations.DispatchAsync("integrations.connectionRemove", new JsonObject { ["id"] = id }, Ct))!;
        Assert.False(first["ok"]!.GetValue<bool>()); // the in-use warning first
        Assert.Equal(2, first["usedByTasks"]!.AsArray().Count);
        Assert.NotNull(f.Conns.Get(id));
        var confirmed = (await integrations.DispatchAsync("integrations.connectionRemove", new JsonObject { ["id"] = id, ["confirm"] = true }, Ct))!;
        Assert.True(confirmed["ok"]!.GetValue<bool>());
        Assert.Null(f.Conns.Get(id));
        Assert.Equal(9, (await f.Request("release")).Precondition);
    }

    // ------------------------------------------------------------------ 6. Cloud Link

    [Fact]
    public async Task ACloudLinkViewerCannotDefineOrChangeBindings()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddConnection(f);
        var rpc = new HostTaskRpc(f.Svc, _ => ["main"]);
        JsonObject Save(JsonArray? bindings, int timeout = 60)
        {
            var def = new JsonObject
            {
                ["name"] = "release", ["program"] = f.Win ? "powershell" : "sh",
                ["args"] = new JsonArray(f.Def().Args.Select(a => (JsonNode?)a).ToArray()), ["timeoutSeconds"] = timeout,
            };
            if (bindings is not null) def["envFromConnections"] = bindings;
            return new JsonObject { ["teamId"] = f.TeamId, ["def"] = def };
        }
        JsonArray Binding(long cid) => [new JsonObject { ["name"] = "GH_TOKEN", ["connectionId"] = cid, ["field"] = "token" }];

        // With host terminals off, the Cloud Link refuses tasks.save before it reaches the core, bindings or not.
        Assert.NotNull(RemoteAccess.Refusal("tasks.save", Save(Binding(id)), allowHostTerminals: false));

        using (Actor.Use(Actor.CloudViewer("remote")))
        {
            var e = await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("tasks.save", Save(Binding(id)), Ct));
            Assert.Equal("forbidden", e.Code);
            Assert.Contains("on the computer itself", e.Message);
            Assert.Empty(f.Svc.Store.Tasks(f.TeamId));
            await rpc.DispatchAsync("tasks.save", Save(null), Ct); // a task without bindings: as before
        }

        using (Actor.Use(Actor.Window)) await rpc.DispatchAsync("tasks.save", Save(Binding(id)), Ct); // at the computer: allowed
        Assert.Single(f.Svc.Store.Task(f.TeamId, "release")!.Def.EnvFromConnections);

        using (Actor.Use(Actor.CloudViewer("remote")))
        {
            await rpc.DispatchAsync("tasks.save", Save(Binding(id), timeout: 90), Ct); // bindings unchanged: another edit is allowed
            Assert.Equal(90, f.Svc.Store.Task(f.TeamId, "release")!.Def.TimeoutSeconds);
            await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("tasks.save", Save(null), Ct)); // removing
            var other = AddConnection(f, "Other", "other-token-value-1111111111");
            await Assert.ThrowsAsync<RpcException>(() => rpc.DispatchAsync("tasks.save", Save(Binding(other)), Ct)); // re-pointing
            Assert.Equal(id.ToString(), f.Svc.Store.Task(f.TeamId, "release")!.Def.EnvFromConnections[0].ConnectionId);
            var listed = await rpc.DispatchAsync("tasks.list", new JsonObject { ["teamId"] = f.TeamId }, Ct); // shown remotely: names only
            Assert.Contains("GH_TOKEN", listed!.ToJsonString());
            Assert.DoesNotContain(Token, listed.ToJsonString());
        }

        var refusedValue = await Assert.ThrowsAsync<RpcException>(() =>
        {
            using var _ = Actor.Use(Actor.Window);
            return rpc.DispatchAsync("tasks.save", Save([new JsonObject { ["name"] = "GH_TOKEN", ["connectionId"] = id, ["field"] = "token", ["value"] = "x" }]), Ct);
        });
        Assert.Equal("bad_params", refusedValue.Code); // a binding never carries a value
    }
}
