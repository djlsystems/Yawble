using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A plugin event's <c>monitor</c> hint: how the Monitor draws it. <see cref="PluginManifest"/>
/// accepts a valid one and serves it on `GET /api/events`; refuses each fault naming the event type
/// and the field, in the sentences the operator CLI uses (cli/internal/plugin/manifest_test.go holds
/// the same cases); lists a key it does not know as ignored; and reads a manifest with no hint
/// exactly as before.
/// </summary>
public sealed class PluginMonitorHintTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-monitor-hint-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>Every kind a label or a field reference can meet: two strings, a number, a list, a boolean.</summary>
    private const string Fields =
        """[{"name":"title","kind":"string"},{"name":"status"},{"name":"count","kind":"number"},{"name":"tags","kind":"list"},{"name":"done","kind":"boolean"}]""";

    private static JsonObject WithEvents(string id, string publishes) =>
        PluginInstall.Manifest(id, edit: m => m["events"] = JsonNode.Parse($$"""{"publishes":{{publishes}}}"""));

    private static (PluginManifest? Manifest, string? Refusal) ParseMonitor(string monitor) =>
        PluginManifest.Parse(WithEvents("fx", """[{"type":"saved","fields":""" + Fields + ""","monitor":""" + monitor + "}]").ToJsonString());

    private static readonly string Valid = $$$"""
        [
          {"type":"saved","fields":{{{Fields}}},
           "monitor":{"icon":"inbox","iconBy":{"field":"status","map":{"open":"drafts","closed":"archive"}},
                      "action":"created","label":"{title} saved with {count} items"}},
          {"type":"moved","fields":{{{Fields}}},"monitor":{"icon":"swap_horiz","actionField":"status"}},
          {"type":"plain","fields":{{{Fields}}}}
        ]
        """;

    [Fact]
    public void A_valid_hint_is_accepted_as_declared()
    {
        var (manifest, refusal) = PluginManifest.Parse(WithEvents("fx", Valid).ToJsonString());

        Assert.Null(refusal);
        var saved = manifest!.Publishes.Single(e => e.Type == "saved").Monitor!;
        Assert.Equal("inbox", saved.Icon);
        Assert.Equal("status", saved.IconBy!.Field);
        Assert.Equal("drafts", saved.IconBy.Map["open"]);
        Assert.Equal("archive", saved.IconBy.Map["closed"]);
        Assert.Equal("created", saved.Action);
        Assert.Null(saved.ActionField);
        Assert.Equal("{title} saved with {count} items", saved.Label);

        var moved = manifest.Publishes.Single(e => e.Type == "moved").Monitor!;
        Assert.Equal("swap_horiz", moved.Icon);
        Assert.Null(moved.Action);
        Assert.Equal("status", moved.ActionField);
        Assert.Null(moved.IconBy);
        Assert.Null(moved.Label);

        Assert.Same(saved, manifest.Publishes.Single(e => e.Type == "saved").Definition("fx").Monitor);
        Assert.Empty(manifest.Ignored);
    }

    [Theory]
    [InlineData("created")]
    [InlineData("changed")]
    [InlineData("deleted")]
    public void Each_fixed_action_is_accepted(string action)
    {
        var (manifest, refusal) = ParseMonitor($$"""{"icon":"inbox","action":"{{action}}"}""");

        Assert.Null(refusal);
        Assert.Equal(action, manifest!.Publishes.Single().Monitor!.Action);
    }

    [Fact]
    public void A_label_of_exactly_the_limit_is_accepted_and_one_more_is_refused()
    {
        var limit = "{title}" + new string('x', PluginMonitorHint.MaxLabel - "{title}".Length);
        Assert.Equal(PluginMonitorHint.MaxLabel, limit.Length);

        Assert.Null(ParseMonitor($$"""{"icon":"inbox","action":"created","label":"{{limit}}"}""").Refusal);
        Assert.Equal(
            "`events.publishes` `saved`: `monitor.label` is 121 characters; a label is at most 120.",
            ParseMonitor($$"""{"icon":"inbox","action":"created","label":"{{limit}}y"}""").Refusal);
    }

    /// <summary>The cases the CLI's tests read too: tests/Fixtures/PluginMonitorHints/cases.json.</summary>
    private static readonly JsonNode Cases = JsonNode.Parse(File.ReadAllText(
        Path.Combine(SolutionSamples.RepoRoot(), "tests", "Fixtures", "PluginMonitorHints", "cases.json")))!;

    public static TheoryData<string> Accepted() => [.. Cases["accepted"]!.AsArray().Select(c => c!.ToJsonString())];

    public static TheoryData<string, string> Refused() =>
        [.. Cases["refused"]!.AsArray().Select(c => (c!["monitor"]!.ToJsonString(), c["says"]!.GetValue<string>()))];

    [Fact]
    public void The_shared_cases_declare_the_fields_these_tests_use()
    {
        Assert.Equal(JsonNode.Parse(Fields)!.ToJsonString(), Cases["fields"]!.ToJsonString());
    }

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Each_shared_valid_hint_is_accepted(string monitor)
    {
        var (manifest, refusal) = ParseMonitor(monitor);

        Assert.Null(refusal);
        Assert.NotNull(manifest!.Publishes.Single().Monitor);
    }

    [Theory]
    [MemberData(nameof(Refused))]
    public void Each_fault_is_refused_naming_the_event_type_and_the_field(string monitor, string says)
    {
        var (manifest, refusal) = ParseMonitor(monitor);

        Assert.Null(manifest);
        Assert.Equal("`events.publishes` `saved`: " + says, refusal);
    }

    [Fact]
    public void An_unknown_key_inside_monitor_is_ignored_by_name_and_never_refused()
    {
        var (manifest, refusal) = ParseMonitor("""{"icon":"inbox","action":"created","colour":"teal","pulse":true}""");

        Assert.Null(refusal);
        Assert.Equal(["events.publishes.saved.monitor.colour", "events.publishes.saved.monitor.pulse"], manifest!.Ignored);
        Assert.Equal("inbox", manifest.Publishes.Single().Monitor!.Icon);
    }

    [Fact]
    public void A_manifest_without_monitor_reads_as_before()
    {
        var (manifest, refusal) = PluginManifest.Parse(WithEvents("fx",
            """[{"type":"tick","highVolume":true},{"type":"done","fields":[{"name":"n","kind":"number"}]}]""").ToJsonString());

        Assert.Null(refusal);
        Assert.Empty(manifest!.Ignored);
        Assert.All(manifest.Publishes, e => Assert.Null(e.Monitor));

        var done = manifest.Publishes.Single(e => e.Type == "done").Definition("fx");
        Assert.Null(done.Monitor);
        Assert.True(done.InLedger);
        Assert.False(done.HighVolume);
        Assert.Equal([PayloadFields.Source, "n"], done.Fields.Select(f => f.Name));
    }

    [Fact]
    public void The_plugin_list_names_a_refused_hint()
    {
        PluginInstall.Write(_dataRoot, "fx", manifest: WithEvents("fx", """[{"type":"saved","monitor":{"icon":"inbox"}}]"""));
        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();

        Assert.Empty(catalog.Plugins);
        Assert.Contains(catalog.Refused, r => r.Reason.Contains(
            "`events.publishes` `saved`: `monitor` needs `action` (created, changed or deleted) or `actionField` (a declared string field).",
            StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_hint_is_served_on_GET_api_events_as_declared_and_nothing_else_changes()
    {
        PluginInstall.Write(_dataRoot, "fx", manifest: WithEvents("fx", Valid));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        await factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", "correct horse battery", Ct);
        using var person = factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = "correct horse battery" }, Ct)).EnsureSuccessStatusCode();

        using var events = JsonDocument.Parse(await person.GetStringAsync("/api/events", Ct));
        var byType = events.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("type").GetString()!);

        var saved = byType["plugin.fx.saved"].GetProperty("monitor");
        Assert.Equal("inbox", saved.GetProperty("icon").GetString());
        Assert.Equal("status", saved.GetProperty("iconBy").GetProperty("field").GetString());
        Assert.Equal("drafts", saved.GetProperty("iconBy").GetProperty("map").GetProperty("open").GetString());
        Assert.Equal("archive", saved.GetProperty("iconBy").GetProperty("map").GetProperty("closed").GetString());
        Assert.Equal("created", saved.GetProperty("action").GetString());
        Assert.Equal("{title} saved with {count} items", saved.GetProperty("label").GetString());
        Assert.False(saved.TryGetProperty("actionField", out _));

        var moved = byType["plugin.fx.moved"].GetProperty("monitor");
        Assert.Equal(["icon", "actionField"], moved.EnumerateObject().Select(p => p.Name));
        Assert.Equal("status", moved.GetProperty("actionField").GetString());

        // No hint, no key: a plugin event without one and every platform type read exactly as before.
        Assert.False(byType["plugin.fx.plain"].TryGetProperty("monitor", out _));
        Assert.All(EventCatalog.All, platform => Assert.False(byType[platform.Type].TryGetProperty("monitor", out _)));
        Assert.Equal(
            ["type", "publisher", "highVolume", "inLedger", "fields", "summary"],
            byType["plugin.fx.plain"].EnumerateObject().Select(p => p.Name));
    }
}
