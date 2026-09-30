using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The Agents screen's version line comes from the route, and the route reads it from the one CLI
/// version record the doctor and `yawble agents` read. `GET /api/agents` gives a person each preset's
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
        Assert.Equal("start", codex.GetProperty("updatedBy").GetString());

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

    private static async Task<JsonElement> UpdateAsync(HttpClient person)
    {
        var answer = await person.PostAsync("/api/agents/stub-updatable/update", null, Ct);
        Assert.Equal(HttpStatusCode.OK, answer.StatusCode);
        return await answer.Content.ReadFromJsonAsync<JsonElement>(Ct);
    }
}
