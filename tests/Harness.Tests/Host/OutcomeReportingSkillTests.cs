using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Harness.Contracts;
using Harness.Host;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// The `outcome-reporting` built-in skill teaches a report built from the `report` tool only: it names
/// that tool's actions and no other, copies figures without computing one, keeps Actual Spend and
/// Estimated Execution Cost apart in the product's words, reports estimates only when Actual Spend is
/// not the agent's to read, and says plainly what this platform cannot make or send.
/// </summary>
public sealed class OutcomeReportingSkillTests(HostFixture host) : IClassFixture<HostFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Body => BuiltInSkills.Find(BuiltInSkills.OutcomeReporting)!.Body;

    private static string Flat(string text) => Regex.Replace(text, @"\s+", " ");

    private static readonly string[] Actions =
        [.. PlatformMcpTools.ReportActions.Replace(" or ", ", ", StringComparison.Ordinal).Split(", ")];

    /// <summary>Every backticked name the skill may hold that is not a `report` action: the answers' own
    /// field names and values, the CSV columns, the tools, and the file names. An action the tool does not
    /// have - `export`, `costs` - is none of these, so it fails.</summary>
    private static readonly HashSet<string> NotActions = new(StringComparer.Ordinal)
    {
        // Tools and environment.
        "report", "status", "skills_search", "skills_get", "tell", "blocked", "handback", "HARNESS_SHARED",
        // Arguments and their values.
        "from", "to", "bucket", "day", "month", "team", "YYYY-MM-DD",
        // Answer fields and values.
        "total", "totals", "agentRate", "agentSeconds", "cost", "legacyRates", "reconstructed", "fallback-at-upgrade",
        "unpriced", "not-priced", "unavailable", "value", "budget", "outcomeTotalCost", "estimate",
        // CSV columns.
        "period_from", "period_to", "scope", "id", "name", "agent_seconds", "agent_hours", "currency",
        "estimated_execution_cost", "agent_rate", "rate_source", "note", "billing_type", "actual_spend", "projected", ";",
        // The outcomes answer's fields, and how the screens and the Spend page's files write a figure or a cell.
        "figures", "attentionSeconds", "weekly", "throughput", "costPerCompleted", "humanWeekly", "backlog",
        "linkedWorkflows", "toFixed(1)", "=", "+", "-", "@", "'",
        // File names.
        "<subject>-<from>-to-<to>.md", "<subject>-<from>-to-<to>-estimates-only.md", "-estimates-only", "<name>",
        "<name>-estimated-execution-cost.csv", "<name>-actual-spend.csv", "plugin-<id>",
    };

    /// <summary>The skill's text as sentences, so a rule is read as the sentence it is and a line added beside it is
    /// read on its own.</summary>
    private static string[] Sentences => Regex.Split(Flat(Body), @"(?<=[.!?])\s+(?=[A-Z*""(`-])|\s+(?=- |\| |#)");

    [Fact]
    public async Task The_reporting_skill_is_served_to_members_and_the_manager_and_not_to_the_concierge()
    {
        var skill = BuiltInSkills.Find(BuiltInSkills.OutcomeReporting)!;
        Assert.Equal([SkillRoles.Manager, SkillRoles.Member], skill.Roles);

        using var member = host.Container(host.AlphaContainerKey);
        var served = await member.GetStringAsync($"/api/me/skills/{skill.Name}", Ct);
        Assert.StartsWith($"---\nname: {skill.Name}\n", served, StringComparison.Ordinal);
        Assert.EndsWith(skill.Body, served, StringComparison.Ordinal);
        var listed = await member.GetFromJsonAsync<JsonElement[]>("/api/me/skills", Ct);
        Assert.Contains(skill.Name, listed!.Select(r => r.GetProperty("name").GetString()));

        var managerKey = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            new ContainerId(host.Alpha, TeamRegistry.DefaultManagerName).ToString(),
            PrincipalKind.Container, host.Alpha, TeamRegistry.ManagerPermits, ct: Ct);
        using var manager = host.Container(managerKey);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/api/me/skills/{skill.Name}", Ct)).StatusCode);

        var person = await host.Services.GetRequiredService<IUserStore>().FindAsync("person@example.test", Ct);
        var conciergeKey = await host.Services.GetRequiredService<IPrincipalStore>().MintAsync(
            ConciergeLaunchFactory.PrincipalId(person!.Id), PrincipalKind.TenantConcierge, null,
            ConciergeLaunchFactory.ConciergePermits, ownerUserId: person.Id, ct: Ct);
        using var concierge = host.Container(conciergeKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await concierge.GetAsync($"/api/me/skills/{skill.Name}", Ct)).StatusCode);
    }

    [Fact]
    public void The_reporting_skill_names_every_report_action_and_no_action_the_tool_lacks()
    {
        var table = Regex.Matches(Body, @"^\| `([^`]+)` \|", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(Actions.Order(StringComparer.Ordinal), table.Order(StringComparer.Ordinal));

        var stray = Regex.Matches(Body[Body.IndexOf("# Outcome reporting", StringComparison.Ordinal)..], "`([^`]+)`")
            .Select(m => m.Groups[1].Value)
            .Where(name => !Actions.Contains(name) && !NotActions.Contains(name))
            .Distinct()
            .ToList();
        Assert.True(stray.Count == 0, "Names no report action or listed field: " + string.Join(", ", stray));
    }

    [Fact]
    public void The_reporting_skill_uses_the_product_words_and_never_calls_an_estimate_spend()
    {
        var flat = Flat(Body);

        foreach (var term in new[]
                 {
                     "Actual Spend", "Estimated Agent Cost", "Estimated Plugin Cost", "Estimated Execution Cost",
                     "Agent Hours", "Human Interventions", "Human Attention Time", "Estimate unavailable", "Not priced",
                 })
        {
            Assert.Contains(term, flat, StringComparison.Ordinal);
        }

        Assert.Contains("\"" + PlatformMcpTools.SpendSentence + "\"", flat, StringComparison.Ordinal);
        Assert.Contains("Wherever Actual Spend and Estimated Execution Cost both appear, write this sentence", flat, StringComparison.Ordinal);

        // The two rule lines that name the forbidden words are the only place they may stand.
        const string NeverSpend = "An estimate is never called spend, spent, paid or cost alone.";
        const string NeverValue = "No value words near an estimate: not value, ROI, return or savings.";
        Assert.Contains(NeverSpend, flat, StringComparison.Ordinal);
        Assert.Contains(NeverValue, flat, StringComparison.Ordinal);
        var rest = flat.Replace(NeverSpend, "", StringComparison.Ordinal).Replace(NeverValue, "", StringComparison.Ordinal);

        Assert.DoesNotMatch(new Regex(@"\bspent\b|\bROI\b|\bsavings\b", RegexOptions.IgnoreCase), rest);

        // Every other "spend" is Actual Spend, the `spend` action, the setting, or its place in Settings.
        var named = Regex.Replace(rest, @"Actual Spend|`spend`|Settings → Spend|the Spend page|-actual-spend\.csv|`actual_spend`", "");
        Assert.DoesNotMatch(new Regex(@"spend", RegexOptions.IgnoreCase), named);
    }

    [Fact]
    public void The_reporting_skill_copies_figures_and_never_computes_infers_or_mixes_one()
    {
        var flat = Flat(Body);

        foreach (var rule in new[]
                 {
                     "Call `report` and nothing else for figures.",
                     "You copy figures; you never make one.",
                     "No sum, total, average, rate, share, percentage, difference or trend of your own.",
                     "Agent Hours is `agentSeconds` divided by 3600",
                     "**Never infer Actual Spend** from hours, rates, tokens or an estimate.",
                     "Each currency is its own line, never converted, never added to another.",
                     "Estimated Execution Cost and Actual Spend never share one figure, row, column, chart or total",
                     "`cost` null is written \"Estimate unavailable\" (nothing was priced), never 0",
                     "A non-empty `legacyRates` (`reconstructed`, `fallback-at-upgrade`) is carried beside the figure",
                     "Never compare an estimate to it",
                 })
        {
            Assert.True(flat.Contains(rule, StringComparison.Ordinal), $"Missing: {rule}");
        }
    }

    [Fact]
    public void The_reporting_skill_reports_estimates_only_when_Actual_Spend_is_refused_and_names_the_setting_a_person_turns_on()
    {
        var flat = Flat(Body);
        const string Label = "Let agents read Actual Spend for reports";

        Assert.Contains("When they are refused, report the estimates only", flat, StringComparison.Ordinal);
        Assert.Contains(
            $"\"Actual Spend is not in this report: a person must turn on {Label} in Admin → Settings → Spend.\"",
            flat, StringComparison.Ordinal);

        // The words a person looks for are the setting's own label on the Tenant Settings screen.
        var settings = File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "lib", "tenantSettings.ts"));
        Assert.Contains($"label: '{Label}'", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reporting_skill_writes_markdown_html_and_csv_to_the_shared_folder_and_says_what_it_cannot_make_or_send()
    {
        var flat = Flat(Body);

        foreach (var rule in new[]
                 {
                     "Write each file to the team's shared folder (`HARNESS_SHARED`)",
                     "**Markdown** is the default.",
                     "**HTML** is one self-contained file: inline styles, no script, no remote font, image or link.",
                     "**CSV** is one file per measurement",
                     "The platform writes no XLSX.",
                     "The platform writes no PDF. Make one only through a plugin member on your team whose own skill says it makes PDFs.",
                     "Mail goes only through a mail plugin member on your team that a person has set up.",
                     "\"This report was not sent: no mail plugin is set up on this team.\"",
                     "Never send mail any other way.",
                     "There is no report scheduler; ask the person for a trigger.",
                 })
        {
            Assert.True(flat.Contains(rule, StringComparison.Ordinal), $"Missing: {rule}");
        }
    }

    [Fact]
    public void The_reporting_skill_copies_money_as_given_and_writes_Agent_Hours_and_Human_Attention_Time_one_way()
    {
        var flat = Flat(Body);

        foreach (var rule in new[]
                 {
                     "**Money** is copied exactly as the answer gives it, with its currency code: never rounded, never padded, never shortened.",
                     "An answer of 151.445 is written 151.445.",
                     "under 100 hours, JavaScript's `toFixed(1)` of the hours (so 9540 seconds is 2.6, not 2.7); from 100, the hours rounded to a whole number.",
                     "A plugin member has no Agent Hours: leave them out for it.",
                     "**Human Attention Time** is copied as the answer's `attentionSeconds`, written in seconds (for example \"2014 seconds\").",
                     "It is not converted to minutes or hours: that is not the one conversion.",
                 })
        {
            Assert.True(flat.Contains(rule, StringComparison.Ordinal), $"Missing: {rule}");
        }

        // "To the cent" is a rounding the skill may not ask for: the screens round, a report copies.
        Assert.DoesNotContain("to the cent", flat, StringComparison.OrdinalIgnoreCase);

        // The rule the skill names is the one the screens run (hoursFigure in web/src/lib/teamCost.ts).
        var screens = File.ReadAllText(Path.Combine(RepoRoot(), "web", "src", "lib", "teamCost.ts"));
        Assert.Contains("return hours < 100 ? hours.toFixed(1) : Math.round(hours).toLocaleString()", screens, StringComparison.Ordinal);
    }

    [Fact]
    public void The_reporting_skill_neutralises_formula_cells_as_the_Spend_files_do_names_the_period_fields_and_never_overwrites_an_estimates_only_report()
    {
        var flat = Flat(Body);

        // Formula cells: the same first characters, and the same quote, as the Spend page's own files.
        const string Cells = "A text cell whose first character is `=`, `+`, `-`, `@`, a tab or a carriage return gets a single quote (`'`) before it, as the Spend page's own files do";
        Assert.Contains(Cells, flat, StringComparison.Ordinal);
        Assert.Contains("A plain number such as -12.50 is written as it is.", flat, StringComparison.Ordinal);
        foreach (var first in new[] { "=", "+", "-", "@", "\t", "\r" })
        {
            Assert.Equal("'" + first + "SUM(A1)", SpendExport.Neutral(first + "SUM(A1)"));
        }

        Assert.Equal("-12.50", SpendExport.Neutral("-12.50"));
        Assert.Equal("Seats", SpendExport.Neutral("Seats"));

        // The outcomes answer: which fields are the period's, which are the last eight weeks, which are now.
        Assert.Contains("Only each outcome's `figures`, `cost` and `human` are read for the period you sent.", flat, StringComparison.Ordinal);
        Assert.Contains("`weekly`, `throughput`, `costPerCompleted` and `humanWeekly` are the last eight weeks up to now", flat, StringComparison.Ordinal);
        Assert.Contains("`backlog` and `linkedWorkflows` are as they stand now, whatever the period", flat, StringComparison.Ordinal);
        var described = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Harness.Host", "OutcomeEndpoints.cs"));
        // The route's own description says the same of `weekly`.
        Assert.Contains("`weekly` (the last 8 weeks, Monday-start \"", described, StringComparison.Ordinal);
        Assert.Contains("\"UTC, whatever the period:", described, StringComparison.Ordinal);

        // File names: one stem per report, and a setting-off report's never equals a setting-on report's of the same period.
        Assert.Contains("Every file of one report shares that stem, the `<name>` below.", flat, StringComparison.Ordinal);
        Assert.Contains("When `spend` was refused, the stem ends `-estimates-only` (`<subject>-<from>-to-<to>-estimates-only.md`), so a later report on the same period with Actual Spend never overwrites it.", flat, StringComparison.Ordinal);
        Assert.Contains("where `from` and `to` are the days you sent (`to` is the day after the last).", flat, StringComparison.Ordinal);
    }

    /// <summary>
    /// A LINE ADDED BESIDE A RULE THAT UNDOES IT. The rule tests above find each rule's own sentence; these find a
    /// sentence that says the opposite next to it: adding or converting currencies, the Spend overview's estimate
    /// read as one team's, or Human Attention Time turned into minutes or hours.
    /// </summary>
    [Fact]
    public void The_reporting_skill_holds_no_sentence_against_its_own_rules()
    {
        var sentences = Sentences;

        // Currencies: the only sentences that join a currency to a sum, a total, an addition or a conversion are the rules.
        string[] currencyRules =
        [
            "**Never add currencies.** Actual Spend and Projected are per currency.",
            "Each currency is its own line, never converted, never added to another.",
            "ISO dates, currency codes and the answer's ids; nothing inferred, nothing added across currencies.",
        ];
        var currency = new Regex(@"currenc", RegexOptions.IgnoreCase);
        var joining = new Regex(@"\b(sum|summ|add|total|convert|combin|grand|aggregat|exchange)\w*", RegexOptions.IgnoreCase);
        var flat = Flat(Body);
        foreach (var rule in currencyRules) Assert.True(flat.Contains(rule, StringComparison.Ordinal), $"Missing: {rule}");
        // A backticked name is a field or a column, not an instruction: `total` and `currency` in one table row add nothing.
        static string Prose(string sentence) => Regex.Replace(sentence, "`[^`]*`", "");
        var against = sentences
            .Where(s => currency.IsMatch(Prose(s)) && joining.IsMatch(Prose(s)))
            .Where(s => !currencyRules.Any(rule => rule.Contains(s, StringComparison.Ordinal) || s.Contains(rule, StringComparison.Ordinal)))
            .ToList();
        Assert.True(against.Count == 0, "Joins currencies: " + string.Join(" | ", against));

        // The Spend overview's estimate is every team's, and no other sentence makes it a team's own.
        const string EveryTeams = "The `estimate` inside the `spend` answer is every team's: it is not one team's figure.";
        Assert.Contains(EveryTeams, flat, StringComparison.Ordinal);
        var teams = sentences
            .Select(s => s.Replace(EveryTeams, "", StringComparison.Ordinal))
            .Where(s => s.Contains("`estimate`", StringComparison.Ordinal))
            .Where(s => Regex.IsMatch(s, @"team's|team’s|own team|this team", RegexOptions.IgnoreCase))
            .ToList();
        Assert.True(teams.Count == 0, "Reads the overview's estimate as one team's: " + string.Join(" | ", teams));

        // Human Attention Time is written in seconds: no sentence about it names minutes or hours, but the one saying not to.
        const string NotConverted = "It is not converted to minutes or hours: that is not the one conversion.";
        var attention = sentences
            .Where(s => s.Contains("Attention Time", StringComparison.Ordinal) || s.Contains("attentionSeconds", StringComparison.Ordinal))
            .Where(s => Regex.IsMatch(s, @"\b(minutes?|hours?|h|m)\b") && s != NotConverted)
            .ToList();
        Assert.True(attention.Count == 0, "Converts Human Attention Time: " + string.Join(" | ", attention));
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Harness.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("No repository root above the test binaries.");
    }
}
