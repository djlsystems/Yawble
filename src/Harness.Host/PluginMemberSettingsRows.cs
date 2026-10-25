using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// WHAT A <c>team_member_config</c> ROW HOLDS, for every provider's <see cref="IPluginMemberSettingsStore"/>
/// and the member-secret rows that save it beside their own: the three JSON columns as they are written
/// and read back. No SQL; each store has its own.
/// </summary>
public static class PluginMemberSettingsRows
{
    public static string ConfigJson(PluginMemberSettings settings) => JsonSerializer.Serialize(settings.Config);

    public static string SecretsJson(PluginMemberSettings settings) => JsonSerializer.Serialize(settings.Secrets);

    public static string ConnectionsJson(PluginMemberSettings settings) => JsonSerializer.Serialize(settings.Connections);

    /// <summary>The settings a row's three columns hold.</summary>
    public static PluginMemberSettings Read(string configJson, string secretsJson, string connectionsJson) =>
        new(
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(configJson) ?? [],
            JsonSerializer.Deserialize<Dictionary<string, string>>(secretsJson) ?? [])
        {
            Connections = JsonSerializer.Deserialize<Dictionary<string, string>>(connectionsJson) ?? [],
        };

    /// <summary>The values a slot-to-id column binds: connection ids, or logical keys.</summary>
    public static IEnumerable<string> Bound(string json) =>
        (JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? []).Values;

    /// <summary>Every (field, logical key) a row's <c>secrets_json</c> binds.</summary>
    public static IEnumerable<(string Field, string Key)> SecretBindings(string secretsJson) =>
        (JsonSerializer.Deserialize<Dictionary<string, string>>(secretsJson) ?? []).Select(b => (b.Key, b.Value));
}
