using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The Agents screen's version line comes from the route, and the route reads it from the one CLI
/// version record the operator CLI's `doctor` and `agents` read. `GET /api/agents` gives a person each preset's
/// `cliVersions` entry - version, when it last changed, and who brought it - and a machine principal
/// none; `POST /api/agents/{name}/update` answers with the entry as it stands after its own line.
/// </summary>
public sealed class AgentCliVersionRouteTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_person_reads_each_presets_version_when_it_changed_and_who_brought_it()
    {
        var history = host.Services.GetRequiredService<CliVersionHistory>();

        // 2.1.285 arrived by a person's update, and the next start kept it; codex's version could not
        // be read at the newest start.
        await File.WriteAllLinesAsync(history.Path,
        [
            """{"at":"2026-09-25T15:22:00Z","versions":{"claude":"2.1.284 (Claude Code)","codex":"codex-cli 0.157.0"}}""",
            """{"at":"2026-09-29T20:17:30Z","versions":{"claude":"2.1.285 (Claude Code)","codex":"codex-cli 0.157.0"},"by":"update","person":"person@example.test"}""",
            """{"at":"2026-09-30T08:00:00Z","versions":{"claude":"2.1.285 (Claude Code)","codex":null}}""",
        ], Ct);

        using var person = await host.PersonAsync();
        var body = await person.GetFromJsonAsync<JsonElement>("/api/agents", Ct);
        var entries = body.GetProperty("cliVersions").EnumerateArray()
            .ToDictionary(e => e.GetProperty("agent").GetString()!);

        var claude = entries["claude-headless"];
        Assert.Equal("claude", claude.GetProperty("cli").GetString());
        Assert.Equal("2.1.285 (Claude Code)", claude.GetProperty("version").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-09-29T20:17:30Z"), claude.GetProperty("updatedAt").GetDateTimeOffset());
        Assert.Equal("person", claude.GetProperty("updatedBy").GetString());
        Assert.Equal("person@example.test", claude.GetProperty("person").GetString());
        Assert.Equal(DateTimeOffset.Parse("2026-09-25T15:22:00Z"), claude.GetProperty("since").GetDateTimeOffset());

        // Not read at the newest start: null, never the last version that was.
        var codex = entries.Values.First(e => e.GetProperty("cli").GetString() == "codex");
        Assert.Equal(JsonValueKind.Null, codex.GetProperty("version").ValueKind);
        Assert.Equal(JsonValueKind.Null, codex.GetProperty("updatedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, codex.GetProperty("updatedBy").ValueKind);

        // Every built-in preset that launches a command has an entry.
        Assert.All(
            AgentCatalogFile.BuiltIns().Where(a => a.Launch is not null),
            preset => Assert.True(entries.ContainsKey(preset.Name), preset.Name));

        // A machine principal gets none: it names the tooling every member runs on.
        using var container = host.Container(host.AlphaContainerKey);
        var redacted = await container.GetFromJsonAsync<JsonElement>("/api/agents", Ct);
        Assert.False(redacted.TryGetProperty("cliVersions", out _));
    }

    [Fact]
    public async Task A_version_that_is_not_known_has_no_update_time_and_no_one_who_brought_it()
    {
        var history = host.Services.GetRequiredService<CliVersionHistory>();

        // copilot and codex had versions, then the newest start could not read them: copilot's key is
        // missing, codex's is null. The start where they went missing is not an update of anything.
        await File.WriteAllLinesAsync(history.Path,
        [
            """{"at":"2026-09-25T15:22:00Z","versions":{"claude":"2.1.286 (Claude Code)","copilot":"GitHub Copilot CLI 1.0.88","codex":"codex-cli 0.157.0"}}""",
            """{"at":"2026-09-29T20:17:30Z","versions":{"claude":"2.1.286 (Claude Code)","copilot":"GitHub Copilot CLI 1.0.89","codex":"codex-cli 0.157.0"},"by":"update","person":"person@example.test"}""",
            """{"at":"2026-09-30T05:00:00Z","versions":{"claude":"2.1.286 (Claude Code)","codex":null}}""",
        ], Ct);

        using var person = await host.PersonAsync();
        var body = await person.GetFromJsonAsync<JsonElement>("/api/agents", Ct);
        var entries = body.GetProperty("cliVersions").EnumerateArray().ToList();

        foreach (var cli in new[] { "copilot", "codex" })
        {
            var entry = entries.First(e => e.GetProperty("cli").GetString() == cli);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("version").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("updatedAt").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("updatedBy").ValueKind);
            Assert.Equal(JsonValueKind.Null, entry.GetProperty("person").ValueKind);
            // How far back the record looked is still true of a version that is not known.
            Assert.Equal(DateTimeOffset.Parse("2026-09-25T15:22:00Z"), entry.GetProperty("since").GetDateTimeOffset());
        }
    }

    [Fact]
    public async Task The_update_route_answers_with_the_entry_after_its_own_line_and_records_the_person()
    {
        var history = host.Services.GetRequiredService<CliVersionHistory>();

        // A preset's command must be a name on PATH, so the stand-in CLI is coreutils' `echo`: its
        // `--version` answers a version, and `true` is its update. The record starts at an older one.
        await File.WriteAllLinesAsync(history.Path,
            ["""{"at":"2026-09-25T15:22:00Z","versions":{"echo":"echo 0.1"}}"""], Ct);

        using var person = await host.PersonAsync();
        var custom = new AgentDefinition(
            "stub-updatable", AgentMode.Headless, new AgentLaunch("echo", [], LanguageModel: false),
            Updates: new AgentUpdates(new Dictionary<string, string>(), Update: ["true"]));
        var saved = await person.PutAsJsonAsync("/api/agents", new { agents = new[] { custom } }, Ct);
        Assert.True(saved.StatusCode == HttpStatusCode.NoContent, await saved.Content.ReadAsStringAsync(Ct));

        // First update: the version read now differs from the record's, so it arrived with this line.
        var first = await UpdateAsync(person);
        Assert.True(first.GetProperty("updated").GetBoolean(), first.GetProperty("detail").GetString());
        var version = first.GetProperty("versionAfter").GetString();
        Assert.False(string.IsNullOrEmpty(version));

        var entry = first.GetProperty("cliVersion");
        Assert.Equal("echo", entry.GetProperty("cli").GetString());
        Assert.Equal(version, entry.GetProperty("version").GetString());
        Assert.Equal("person", entry.GetProperty("updatedBy").GetString());
        Assert.Equal("person@example.test", entry.GetProperty("person").GetString());
        var arrived = entry.GetProperty("updatedAt").GetDateTimeOffset();

        var newest = Assert.Single(await history.ReadAsync(1, Ct));
        Assert.Equal("update", newest.By);
        Assert.Equal("person@example.test", newest.Person);

        // Second update: nothing changed, so the entry still says it arrived with the first.
        var second = await UpdateAsync(person);
        Assert.Equal(second.GetProperty("versionBefore").GetString(), second.GetProperty("versionAfter").GetString());
        Assert.Equal(arrived, second.GetProperty("cliVersion").GetProperty("updatedAt").GetDateTimeOffset());

        // A machine principal may not update.
        using var container = host.Container(host.AlphaContainerKey);
        var refused = await container.PostAsync("/api/agents/stub-updatable/update", null, Ct);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task An_update_asked_while_a_run_is_in_flight_answers_at_once_waiting_on_that_run_and_reads_the_same_when_the_dialog_reopens()
    {
        using var person = await host.PersonAsync();
        await SaveStubAsync(person, "stub-waiting", "printf");
        var gate = host.Services.GetRequiredService<AgentUpdateGate>();

        // alpha/worker holds a share of printf's install: a run in flight.
        var inFlight = await gate.EnterRunAsync("printf", null, Ct, new AgentRunHolder("alpha", "worker"));

        // ANSWERED AT ONCE, 202, with the gate's own state: waiting for that run, naming it.
        var answer = await person.PostAsync("/api/agents/stub-waiting/update", null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, answer.StatusCode);
        var asked = await answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
        AssertWaitingOnAlphaWorker(asked);

        // Closing the dialog cancels nothing; opening it again reads the same state.
        var reopened = await person.GetFromJsonAsync<JsonElement>("/api/agents/stub-waiting/update", Ct);
        AssertWaitingOnAlphaWorker(reopened);
        Assert.Equal(asked.GetProperty("requestedAt").GetDateTimeOffset(), reopened.GetProperty("requestedAt").GetDateTimeOffset());

        // A launch arriving now is held, and the list the board reads names it.
        var held = gate.EnterRunAsync("printf", null, Ct, new AgentRunHolder("beta", "builder"));
        await UntilAsync(async () => (await person.GetFromJsonAsync<JsonElement>("/api/agents/updates", Ct))
            .EnumerateArray().Any(s => s.GetProperty("command").GetString() == "printf"
                && s.GetProperty("held").GetArrayLength() == 1));
        Assert.False(held.IsCompleted);

        // A machine principal may neither read nor cancel it.
        using var container = host.Container(host.AlphaContainerKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await container.GetAsync("/api/agents/stub-waiting/update", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await container.DeleteAsync("/api/agents/stub-waiting/update", Ct)).StatusCode);

        // Cancel removes the waiting update and releases the launch it held.
        var cancel = await person.DeleteAsync("/api/agents/stub-waiting/update", Ct);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        var cancelled = await cancel.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("cancelled", cancelled.GetProperty("phase").GetString());
        Assert.Equal("person@example.test", cancelled.GetProperty("cancelledBy").GetString());
        using (await held.WaitAsync(TimeSpan.FromSeconds(10), Ct)) { }
        Assert.False(gate.Updating("printf"));
        Assert.Equal("cancelled", (await person.GetFromJsonAsync<JsonElement>("/api/agents/stub-waiting/update", Ct))
            .GetProperty("phase").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await person.DeleteAsync("/api/agents/stub-waiting/update", Ct)).StatusCode);

        // Asked again: it waits for the run, then updates, then says what it measured.
        Assert.Equal(HttpStatusCode.Accepted, (await person.PostAsync("/api/agents/stub-waiting/update", null, Ct)).StatusCode);
        Assert.Equal("waiting", (await person.GetFromJsonAsync<JsonElement>("/api/agents/stub-waiting/update", Ct))
            .GetProperty("phase").GetString());
        inFlight.Dispose();

        var done = await DoneAsync(person, "stub-waiting");
        Assert.True(done.GetProperty("result").GetProperty("updated").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, done.GetProperty("startedAt").ValueKind);
    }

    [Fact]
    public async Task A_running_update_cannot_be_cancelled()
    {
        using var person = await host.PersonAsync();
        var gate = host.Services.GetRequiredService<AgentUpdateGate>();
        var go = new TaskCompletionSource<AgentUpdateResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Nothing in flight, so the update runs at once, and waits here until let go.
        gate.Request("sleep", "stub-running", "person@example.test", _ => go.Task);
        await SaveStubAsync(person, "stub-running", "sleep");

        var cancel = await person.DeleteAsync("/api/agents/stub-running/update", Ct);
        Assert.Equal(HttpStatusCode.Conflict, cancel.StatusCode);
        Assert.Contains("cannot be cancelled", (await cancel.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString());
        Assert.Equal("updating", (await person.GetFromJsonAsync<JsonElement>("/api/agents/stub-running/update", Ct))
            .GetProperty("phase").GetString());

        go.SetResult(new AgentUpdateResult("stub-running", "sleep", true, 0, "1", "1", DateTimeOffset.UtcNow, "ran"));
        Assert.Equal("done", (await DoneAsync(person, "stub-running")).GetProperty("phase").GetString());
    }

    private static void AssertWaitingOnAlphaWorker(JsonElement state)
    {
        Assert.Equal("waiting", state.GetProperty("phase").GetString());
        Assert.Equal("printf", state.GetProperty("command").GetString());
        Assert.Equal(1, state.GetProperty("running").GetInt32());
        var run = Assert.Single(state.GetProperty("inFlight").EnumerateArray());
        Assert.Equal("alpha", run.GetProperty("team").GetString());
        Assert.Equal("worker", run.GetProperty("member").GetString());
        Assert.Equal("person@example.test", state.GetProperty("requestedBy").GetString());
    }

    private static async Task SaveStubAsync(HttpClient person, string name, string command)
    {
        var custom = new AgentDefinition(
            name, AgentMode.Headless, new AgentLaunch(command, [], LanguageModel: false),
            Updates: new AgentUpdates(new Dictionary<string, string>(), Update: ["true"]));
        var saved = await person.PutAsJsonAsync("/api/agents", new { agents = new[] { custom } }, Ct);
        Assert.True(saved.StatusCode == HttpStatusCode.NoContent, await saved.Content.ReadAsStringAsync(Ct));
    }

    private static async Task<JsonElement> UpdateAsync(HttpClient person)
    {
        // Answered at once; the outcome is read back, as the row polls it.
        var answer = await person.PostAsync("/api/agents/stub-updatable/update", null, Ct);
        Assert.Equal(HttpStatusCode.Accepted, answer.StatusCode);
        return (await DoneAsync(person, "stub-updatable")).GetProperty("result");
    }

    private static async Task<JsonElement> DoneAsync(HttpClient person, string name)
    {
        JsonElement state = default;
        await UntilAsync(async () =>
        {
            state = await person.GetFromJsonAsync<JsonElement>($"/api/agents/{name}/update", Ct);
            return state.GetProperty("phase").GetString() is "done" or "failed";
        });
        return state;
    }

    private static async Task UntilAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("The condition did not hold in time.");
            await Task.Delay(50, Ct);
        }
    }
}
