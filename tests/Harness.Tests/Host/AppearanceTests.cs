using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A PERSON'S APPEARANCE IS THEIRS, AND THE INSTANCE HAS A DEFAULT. `GET`/`PUT /api/me/appearance`
/// keep one record per person on the server: the theme chosen (Match system, a preset or one of their
/// own), text size, font, line spacing and their own themes. Someone who never chose reads none and
/// the instance default (`theme.default`, Match system or a preset). Anything out of bounds is refused
/// with a sentence naming the field, and nothing is stored. A machine principal has no appearance.
/// </summary>
public sealed class AppearanceTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Valid() => JsonNode.Parse(
        """
        {
          "selected": "custom:evening",
          "textSize": 130,
          "font": "atkinson",
          "lineSpacing": "relaxed",
          "themes": [
            {
              "id": "evening",
              "name": "Evening",
              "basedOn": "soft-dark",
              "colours": {
                "background": "#1b1a18", "panels": "#262420", "text": "#f2ede4", "accent": "#ff7a3d",
                "running": "#4caf50", "waiting": "#e0a030", "blocked": "#e57373", "failed": "#ff5252"
              }
            }
          ]
        }
        """)!.AsObject();

    /// <summary>A person of their own per test, so one test's choice is never another's start.</summary>
    private async Task<HttpClient> FreshPersonAsync()
    {
        var email = $"looks-{Guid.NewGuid():N}@example.test";
        await host.Services.GetRequiredService<IUserStore>().CreateAsync(email, HostFixture.Password);
        var client = host.Anonymous();
        (await client.PostAsJsonAsync("/api/auth/login", new { email, password = HostFixture.Password }, Ct))
            .EnsureSuccessStatusCode();
        return client;
    }

    private static async Task<JsonObject> ReadAsync(HttpClient client)
    {
        var response = await client.GetAsync("/api/me/appearance", Ct);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!.AsObject();
    }

    [Fact]
    public async Task A_person_who_never_chose_reads_no_appearance_and_the_instance_default()
    {
        using var person = await FreshPersonAsync();

        var read = await ReadAsync(person);

        Assert.Null(read["appearance"]);
        Assert.Equal(host.Services.GetRequiredService<TenantSettings>().ThemeDefault, (string?)read["instanceDefault"]);
    }

    [Fact]
    public async Task A_persons_appearance_is_kept_and_read_back_whole()
    {
        using var person = await FreshPersonAsync();

        var put = await person.PutAsJsonAsync("/api/me/appearance", Valid(), Ct);
        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

        var stored = (await ReadAsync(person))["appearance"]!;
        Assert.True(JsonNode.DeepEquals(Valid(), stored), stored.ToJsonString());

        // Another person still has none: the record is the person's, not the instance's.
        using var other = await FreshPersonAsync();
        Assert.Null((await ReadAsync(other))["appearance"]);
    }

    public static TheoryData<string, string> Refusals() => new()
    {
        { """{"selected":"purple"}""", "selected" },
        { """{"selected":"custom:nobody"}""", "selected" },
        { """{"textSize":80}""", "textSize" },
        { """{"textSize":170}""", "textSize" },
        { """{"textSize":125}""", "textSize" },
        { """{"font":"comic"}""", "font" },
        { """{"lineSpacing":"double"}""", "lineSpacing" },
        { """{"themes":[{"id":"evening","name":"","basedOn":"soft-dark"}]}""", "themes[0].name" },
        { """{"themes":[{"id":"evening","name":"Evening","basedOn":"neon"}]}""", "themes[0].basedOn" },
        { """{"themes":[{"id":"Bad Id","name":"Evening","basedOn":"dark"}]}""", "themes[0].id" },
        { """{"themes":[{"id":"evening","name":"Evening","basedOn":"dark","colours":{"text":"red"}}]}""", "themes[0].colours.text" },
        { """{"themes":[{"id":"evening","name":"Evening","basedOn":"dark","colours":{"glow":"#ffffff"}}]}""", "themes[0].colours.glow" },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_value_out_of_bounds_is_refused_naming_its_field_and_nothing_is_stored(string change, string field)
    {
        using var person = await FreshPersonAsync();
        var body = Valid();
        foreach (var (key, value) in JsonNode.Parse(change)!.AsObject())
        {
            body[key] = value?.DeepClone();
        }

        // A selection naming the custom theme the change replaced would be refused for that instead.
        if (field.StartsWith("themes", StringComparison.Ordinal)) body["selected"] = "dark";

        var response = await person.PutAsJsonAsync("/api/me/appearance", body, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["error"];
        Assert.Contains($"`{field}`", error);
        Assert.Null((await ReadAsync(person))["appearance"]);
    }

    [Fact]
    public async Task Two_themes_sharing_an_id_and_more_than_twenty_themes_are_refused()
    {
        using var person = await FreshPersonAsync();

        var twice = Valid();
        twice["themes"]!.AsArray().Add(twice["themes"]![0]!.DeepClone());
        var response = await person.PutAsJsonAsync("/api/me/appearance", twice, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("`themes[1].id`", await response.Content.ReadAsStringAsync(Ct));

        var many = Valid();
        many["themes"] = new JsonArray([.. Enumerable.Range(0, Appearance.MaxThemes + 1).Select(i =>
            (JsonNode)new JsonObject { ["id"] = $"t{i}", ["name"] = $"Theme {i}", ["basedOn"] = "light" })]);
        many["selected"] = "light";
        response = await person.PutAsJsonAsync("/api/me/appearance", many, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("`themes`", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_machine_principal_has_no_appearance_to_read_or_write()
    {
        using var container = host.Container(host.AlphaContainerKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await container.GetAsync("/api/me/appearance", Ct)).StatusCode);
        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await container.PutAsJsonAsync("/api/me/appearance", Valid(), Ct)).StatusCode);
    }

    [Fact]
    public async Task The_instance_default_is_match_system_or_a_preset_and_nothing_else()
    {
        using var person = await host.PersonAsync();

        var setting = host.Services.GetRequiredService<TenantSettings>().Definitions
            .Single(d => d.Name == TenantSettings.ThemeDefaultName);
        Assert.Equal(["auto", .. Appearance.Presets], setting.Choices);

        var ok = await person.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { [TenantSettings.ThemeDefaultName] = "high-contrast-dark" }, Ct);
        ok.EnsureSuccessStatusCode();
        using var fresh = await FreshPersonAsync();
        Assert.Equal("high-contrast-dark", (string?)(await ReadAsync(fresh))["instanceDefault"]);

        var refused = await person.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object> { [TenantSettings.ThemeDefaultName] = "purple" }, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        (await person.PutAsJsonAsync(
            "/api/tenant/settings", new Dictionary<string, object?> { [TenantSettings.ThemeDefaultName] = null }, Ct))
            .EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The web draws what the Host accepts: its preset and font lists are the Host's, read from the
    /// web's own source so a preset added on one side and not the other fails here.
    /// </summary>
    [Fact]
    public void The_webs_presets_and_fonts_are_the_hosts()
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "lib", "appearance.ts"));

        Assert.Equal(Appearance.Presets, Quoted(source, "PRESET_IDS"));
        Assert.Equal(Appearance.Fonts, Quoted(source, "FONT_IDS"));
        Assert.Equal(Appearance.Colours, Quoted(source, "COLOUR_KEYS"));
    }

    private static string[] Quoted(string source, string name)
    {
        var start = source.IndexOf($"export const {name} = [", StringComparison.Ordinal);
        Assert.True(start >= 0, $"web/src/lib/appearance.ts has no `export const {name} = [`.");
        var end = source.IndexOf(']', start);
        return [.. System.Text.RegularExpressions.Regex.Matches(source[start..end], "'([^']+)'").Select(m => m.Groups[1].Value)];
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Harness.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
