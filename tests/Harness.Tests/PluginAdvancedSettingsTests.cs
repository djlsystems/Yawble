using System.Text.Json;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A PLUGIN SETTING MAY BE ADVANCED, AND A NEW VERSION MAY DROP A SETTING. `"advanced": true` in a
/// manifest's `config` field puts it behind the settings form's Advanced section; anything but true
/// or false is refused naming the field. A value stored for a field the active version no longer
/// declares is kept when it is sent back unchanged (a run never sees it), as a stored number outside
/// later bounds is; a new value for an unknown field is still refused.
/// </summary>
public sealed class PluginAdvancedSettingsTests
{
    private static PluginConfigField? Field(string json, out string? refusal)
    {
        var (field, why) = PluginConfigField.Parse("cap", JsonDocument.Parse(json).RootElement);
        refusal = why;
        return field;
    }

    [Fact]
    public void A_field_marked_advanced_is_advanced_and_one_left_unmarked_is_not()
    {
        Assert.True(Field("""{"type":"number","default":5,"advanced":true}""", out _)!.Advanced);
        Assert.False(Field("""{"type":"number","default":5,"advanced":false}""", out _)!.Advanced);
        Assert.False(Field("""{"type":"number","default":5}""", out _)!.Advanced);
    }

    [Fact]
    public void Advanced_that_is_not_true_or_false_is_refused_naming_the_field()
    {
        Assert.Null(Field("""{"type":"number","advanced":"yes"}""", out var refusal));
        Assert.Equal("`config.cap.advanced` must be true or false.", refusal);
    }

    private static PluginManifest Manifest() => PluginManifest.Parse(
        """
        {
          "schemaVersion": 1, "id": "texter", "name": "Texter", "description": "Sends texts.", "version": "1.1.0",
          "protocol": "harness.member/1", "executable": { "path": "bin/texter" },
          "config": { "mode": { "type": "string", "default": "dryRun" } }
        }
        """).Manifest!;

    private static PluginMemberSettings Settings(params (string Name, object Value)[] config) => new(
        config.ToDictionary(c => c.Name, c => JsonSerializer.SerializeToElement(c.Value), StringComparer.Ordinal),
        new Dictionary<string, string>(StringComparer.Ordinal));

    [Fact]
    public void A_value_stored_for_a_field_the_new_version_dropped_is_kept_when_sent_back_unchanged()
    {
        var stored = Settings(("mode", "send"), ("quietHoursStart", "20:00"));

        Assert.Null(PluginMemberRunner.SettingsRefusal(Manifest(), stored, null, stored: stored));

        var (config, refusal) = PluginMemberRunner.EffectiveConfig(Manifest(), stored);
        Assert.Null(refusal);
        Assert.Equal(["mode"], config.Keys);
    }

    [Fact]
    public void A_new_value_for_a_field_the_plugin_does_not_declare_is_still_refused()
    {
        var stored = Settings(("mode", "send"), ("quietHoursStart", "20:00"));

        var changed = Settings(("mode", "send"), ("quietHoursStart", "21:00"));
        Assert.StartsWith("`quietHoursStart` is not a configuration field of plugin 'texter'.", PluginMemberRunner.SettingsRefusal(Manifest(), changed, null, stored: stored));

        var fresh = Settings(("mode", "send"), ("quietHoursStart", "20:00"));
        Assert.StartsWith("`quietHoursStart` is not a configuration field", PluginMemberRunner.SettingsRefusal(Manifest(), fresh, null));
    }
}
