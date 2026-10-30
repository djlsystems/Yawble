using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Host;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests;

/// <summary>
/// A SETTING'S <c>options</c>: a string or list field's choices, each a value and the words shown for
/// it - a named set (<c>locale</c>: every language Chrome accepts; <c>timeZone</c>: UTC and every IANA
/// time zone) or the manifest's own list. The Host refuses a new value outside them from every writer
/// in one short sentence; a value stored before the field had them is kept, reported on the settings
/// read, and never handed to a run, which gets the default instead. Both answers carry the options.
/// A named set's value in another case, or a time zone under an older tz name (Asia/Calcutta, GMT), is
/// that option: saved, read and run as it. A refusal names the field by its label.
/// </summary>
public sealed partial class PluginOptionSetsTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-options-{Guid.NewGuid():N}");
    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginInstall.Write(_dataRoot, "browser", manifest: PluginInstall.Manifest("browser", edit: m => m["config"] = JsonNode.Parse("""
            {"timeZone":{"type":"string","label":"Time zone","options":"timeZone","default":"UTC","advanced":true},
             "languages":{"type":"list","label":"Language","options":"locale","default":["en-US"],"advanced":true},
             "note":{"type":"string","default":""}}
            """)));

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(new FakeAgent())));

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _team = (await Services.GetRequiredService<TeamRegistry>().CreateAsync("Options", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    // ---- the sets ------------------------------------------------------------------------------

    [GeneratedRegex("^[a-z]{2,3}(-[A-Z][a-z]{3})?(-([A-Z]{2}|[0-9]{3}))?(-[a-z]{5,8})?$")]
    private static partial Regex LanguageTag();

    [Fact]
    public void The_locale_set_is_every_language_Chrome_accepts_by_its_code_labelled_in_English()
    {
        var locale = PluginOptionSets.Named("locale")!;

        Assert.InRange(locale.Count, 150, 400);
        Assert.Contains(locale, o => o is { Value: "en-US", Label: "English (United States)", Keywords: "American English" });
        Assert.Contains(locale, o => o is { Value: "en-GB", Label: "English (United Kingdom)", Keywords: "British English" });
        Assert.Contains(locale, o => o is { Value: "en", Label: "English", Keywords: null });
        Assert.Contains(locale, o => o is { Value: "de-DE", Label: "German (Germany)" });
        Assert.Contains(locale, o => o.Value == "es-419");
        Assert.Equal(locale.Count, locale.Select(o => o.Value).Distinct(StringComparer.Ordinal).Count());
        Assert.All(locale, o => Assert.Matches(LanguageTag(), o.Value));
        Assert.All(locale, o => Assert.False(string.IsNullOrWhiteSpace(o.Label)));
        // Chromium's pseudolocales are a build's test languages, not a person's.
        Assert.DoesNotContain(locale, o => o.Value is "en-XA" or "ar-XB");
    }

    [Fact]
    public void The_time_zone_set_is_UTC_first_then_every_IANA_zone_each_one_this_machine_knows()
    {
        var zones = PluginOptionSets.Named("timeZone")!;

        Assert.Equal(("UTC", "UTC"), (zones[0].Value, zones[0].Label));
        // A zone's older names are words its filter finds it by.
        Assert.Contains("Etc/UTC", zones[0].Keywords!.Split(' '));
        Assert.Contains("GMT", zones[0].Keywords!.Split(' '));
        Assert.Equal("Asia/Calcutta", zones.Single(o => o.Value == "Asia/Kolkata").Keywords);
        Assert.InRange(zones.Count, 350, 600);
        Assert.Contains(zones, o => o.Value == "Europe/London");
        Assert.Contains(zones, o => o.Value == "America/New_York");
        // IANA's names, not CLDR's old ones that Intl.supportedValuesOf answers in Node.
        Assert.Contains(zones, o => o.Value == "Asia/Kolkata");
        Assert.DoesNotContain(zones, o => o.Value == "Asia/Calcutta");
        Assert.Equal(zones.Count, zones.Select(o => o.Value).Distinct(StringComparer.Ordinal).Count());
        Assert.All(zones, o => Assert.True(TimeZoneInfo.TryFindSystemTimeZoneById(o.Value, out _), $"{o.Value} is not a time zone here"));
    }

    [Theory]
    [InlineData("Asia/Calcutta", "Asia/Kolkata")]
    [InlineData("Europe/Kiev", "Europe/Kyiv")]
    [InlineData("Asia/Saigon", "Asia/Ho_Chi_Minh")]
    [InlineData("America/Buenos_Aires", "America/Argentina/Buenos_Aires")]
    [InlineData("Asia/Katmandu", "Asia/Kathmandu")]
    [InlineData("US/Eastern", "America/New_York")]
    [InlineData("Etc/UTC", "UTC")]
    [InlineData("Etc/GMT", "UTC")]
    [InlineData("GMT", "UTC")]
    [InlineData("Zulu", "UTC")]
    [InlineData("asia/calcutta", "Asia/Kolkata")]
    [InlineData("europe/london", "Europe/London")]
    [InlineData("Europe/London", "Europe/London")]
    public void A_time_zones_older_name_or_other_case_stands_for_its_entry(string given, string entry) =>
        Assert.Equal(entry, PluginOptionSets.Canonical("timeZone", given));

    [Theory]
    [InlineData("en-gb", "en-GB")]
    [InlineData("EN-US", "en-US")]
    [InlineData("zh-tw", "zh-TW")]
    [InlineData("de", "de")]
    public void A_language_tag_in_another_case_stands_for_its_entry(string given, string entry) =>
        Assert.Equal(entry, PluginOptionSets.Canonical("locale", given));

    [Theory]
    [InlineData("timeZone", "Mars/Olympus")]
    [InlineData("timeZone", "Etc/GMT+5")]
    [InlineData("locale", "xx-INVALID")]
    [InlineData("locale", "en_GB")]
    [InlineData("planets", "UTC")]
    public void A_value_no_entry_stands_for_has_none(string set, string given) =>
        Assert.Null(PluginOptionSets.Canonical(set, given));

    [Fact]
    public void Every_time_zone_alias_names_a_zone_this_machine_knows_and_stands_for_an_entry_it_is_not()
    {
        using var stream = typeof(PluginOptionSets).Assembly.GetManifestResourceStream(PluginOptionSets.ResourceName)!;
        var aliases = JsonDocument.Parse(stream).RootElement.GetProperty("aliases").GetProperty("timeZone").EnumerateObject().ToList();
        var entries = PluginOptionSets.Named("timeZone")!.Select(o => o.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.InRange(aliases.Count, 100, 400);
        Assert.All(aliases, a => Assert.DoesNotContain(a.Name, entries));
        Assert.All(aliases, a => Assert.Contains(a.Value.GetString()!, entries));
        Assert.All(aliases, a => Assert.True(TimeZoneInfo.TryFindSystemTimeZoneById(a.Name, out _), $"{a.Name} is not a time zone here"));
    }

    // ---- the manifest --------------------------------------------------------------------------

    private static (PluginConfigField? Field, string? Refusal) Field(string json) =>
        PluginConfigField.Parse("zone", JsonDocument.Parse(json).RootElement);

    private static JsonElement Value(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void A_named_set_gives_the_field_its_options_and_their_values_as_its_enum()
    {
        var (field, refusal) = Field("""{"type":"string","options":"timeZone","default":"UTC"}""");

        Assert.Null(refusal);
        Assert.Equal("timeZone", field!.OptionSet);
        Assert.Equal(PluginOptionSets.Named("timeZone"), field.Options);
        Assert.Equal(field.Options!.Select(o => o.Value), field.Enum);
    }

    [Fact]
    public void A_manifests_own_options_carry_their_labels_and_a_label_defaults_to_the_value()
    {
        var (field, refusal) = Field("""{"type":"list","options":[{"value":"a","label":"Alpha"},{"value":"b"}]}""");

        Assert.Null(refusal);
        Assert.Null(field!.OptionSet);
        Assert.Equal([new PluginOption("a", "Alpha"), new PluginOption("b", "b")], field.Options);
        Assert.Equal(["a", "b"], field.Enum);
    }

    [Theory]
    [InlineData("""{"type":"string","options":"planets"}""", "`config.zone.options` names no option set: 'planets'.")]
    [InlineData("""{"type":"number","options":"timeZone"}""", "`config.zone.options` applies only to a string or list field.")]
    [InlineData("""{"type":"string","options":"timeZone","enum":["UTC"]}""", "`config.zone` gives both `enum` and `options`; give one.")]
    [InlineData("""{"type":"string","options":[]}""", "`config.zone.options` must be")]
    [InlineData("""{"type":"string","options":[{"value":"a"},{"value":"a"}]}""", "`config.zone.options` lists 'a' twice.")]
    [InlineData("""{"type":"string","options":[{"label":"A"}]}""", "`config.zone.options` must be a set's name or a list")]
    [InlineData("""{"type":"string","options":"timeZone","default":"Mars/Olympus"}""", "`config.zone.default`: `zone` must be an IANA time zone, such as Europe/London, or UTC; 'Mars/Olympus' is not.")]
    [InlineData("""{"type":"list","options":"locale","default":["en-US","xx-YY"]}""", "`config.zone.default`: `zone` holds 'xx-YY'; each item must be a language Chrome accepts, such as en-US.")]
    public void Options_the_Host_cannot_use_refuse_the_manifest_naming_the_field(string json, string refusal)
    {
        var (field, why) = Field(json);

        Assert.Null(field);
        Assert.StartsWith(refusal, why);
    }

    [Fact]
    public void A_fields_value_is_stored_as_the_option_it_stands_for_and_anything_else_is_left_for_the_refusal()
    {
        var (zone, _) = Field("""{"type":"string","options":"timeZone"}""");
        var (languages, _) = Field("""{"type":"list","options":"locale"}""");
        var (own, _) = Field("""{"type":"string","options":[{"value":"Alpha"}]}""");

        Assert.Equal("Asia/Kolkata", zone!.Canonical(Value("\"Asia/Calcutta\"")).GetString());
        Assert.Equal("Mars/Olympus", zone.Canonical(Value("\"Mars/Olympus\"")).GetString());
        Assert.Null(zone.Refusal("zone", Value("\"Asia/Calcutta\"")));
        Assert.Equal("""["en-GB","fr-FR","xx-INVALID"]""",
            languages!.Canonical(Value("""["en-gb","fr-FR","EN-GB","xx-INVALID"]""")).GetRawText());
        // A manifest's own list is matched exactly: only a named set knows its other spellings.
        Assert.Equal("alpha", own!.Canonical(Value("\"alpha\"")).GetString());
        Assert.NotNull(own.Refusal("zone", Value("\"alpha\"")));
    }

    [Fact]
    public void An_option_or_bounds_sentence_names_the_field_by_its_label_and_by_its_key_without_one()
    {
        var (labelled, _) = Field("""{"type":"string","label":"Time zone","options":"timeZone"}""");
        var (bare, _) = Field("""{"type":"string","options":"timeZone"}""");
        var (retries, _) = PluginConfigField.Parse("retries", Value("""{"type":"number","label":"Retries","max":5,"integer":true}"""));
        var (count, _) = PluginConfigField.Parse("count", Value("""{"type":"number","min":1}"""));
        var (mode, _) = PluginConfigField.Parse("mode", Value("""{"type":"string","label":"Mode","enum":["a","b"]}"""));

        Assert.Equal("Time zone must be an IANA time zone, such as Europe/London, or UTC; 'Mars/Olympus' is not.",
            labelled!.Refusal("zone", Value("\"Mars/Olympus\"")));
        Assert.Equal("`zone` must be an IANA time zone, such as Europe/London, or UTC; 'Mars/Olympus' is not.",
            bare!.Refusal("zone", Value("\"Mars/Olympus\"")));
        Assert.Equal("Retries must be at most 5; 9 is above it.", retries!.BoundsRefusal("retries", Value("9")));
        Assert.Equal("Retries must be a whole number; 2.5 is not.", retries.BoundsRefusal("retries", Value("2.5")));
        Assert.Equal("`count` must be at least 1; 0 is below it.", count!.BoundsRefusal("count", Value("0")));
        Assert.Equal("Mode must be one of: a, b.", mode!.Refusal("mode", Value("\"c\"")));
    }

    // ---- the routes and a run ------------------------------------------------------------------

    private async Task<HttpClient> PersonAsync()
    {
        var person = _factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        return person;
    }

    private string SettingsRoute(string member) => $"/api/teams/{_team}/members/{member}/plugin-settings";

    private Task<HttpResponseMessage> HireAsync(HttpClient person, string name, object config) =>
        person.PostAsJsonAsync($"/api/teams/{_team}/containers", new { name, agent = "plugin:browser", config }, Ct);

    private Task<PluginMemberSettings> StoredAsync(string member) =>
        Services.GetRequiredService<IPluginMemberSettingsStore>().ForAsync(new ContainerId(_team, member), Ct);

    private PluginManifest Manifest() => Services.GetRequiredService<PluginCatalog>().For("browser")!.Manifest;

    private static async Task<string> RefusalAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task The_hire_and_settings_routes_refuse_a_choice_outside_the_options_in_one_short_sentence()
    {
        using var person = await PersonAsync();

        Assert.Equal("Time zone must be an IANA time zone, such as Europe/London, or UTC; 'Mars/Olympus' is not.",
            await RefusalAsync(await HireAsync(person, "Bad", new { timeZone = "Mars/Olympus" })));
        Assert.Equal("Language holds 'klingon'; each item must be a language Chrome accepts, such as en-US.",
            await RefusalAsync(await HireAsync(person, "Bad", new { languages = new[] { "en-GB", "klingon" } })));

        var hired = await HireAsync(person, "Browser", new { timeZone = "Europe/London", languages = new[] { "en-GB", "en" } });
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));

        Assert.Equal("Language holds 'xx-INVALID'; each item must be a language Chrome accepts, such as en-US.",
            await RefusalAsync(await person.PutAsJsonAsync(SettingsRoute("Browser"), new { config = new { languages = new[] { "xx-INVALID" } } }, Ct)));
        Assert.Equal("Europe/London", (await StoredAsync("Browser")).Config["timeZone"].GetString());

        Assert.Equal(HttpStatusCode.OK, (await person.PutAsJsonAsync(SettingsRoute("Browser"), new { config = new { timeZone = "UTC" } }, Ct)).StatusCode);
        Assert.Equal("UTC", (await StoredAsync("Browser")).Config["timeZone"].GetString());
    }

    [Fact]
    public async Task The_hire_and_settings_routes_store_an_older_name_or_another_case_as_its_option()
    {
        using var person = await PersonAsync();

        var hired = await HireAsync(person, "Aliased", new { timeZone = "Asia/Calcutta", languages = new[] { "en-gb", "fr-FR", "EN-GB" } });
        Assert.True(hired.IsSuccessStatusCode, await hired.Content.ReadAsStringAsync(Ct));
        var stored = await StoredAsync("Aliased");
        Assert.Equal("Asia/Kolkata", stored.Config["timeZone"].GetString());
        Assert.Equal("""["en-GB","fr-FR"]""", stored.Config["languages"].GetRawText());

        foreach (var (given, entry) in new[] { ("Etc/UTC", "UTC"), ("GMT", "UTC"), ("Europe/Kiev", "Europe/Kyiv"), ("europe/london", "Europe/London") })
        {
            var saved = await person.PutAsJsonAsync(SettingsRoute("Aliased"), new { config = new { timeZone = given } }, Ct);
            Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
            Assert.Equal(entry, (await StoredAsync("Aliased")).Config["timeZone"].GetString());
            Assert.Equal(entry, (await saved.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("config").GetProperty("timeZone").GetString());
        }
    }

    [Fact]
    public async Task An_older_name_stored_before_the_options_reads_and_runs_as_its_option_and_is_not_reported()
    {
        using var person = await PersonAsync();
        Assert.True((await HireAsync(person, "Old", new { })).IsSuccessStatusCode);

        // STORED BY AN OLDER VERSION, which took any name Chrome knew, in any case.
        await Services.GetRequiredService<IPluginMemberSettingsStore>().SaveAsync(new ContainerId(_team, "Old"), new PluginMemberSettings(
            new Dictionary<string, JsonElement> { ["timeZone"] = Value("\"Asia/Calcutta\""), ["languages"] = Value("""["en-gb","fr-FR"]""") },
            new Dictionary<string, string>()), [], Ct);

        var read = await person.GetFromJsonAsync<JsonElement>(SettingsRoute("Old"), Ct);
        Assert.Equal("Asia/Kolkata", read.GetProperty("config").GetProperty("timeZone").GetString());
        Assert.Equal("""["en-GB","fr-FR"]""", read.GetProperty("config").GetProperty("languages").GetRawText());
        Assert.Empty(read.GetProperty("outOfRange").EnumerateObject());

        var (config, refusal) = PluginMemberRunner.EffectiveConfig(Manifest(), await StoredAsync("Old"));
        Assert.Null(refusal);
        Assert.Equal("Asia/Kolkata", config["timeZone"]!.GetValue<string>());
        Assert.Equal(["en-GB", "fr-FR"], config["languages"]!.AsArray().Select(n => n!.GetValue<string>()));

        // The form sends back what it read: saved as the options.
        var saved = await person.PutAsJsonAsync(SettingsRoute("Old"), new { config = new { timeZone = "Asia/Kolkata", languages = new[] { "en-GB", "fr-FR" } } }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal("Asia/Kolkata", (await StoredAsync("Old")).Config["timeZone"].GetString());
    }

    [Fact]
    public async Task A_member_with_nothing_set_runs_with_the_defaults()
    {
        using var person = await PersonAsync();
        Assert.True((await HireAsync(person, "Plain", new { })).IsSuccessStatusCode);

        var (config, refusal) = PluginMemberRunner.EffectiveConfig(Manifest(), await StoredAsync("Plain"));

        Assert.Null(refusal);
        Assert.Equal("UTC", config["timeZone"]!.GetValue<string>());
        Assert.Equal(["en-US"], config["languages"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task A_choice_stored_before_the_options_is_kept_and_reported_and_a_run_gets_the_default_instead()
    {
        using var person = await PersonAsync();
        Assert.True((await HireAsync(person, "Old", new { })).IsSuccessStatusCode);

        // STORED BY AN OLDER VERSION, when the field was free text.
        var store = Services.GetRequiredService<IPluginMemberSettingsStore>();
        await store.SaveAsync(new ContainerId(_team, "Old"), new PluginMemberSettings(
            new Dictionary<string, JsonElement> { ["timeZone"] = Value("\"Mars/Olympus\""), ["languages"] = Value("""["en-GB","klingon"]""") },
            new Dictionary<string, string>()), [], Ct);

        var read = await person.GetFromJsonAsync<JsonElement>(SettingsRoute("Old"), Ct);
        Assert.Equal("Mars/Olympus", read.GetProperty("config").GetProperty("timeZone").GetString());
        Assert.Equal("Time zone must be an IANA time zone, such as Europe/London, or UTC; 'Mars/Olympus' is not.",
            read.GetProperty("outOfRange").GetProperty("timeZone").GetString());
        Assert.True(read.GetProperty("outOfRange").TryGetProperty("languages", out _));

        // The rest of the form still saves with the stored values sent back unchanged.
        var saved = await person.PutAsJsonAsync(SettingsRoute("Old"), new { config = new { timeZone = "Mars/Olympus", languages = new[] { "en-GB", "klingon" }, note = "kept" } }, Ct);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        // A RUN NEVER SEES THEM: the zone is the default, the list keeps only its usable items.
        var (config, refusal) = PluginMemberRunner.EffectiveConfig(Manifest(), await StoredAsync("Old"));
        Assert.Null(refusal);
        Assert.Equal("UTC", config["timeZone"]!.GetValue<string>());
        Assert.Equal(["en-GB"], config["languages"]!.AsArray().Select(n => n!.GetValue<string>()));

        await store.SaveAsync(new ContainerId(_team, "Old"), new PluginMemberSettings(
            new Dictionary<string, JsonElement> { ["languages"] = Value("""["klingon"]""") }, new Dictionary<string, string>()), [], Ct);
        (config, _) = PluginMemberRunner.EffectiveConfig(Manifest(), await StoredAsync("Old"));
        Assert.Equal(["en-US"], config["languages"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public async Task The_settings_read_and_the_plugin_listing_carry_each_options_value_and_label()
    {
        using var person = await PersonAsync();
        Assert.True((await HireAsync(person, "Browser", new { })).IsSuccessStatusCode);

        var read = await person.GetFromJsonAsync<JsonElement>(SettingsRoute("Browser"), Ct);
        var listing = await person.GetFromJsonAsync<JsonElement>("/api/plugins", Ct);
        var listed = listing.GetProperty("plugins").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "browser").GetProperty("config");

        foreach (var fields in new[] { read.GetProperty("fields"), listed })
        {
            var languages = fields.GetProperty("languages");
            Assert.Equal("locale", languages.GetProperty("optionSet").GetString());
            Assert.True(languages.GetProperty("advanced").GetBoolean());
            Assert.Contains(languages.GetProperty("options").EnumerateArray(),
                o => o.GetProperty("value").GetString() == "en-US" && o.GetProperty("label").GetString() == "English (United States)");
            Assert.Contains(languages.GetProperty("options").EnumerateArray(),
                o => o.GetProperty("value").GetString() == "en-GB" && o.GetProperty("keywords").GetString() == "British English");
            Assert.Equal("UTC", fields.GetProperty("timeZone").GetProperty("options")[0].GetProperty("value").GetString());
            Assert.Equal(JsonValueKind.Null, fields.GetProperty("note").GetProperty("options").ValueKind);
        }
    }
}
