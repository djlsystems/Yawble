using System.Text.Json;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>One setting of a member that does not fit the version now active, by field name, with the
/// sentence saying why. Never a value.</summary>
public sealed record AttentionField(string Field, string Reason);

/// <summary>A member hired on an upgraded plugin whose saved settings do not fit the new version, with
/// every field that does not, not only the first.</summary>
public sealed record UpgradeAttention(string Team, string Member, string TeamName, string MemberName, IReadOnlyList<AttentionField> Fields);

/// <summary>
/// WHICH MEMBERS' SETTINGS NEED A PERSON AFTER A PLUGIN UPGRADE (PLG 2). A member is hired as
/// <c>plugin:&lt;id&gt;</c> with no version, so a newer active version moves every member onto it at
/// once, settings kept by field name. This reads each member's saved settings against the new
/// manifest with the same checks a run and a save make (<see cref="PluginConfigField.Refusal"/>,
/// <see cref="PluginConfigField.BoundsRefusal"/>, as <see cref="PluginMemberRunner.EffectiveConfig"/>,
/// <see cref="PluginMemberRunner.OutOfRange"/> and <see cref="PluginMemberRunner.SettingsRefusal"/>
/// do) - but gathers EVERY problem per member instead of stopping at the first:
///
/// <list type="bullet">
/// <item>a required field with no saved value and no default;</item>
/// <item>a saved value of the wrong type, or outside the field's bounds;</item>
/// <item>a saved value for a field the new version no longer declares;</item>
/// <item>a secret binding, or the member's own secret value, for a secret the new version does not declare.</item>
/// </list>
///
/// It changes nothing: a member is never removed or stopped by an upgrade. One whose settings need
/// attention fails its runs, naming the field, until a person fixes it - as before. Never a value:
/// only field names and sentences.
/// </summary>
public sealed class PluginUpgradeAttention(TeamRegistry teams, IPluginMemberSettings settings, IPluginMemberSecretRows? secretRows = null)
{
    /// <summary>Each member hired on <paramref name="manifest"/>'s plugin whose saved settings do not
    /// fit it, in team then member order. Empty when every member's fit.</summary>
    public async Task<IReadOnlyList<UpgradeAttention>> ForAsync(PluginManifest manifest, CancellationToken ct = default)
    {
        var found = new List<UpgradeAttention>();

        foreach (var (team, member) in teams.All()
            .SelectMany(team => team.Containers.Select(member => (team, member)))
            .Where(pair => MemberRef.IsPlugin(pair.member.Agent, out var hired) && hired == manifest.Id)
            .OrderBy(pair => pair.team.Id, StringComparer.Ordinal)
            .ThenBy(pair => pair.member.Id, StringComparer.Ordinal))
        {
            var id = new ContainerId(team.Id, member.Id);
            var bound = await settings.ForAsync(id, ct);
            var own = secretRows is null ? [] : (await secretRows.RowsAsync(id, ct)).Select(r => r.Field).ToList();

            var fields = Problems(manifest, bound, own);
            if (fields.Count > 0) found.Add(new UpgradeAttention(team.Id, member.Id, team.Name, member.Name, fields));
        }

        return found;
    }

    /// <summary>Every field of <paramref name="bound"/> (and of the member's own secret values, by
    /// field name in <paramref name="ownSecretFields"/>) that does not fit <paramref name="manifest"/>,
    /// each once, in field order.</summary>
    public static IReadOnlyList<AttentionField> Problems(
        PluginManifest manifest, PluginMemberSettings bound, IReadOnlyCollection<string>? ownSecretFields = null)
    {
        var problems = new List<AttentionField>();
        var named = $"{manifest.Id} {manifest.Version}";

        foreach (var (name, field) in manifest.Config)
        {
            if (bound.Config.TryGetValue(name, out var value))
            {
                // THE TYPE FIRST, then the bounds: a value of the wrong type has no bounds to be inside.
                if (field.Refusal(name, value, bounds: false) is { } wrong) problems.Add(new(name, wrong));
                else if (field.BoundsRefusal(name, value) is { } outside) problems.Add(new(name, outside));
            }
            else if (field.Default is null && field.Required)
            {
                problems.Add(new(name, $"`{name}` is required by {named}, and this member has no value for it and the plugin gives no default."));
            }
        }

        foreach (var name in bound.Config.Keys.Where(k => !manifest.Config.ContainsKey(k)))
        {
            problems.Add(new(name, $"`{name}` has a saved value, and {named} no longer has this setting: the value is kept and not used."));
        }

        foreach (var name in bound.Secrets.Keys.Where(k => !manifest.Secrets.ContainsKey(k)))
        {
            problems.Add(new(name, $"`{name}` is bound to a secret, and {named} does not declare the secret `{name}`: the binding is kept and not used."));
        }

        foreach (var name in (ownSecretFields ?? []).Where(f => !manifest.Secrets.ContainsKey(f) && !bound.Secrets.ContainsKey(f)))
        {
            problems.Add(new(name, $"`{name}` holds this member's own secret value, and {named} does not declare the secret `{name}`: the value is kept and not used."));
        }

        return [.. problems.DistinctBy(p => p.Field, StringComparer.Ordinal).OrderBy(p => p.Field, StringComparer.Ordinal)];
    }
}
