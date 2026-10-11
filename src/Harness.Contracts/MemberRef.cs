using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Harness.Contracts;

/// <summary>
/// WHAT A MEMBER RUNS, READ FROM ONE STRING: <c>team_members.agent</c>, which is
/// <see cref="ContainerDefinition.Agent"/> and <see cref="ContainerSnapshot.Agent"/>.
///
/// A bare name is an Agent preset, exactly as it always was. <c>plugin:&lt;id&gt;</c> is an
/// installed plugin. An Agent preset's name cannot contain a colon (<c>AgentDefinition</c>'s own
/// name rule), so no existing value can be read as a plugin and no schema change is needed: every
/// row a team already has means what it meant.
///
/// THE ONE PARSE. Every reader that would ask the Agent catalog about a member asks this first,
/// so a plugin member is "not an agent" rather than "a missing agent".
/// </summary>
public static partial class MemberRef
{
    public const string PluginPrefix = "plugin:";

    public const string AgentKind = "agent";

    public const string PluginKind = "plugin";

    /// <summary>Whether <paramref name="implementation"/> names a plugin, and which.</summary>
    public static bool IsPlugin(string? implementation, [NotNullWhen(true)] out string? pluginId)
    {
        pluginId = null;

        if (implementation is null
            || !implementation.StartsWith(PluginPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        pluginId = implementation[PluginPrefix.Length..].Trim();
        return true;
    }

    public static bool IsPlugin(string? implementation) => IsPlugin(implementation, out _);

    /// <summary><see cref="AgentKind"/> or <see cref="PluginKind"/>.</summary>
    public static string KindOf(string? implementation) => IsPlugin(implementation) ? PluginKind : AgentKind;

    /// <summary>The reference for plugin <paramref name="id"/>.</summary>
    public static string ForPlugin(string id) => PluginPrefix + id;

    /// <summary>
    /// A plugin id: lowercase letters, digits and hyphens, starting with a letter or digit, at most
    /// 48 characters - the same allowlist a skill name has, because a plugin's id becomes part of
    /// its skills' names and its event types.
    /// </summary>
    public static bool IsValidPluginId(string? id) => id is not null && PluginId().IsMatch(id);

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,47}$")]
    private static partial Regex PluginId();
}
