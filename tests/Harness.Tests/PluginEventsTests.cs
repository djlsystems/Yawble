using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// STEP 9, PLUGIN EVENTS (assessment §8): what a manifest may declare, and <see cref="EventCatalog"/>
/// answering the platform's types and the installed plugins' behind one lookup - so the existing
/// high-volume checks refuse a plugin firehose as a language-model member's subscription.
/// </summary>
public sealed class PluginEventsTests : IDisposable
{
    private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-plugin-events-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private static (PluginManifest? Manifest, string? Refusal) Parse(string events) =>
        PluginManifest.Parse(PluginInstall.Manifest("fx", edit: m => m["events"] = JsonNode.Parse(events)).ToJsonString());

    [Fact]
    public void A_declared_event_carries_its_fields_and_volume()
    {
        var (manifest, refusal) = Parse("""{"publishes":[{"type":"tick","summary":"A tick.","highVolume":true,"fields":[{"name":"n","kind":"number","summary":"How many."}]}]}""");

        Assert.Null(refusal);
        var definition = manifest!.Publishes.Single().Definition("fx");
        Assert.Equal("plugin.fx.tick", definition.Type);
        Assert.Equal(EventPublisher.Plugin, definition.Publisher);
        Assert.True(definition.HighVolume);
        Assert.Equal([PayloadFields.Source, "n"], definition.Fields.Select(f => f.Name));
        Assert.Equal(EventFieldKind.Integer, definition.Fields[1].Kind);
    }

    [Theory]
    [InlineData("""{"publishes":[{"type":"tick","fields":[{"name":"source"}]}]}""", "platform's own field")]
    [InlineData("""{"publishes":[{"type":"tick","fields":[{"name":"n","kind":"float"}]}]}""", "use string, number, boolean or list")]
    [InlineData("""{"publishes":[{"type":"tick"},{"type":"tick"}]}""", "twice")]
    [InlineData("""{"publishes":[{"type":"Tick"}]}""", "lowercase")]
    public void A_bad_declaration_refuses_the_manifest_by_name(string events, string says)
    {
        Assert.Contains(says, Parse(events).Refusal);
    }

    [Fact]
    public void The_catalog_answers_the_platforms_types_and_the_installed_plugins_behind_one_lookup()
    {
        PluginInstall.Write(_dataRoot, "fx", manifest: PluginInstall.Manifest("fx",
            edit: m => m["events"] = JsonNode.Parse("""{"publishes":[{"type":"tick","highVolume":true},{"type":"done"}]}""")));
        var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
        catalog.Rescan();

        Assert.Null(EventCatalog.For("plugin.fx.tick"));

        using (EventCatalog.Register(catalog))
        {
            Assert.Equal(EventPublisher.Plugin, EventCatalog.For("plugin.fx.done")!.Publisher);
            Assert.True(EventCatalog.IsHighVolume("plugin.fx.tick"));
            Assert.False(EventCatalog.IsHighVolume("plugin.fx.done"));
            Assert.Null(EventCatalog.For("plugin.fx.undeclared"));
            Assert.Contains(EventCatalog.WithPlugins(), e => e.Type == "plugin.fx.done");

            // A platform type is the platform's, whatever is installed.
            Assert.Equal(EventPublisher.Platform, EventCatalog.For(MessageTypes.Completed)!.Publisher);
        }

        Assert.Null(EventCatalog.For("plugin.fx.done"));
    }

    [Fact]
    public async Task A_high_volume_plugin_event_is_refused_as_a_language_model_members_subscription()
    {
        PluginInstall.Write(_dataRoot, "fx", manifest: PluginInstall.Manifest("fx",
            edit: m => m["events"] = JsonNode.Parse("""{"publishes":[{"type":"tick","highVolume":true},{"type":"done"}]}""")));

        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        var registry = factory.Services.GetRequiredService<TeamRegistry>();
        var team = (await registry.CreateAsync("Mixed", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
        await registry.AddContainerAsync(team, "Dev", "claude-headless", "", [], ct: Ct);

        // A subscription at hire, by the member route's own check.
        await Assert.ThrowsAsync<FirehoseSubscriptionException>(() =>
            registry.AddContainerAsync(team, "Dev2", "claude-headless", "", ["plugin.fx.tick"], ct: Ct));

        // And a trigger, by the trigger route's.
        await factory.Services.GetRequiredService<IUserStore>().CreateAsync("person@example.test", "correct horse battery", Ct);
        using var person = factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = "person@example.test", password = "correct horse battery" }, Ct)).EnsureSuccessStatusCode();

        var firehose = await person.PostAsJsonAsync($"/api/teams/{team}/triggers",
            new { name = "Ticks", kind = "event", container = "Dev", eventType = "plugin.fx.tick", instruction = "tick" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, firehose.StatusCode);

        var ordinary = await person.PostAsJsonAsync($"/api/teams/{team}/triggers",
            new { name = "Done", kind = "event", container = "Dev", eventType = "plugin.fx.done", instruction = "done" }, Ct);
        Assert.True(ordinary.IsSuccessStatusCode, await ordinary.Content.ReadAsStringAsync(Ct));

        var undeclared = await person.PostAsJsonAsync($"/api/teams/{team}/triggers",
            new { name = "Nope", kind = "event", container = "Dev", eventType = "plugin.fx.nope", instruction = "nope" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, undeclared.StatusCode);
    }
}
