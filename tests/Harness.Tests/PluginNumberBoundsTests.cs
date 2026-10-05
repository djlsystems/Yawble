using System.Net;
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
/// A NUMBER SETTING'S BOUNDS: a manifest may give a <c>number</c> field <c>min</c>, <c>max</c> and
/// <c>integer</c>, and the Host - not the web - refuses a value outside them from every writer,
/// naming the field and the bound, with nothing written. A value stored before its bounds existed
/// is kept as it is and reported on the settings read, never rewritten. The solution install and
/// update are in <c>Host/SolutionNumberBoundsTests</c>.
/// </summary>
public sealed class PluginNumberBoundsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-bounds-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginInstall.Write(_dataRoot, "bounded", manifest: PluginInstall.Manifest("bounded", edit: m => m["config"] = JsonNode.Parse("""
            {"salaryMax":{"type":"number","min":0,"max":1000000},
             "days":{"type":"number","min":1,"integer":true,"default":7},
             "note":{"type":"string","default":""}}
            """)));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _team = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("Bounds", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // ---- the manifest --------------------------------------------------------------------------

    private static (PluginConfigField? Field, string? Refusal) Field(string json) =>
        PluginConfigField.Parse("salaryMax", JsonDocument.Parse(json).RootElement);

    private static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_number_field_declares_min_max_and_integer_and_a_value_is_held_inside_them()
    {
        var (field, refusal) = Field("""{"type":"number","min":0,"max":100,"integer":true,"default":5}""");

        Assert.Null(refusal);
        Assert.Equal((0d, 100d, true), (field!.Min, field.Max, field.Integer));
        Assert.Null(field.Refusal("salaryMax", Value("0")));
        Assert.Null(field.Refusal("salaryMax", Value("100")));
        Assert.Equal("`salaryMax` must be at least 0; -2 is below it.", field.Refusal("salaryMax", Value("-2")));
        Assert.Equal("`salaryMax` must be at most 100; 101 is above it.", field.Refusal("salaryMax", Value("101")));
        Assert.Equal("`salaryMax` must be a whole number; 1.5 is not.", field.Refusal("salaryMax", Value("1.5")));

        // No bounds declared: any number, as before.
        var (open, _) = Field("""{"type":"number"}""");
        Assert.Equal(((double?)null, (double?)null, false), (open!.Min, open.Max, open.Integer));
        Assert.Null(open.Refusal("salaryMax", Value("-2")));
    }

    [Fact]
    public void A_min_greater_than_its_max_is_refused()
    {
        var (field, refusal) = Field("""{"type":"number","min":10,"max":5}""");

        Assert.Null(field);
        Assert.Equal("`config.salaryMax.min` (10) is greater than its `max` (5).", refusal);
    }

    [Theory]
    [InlineData("""{"type":"number","min":1,"default":0}""", "`config.salaryMax.default`: `salaryMax` must be at least 1; 0 is below it.")]
    [InlineData("""{"type":"number","max":10,"default":11}""", "`config.salaryMax.default`: `salaryMax` must be at most 10; 11 is above it.")]
    [InlineData("""{"type":"number","integer":true,"default":2.5}""", "`config.salaryMax.default`: `salaryMax` must be a whole number; 2.5 is not.")]
    public void A_default_outside_the_bounds_is_refused(string json, string expected)
    {
        var (field, refusal) = Field(json);

        Assert.Null(field);
        Assert.Equal(expected, refusal);
    }

    [Theory]
    [InlineData("""{"type":"string","min":0}""", "`config.salaryMax.min` applies only to a number field.")]
    [InlineData("""{"type":"number","max":"10"}""", "`config.salaryMax.max` must be a number.")]
    [InlineData("""{"type":"number","integer":"yes"}""", "`config.salaryMax.integer` must be true or false.")]
    [InlineData("""{"type":"number","min":1e400}""", "`config.salaryMax.min` must be a finite number; 1e400 is too large to hold.")]
    [InlineData("""{"type":"number","max":-1e400}""", "`config.salaryMax.max` must be a finite number; -1e400 is too large to hold.")]
    [InlineData("""{"type":"number","default":1e400}""", "`config.salaryMax.default`: `salaryMax` must be a finite number; 1e400 is too large to hold.")]
    public void A_bound_that_is_not_a_number_or_not_on_a_number_is_refused(string json, string expected) =>
        Assert.Equal(expected, Field(json).Refusal);

    // ---- the writers ---------------------------------------------------------------------------

    private async Task<HttpClient> PersonAsync()
    {
        var person = _factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return person;
    }

    private string SettingsRoute(string member) => $"/api/teams/{_team}/members/{member}/plugin-settings";

    private Task<HttpResponseMessage> HireAsync(HttpClient person, string name, object config) =>
        person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name, agent = "plugin:bounded", config }, Ct);

    private async Task<int> TenantRowCountAsync() =>
        (await Services.GetRequiredService<ITenantLog>().ReadAsync(take: ITenantLog.MaxTake, ct: Ct)).Events.Count;

    private async Task<List<TenantEvent>> TenantRowsAsync(string action) =>
        [.. (await Services.GetRequiredService<ITenantLog>().ReadAsync(take: ITenantLog.MaxTake, ct: Ct)).Events.Where(e => e.Action == action)];

    private Task<PluginMemberSettings> StoredAsync(string member) =>
        Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, member), Ct);

    private static async Task<string> RefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task The_hire_route_refuses_a_value_outside_its_bounds_naming_the_field_and_bound_and_writes_nothing()
    {
        using var person = await PersonAsync();
        var rows = await TenantRowCountAsync();

        Assert.Equal("`salaryMax` must be at least 0; -2 is below it.", await RefusalAsync(await HireAsync(person, "Low", new { salaryMax = -2 })));
        Assert.Equal("`days` must be a whole number; 1.5 is not.", await RefusalAsync(await HireAsync(person, "Half", new { days = 1.5 })));

        var team = Services.GetRequiredService<TeamRegistry>().All().Single(t => t.Id == _team);
        Assert.DoesNotContain(team.Containers, c => c.Id is "Low" or "Half");
        Assert.Empty((await StoredAsync("Low")).Config);
        Assert.Equal(rows, await TenantRowCountAsync());

        // IN RANGE: hired with its settings, and its row.
        var hired = await HireAsync(person, "Fetcher", new { salaryMax = 120000, days = 3 });
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
        Assert.Equal(120000, (await StoredAsync("Fetcher")).Config["salaryMax"].GetInt32());
        Assert.Single(await TenantRowsAsync(TenantActions.MemberAdded), r => r.Subject == $"{_team}/Fetcher");
    }

    [Fact]
    public async Task The_settings_route_refuses_a_value_outside_its_bounds_and_saves_one_inside_with_its_row()
    {
        using var person = await PersonAsync();
        Assert.True((await HireAsync(person, "Fetcher", new { salaryMax = 100 })).IsSuccessStatusCode);
        var rows = await TenantRowCountAsync();

        Assert.Equal("`salaryMax` must be at least 0; -2 is below it.",
            await RefusalAsync(await person.PutAsJsonAsync(SettingsRoute("Fetcher"), new { config = new { salaryMax = -2 } }, Ct)));
        Assert.Equal("`salaryMax` must be at most 1000000; 2000000 is above it.",
            await RefusalAsync(await person.PutAsJsonAsync(SettingsRoute("Fetcher"), new { config = new { salaryMax = 2000000 } }, Ct)));
        Assert.Equal("`days` must be at least 1; 0 is below it.",
            await RefusalAsync(await person.PutAsJsonAsync(SettingsRoute("Fetcher"), new { config = new { salaryMax = 100, days = 0 } }, Ct)));

        Assert.Equal(100, (await StoredAsync("Fetcher")).Config["salaryMax"].GetInt32());
        Assert.Equal(rows, await TenantRowCountAsync());
        Assert.Empty(await TenantRowsAsync(TenantActions.MemberPluginSettingsChanged));

        // IN RANGE: saved, with its row.
        var saved = await person.PutAsJsonAsync(SettingsRoute("Fetcher"), new { config = new { salaryMax = 0, days = 30 } }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(0, (await StoredAsync("Fetcher")).Config["salaryMax"].GetInt32());
        Assert.Single(await TenantRowsAsync(TenantActions.MemberPluginSettingsChanged));
    }

    [Fact]
    public async Task The_settings_route_refuses_a_number_too_large_to_hold_and_writes_nothing()
    {
        using var person = await PersonAsync();
        Assert.True((await HireAsync(person, "Fetcher", new { salaryMax = 100 })).IsSuccessStatusCode);
        var rows = await TenantRowCountAsync();

        // 1e400 reads as infinity, which is neither below a min nor fractional: refused on its own.
        foreach (var body in (string[])["""{"config":{"days":1e400}}""", """{"config":{"salaryMax":-1e400}}"""])
        {
            var refusal = await RefusalAsync(await person.PutAsync(SettingsRoute("Fetcher"),
                new StringContent(body, System.Text.Encoding.UTF8, "application/json"), Ct));
            Assert.Contains("must be a finite number", refusal);
        }

        var stored = await StoredAsync("Fetcher");
        Assert.Equal(100, stored.Config["salaryMax"].GetInt32());
        Assert.False(stored.Config.ContainsKey("days"));
        Assert.Equal(rows, await TenantRowCountAsync());
        Assert.Empty(await TenantRowsAsync(TenantActions.MemberPluginSettingsChanged));
    }

    [Fact]
    public async Task A_value_stored_outside_bounds_added_later_is_kept_reported_on_read_and_never_rewritten()
    {
        using var person = await PersonAsync();
        Assert.True((await HireAsync(person, "Fetcher", new { salaryMax = 100 })).IsSuccessStatusCode);

        // STORED BEFORE THE BOUNDS EXISTED: as a member hired on an earlier version holds it.
        var store = Services.GetRequiredService<IPluginMemberSettingsStore>();
        var id = new ContainerId(_team, "Fetcher");
        var earlier = new PluginMemberSettings(
            new Dictionary<string, JsonElement> { ["salaryMax"] = Value("-2") }, new Dictionary<string, string>());
        await store.SaveAsync(id, earlier, [], Ct);

        // REPORTED on read, the value as it is.
        var read = await person.GetFromJsonAsync<JsonElement>(SettingsRoute("Fetcher"), Ct);
        Assert.Equal(-2, read.GetProperty("config").GetProperty("salaryMax").GetInt32());
        var outOfRange = read.GetProperty("outOfRange");
        Assert.Equal("`salaryMax` must be at least 0; -2 is below it.", outOfRange.GetProperty("salaryMax").GetString());
        Assert.False(outOfRange.TryGetProperty("days", out _));
        var field = read.GetProperty("fields").GetProperty("salaryMax");
        Assert.Equal((0, 1000000, false), (field.GetProperty("min").GetInt32(), field.GetProperty("max").GetInt32(), field.GetProperty("integer").GetBoolean()));

        // THE REST OF THE FORM STILL SAVES with the stored value sent back unchanged - and it stays.
        var saved = await person.PutAsJsonAsync(SettingsRoute("Fetcher"), new { config = new { salaryMax = -2, note = "kept" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        var answer = await saved.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.True(answer.GetProperty("outOfRange").TryGetProperty("salaryMax", out _));
        Assert.Equal(-2, (await StoredAsync("Fetcher")).Config["salaryMax"].GetInt32());

        // Another value outside the bounds is refused.
        Assert.Contains("`salaryMax` must be at least 0",
            await RefusalAsync(await person.PutAsJsonAsync(SettingsRoute("Fetcher"), new { config = new { salaryMax = -3 } }, Ct)));
        Assert.Equal(-2, (await StoredAsync("Fetcher")).Config["salaryMax"].GetInt32());

        // A RUN IS NOT REFUSED for it: the stored value is the member's until a person changes it.
        var (_, runRefusal) = PluginMemberRunner.EffectiveConfig(
            Services.GetRequiredService<PluginCatalog>().For("bounded")!.Manifest, await StoredAsync("Fetcher"));
        Assert.Null(runRefusal);

        // Brought inside: no longer reported.
        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync(SettingsRoute("Fetcher"), new { config = new { salaryMax = 5 } }, Ct)).StatusCode);
        var after = await person.GetFromJsonAsync<JsonElement>(SettingsRoute("Fetcher"), Ct);
        Assert.Empty(after.GetProperty("outOfRange").EnumerateObject());
    }

    [Fact]
    public async Task The_plugin_listing_carries_each_number_fields_bounds()
    {
        using var person = await PersonAsync();
        var listing = await person.GetFromJsonAsync<JsonElement>("/api/plugins", Ct);
        var plugin = listing.GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "bounded");
        var days = plugin.GetProperty("config").GetProperty("days");

        Assert.Equal(1, days.GetProperty("min").GetInt32());
        Assert.Equal(JsonValueKind.Null, days.GetProperty("max").ValueKind);
        Assert.True(days.GetProperty("integer").GetBoolean());
    }
}
