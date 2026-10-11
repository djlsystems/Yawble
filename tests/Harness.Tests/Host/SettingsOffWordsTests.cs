using System.Text.RegularExpressions;
using Harness.Host;

namespace Harness.Tests.Host;

/// <summary>
/// WHERE AN INSTANCE-WIDE SETTING IS, AS THE CONSOLE LABELS IT: the ribbon's Admin group, its Settings
/// button, then the Settings dialog's tab the setting is on - read from the web source, so a relabelled
/// group, button or tab, or a setting moved to another tab, fails the sentences that name it.
/// </summary>
internal static class ConsoleSettingsLabels
{
    private static string Web(params string[] path) =>
        File.ReadAllText(Path.Combine([SolutionSamples.RepoRoot(), "web", "src", .. path]));

    private static string One(string text, string pattern, string what)
    {
        var match = Regex.Match(text, pattern);
        Assert.True(match.Success, $"The console source no longer says {what}.");
        return match.Groups[1].Value;
    }

    /// <summary>"Admin > Settings > System" for a setting key such as <c>marketplace.check</c>.</summary>
    public static string WhereIs(string key)
    {
        var ribbon = Web("lib", "ribbon.ts");
        var fields = Web("lib", "tenantSettings.ts");
        var dialog = Web("components", "TenantSettingsDialog.vue");

        var admin = One(ribbon, @"id: 'admin',\s*label: '([^']+)'", "the Admin group's label");
        var settings = One(ribbon, @"action: TenantSettingsAction,\s*label: '([^']+)'", "the Settings button's label");
        var constant = One(fields, $@"export const (\w+) = '{Regex.Escape(key)}'", $"which constant names {key}");
        var tab = One(fields, $@"name: {constant},\s*tab: '(\w+)'", $"which tab {key} is on");
        var label = One(dialog, $@"\{{ name: '{tab}', label: '([^']+)' \}}", $"the {tab} tab's label");
        return $"{admin} > {settings} > {label}";
    }
}

public sealed class SettingsOffWordsTests
{
    [Fact]
    public void The_console_labels_read_as_the_host_names_them()
    {
        Assert.Equal("Admin > Settings > System", ConsoleSettingsLabels.WhereIs("marketplace.check"));
        Assert.Equal("Admin > Settings > System", ConsoleSettingsLabels.WhereIs("updates.check"));
        Assert.Equal("Admin > Settings > Concierge", ConsoleSettingsLabels.WhereIs("concierge.mayMerge"));
    }

    [Fact]
    public void A_concierge_refused_by_a_setting_that_is_off_is_told_where_a_person_turns_it_on()
    {
        Assert.Contains(
            $"only a person turns it on, in {ConsoleSettingsLabels.WhereIs(TenantSettings.ConciergeMayMergeName)}. Nothing was changed.",
            ConciergeMergeGate.SettingOff, StringComparison.Ordinal);
        Assert.Contains(
            $"only a person turns it on, in {ConsoleSettingsLabels.WhereIs(TenantSettings.ConciergeMayArchiveName)}. Nothing was changed.",
            ConciergeArchiveGate.SettingOff, StringComparison.Ordinal);
    }
}
