using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;

namespace Harness.Tests;

/// <summary>
/// THE <c>setup</c> BLOCK of <c>solution.json</c>: the plain questions the setup wizard asks. A valid
/// block passes and the plan carries every question; each refusal names <c>solution.json</c> and the
/// field, all at once; an unknown key inside it is listed as ignored; a package without one checks as
/// before. The contract web builds against is <c>setup-contract.md</c> in the team's documents.
/// </summary>
public sealed class SolutionSetupTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"solution-setup-{Guid.NewGuid():N}");

    private static readonly SolutionPlatform Platform = new(
        name => name.ToLowerInvariant() switch { "model" => true, "program" => false, _ => null },
        EventCatalog.For,
        _ => true,
        BuiltInSkills.IsBuiltIn);

    private static SolutionCheck Check(string folder) => new SolutionChecker(Platform).Check(folder);

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// The Job Tracker sample with a mailbox slot on its plugin, a required mailbox input, a required
    /// number setting input (archiveAfterDays: min 1, whole), an optional salaryMin input with no
    /// question, and a setup block asking for the mailbox, the resume and two settings.
    /// </summary>
    private string WithSetup()
    {
        var folder = SolutionSamples.JobTracker("1.0.0", _scratch);

        SolutionSamples.EditJson(Path.Combine(folder, "plugins", "job-board", "plugin.json"), p => p["connections"] = JsonNode.Parse(
            """{"mail":{"description":"The mailbox.","providers":["google","microsoft","imap"],"scopes":{"google":["https://mail.google.com/"]},"required":true}}"""));

        SolutionSamples.Edit(folder, m =>
        {
            m["inputs"]!["connections"] = new JsonArray(new JsonObject { ["member"] = "Scout", ["slot"] = "mail", ["description"] = "The inbox to watch.", ["required"] = true });
            var settings = m["inputs"]!["settings"]!.AsArray();
            settings.Add(new JsonObject { ["member"] = "Scout", ["setting"] = "archiveAfterDays", ["description"] = "How old a posting may be.", ["required"] = true });
            settings.Add(new JsonObject { ["member"] = "Scout", ["setting"] = "salaryMin", ["description"] = "Lowest salary." });

            m["setup"] = JsonNode.Parse(
                """
                {
                  "welcome": "Finds jobs that match what you want.\nWrites a cover letter for each one.",
                  "needs": "a resume (.docx) and a mailbox",
                  "connections": [
                    { "member": "scout", "slot": "mail", "question": "Which mailbox should job emails come from?", "hint": "Gmail, Outlook or another mailbox." }
                  ],
                  "documents": [
                    { "folder": "Resume", "question": "Upload your resume", "hint": "A .docx file works best." }
                  ],
                  "settings": [
                    { "member": "Scout", "setting": "archiveAfterDays", "question": "How many days old may a job be?", "default": 14 },
                    { "member": "Scout", "setting": "sources", "question": "Which job boards should it read?", "default": ["sample"], "advanced": true }
                  ]
                }
                """);
        });

        return folder;
    }

    private static List<SolutionRefusal> Refusals(string folder)
    {
        var check = Check(folder);
        Assert.False(check.Ok);
        Assert.Null(check.Plan);
        return [.. check.Refusals];
    }

    private static SolutionRefusal Refused(string folder, string field) =>
        Assert.Single(Refusals(folder), r => r.File == "solution.json" && r.Field == field);

    // ---- Passes ----------------------------------------------------------------------------------

    [Fact]
    public void A_valid_setup_block_passes_and_the_plan_carries_its_questions()
    {
        var check = Check(WithSetup());

        Assert.True(check.Ok, string.Join("\n", check.Refusals));
        var setup = check.Plan!.Setup!;

        Assert.Equal("Finds jobs that match what you want.\nWrites a cover letter for each one.", setup.Welcome);
        Assert.Equal("a resume (.docx) and a mailbox", setup.Needs);

        var mailbox = Assert.Single(setup.Connections);
        Assert.Equal(("Scout", "mail", "Which mailbox should job emails come from?", "Gmail, Outlook or another mailbox.", false, true),
            (mailbox.Member, mailbox.Slot, mailbox.Question, mailbox.Hint, mailbox.Advanced, mailbox.Required));
        Assert.Equal(("job-board", "The inbox to watch."), (mailbox.Plugin, mailbox.Description));
        Assert.Equal(["google", "microsoft", "imap"], mailbox.Providers);
        Assert.Equal(["https://mail.google.com/"], mailbox.Scopes["google"]);

        var resume = Assert.Single(setup.Documents);
        Assert.Equal(("Resume", "Upload your resume", "A .docx file works best.", false, true), (resume.Folder, resume.Question, resume.Hint, resume.Advanced, resume.Required));

        // The questions in setup order, then the one input the package asks no question for.
        Assert.Equal(["archiveAfterDays", "sources", "salaryMin"], setup.Settings.Select(s => s.Setting));

        var days = setup.Settings[0];
        Assert.Equal(("How many days old may a job be?", false, true, "number", 1d, true), (days.Question, days.Advanced, days.Required, days.Type, days.Min, days.Integer));
        Assert.Equal(14, days.Default!.Value.GetInt32());

        var sources = setup.Settings[1];
        Assert.True(sources.Advanced);
        Assert.Equal(["sample", "adzuna", "usajobs", "themuse"], sources.Choices!);
        Assert.Equal(["sample"], sources.Default!.Value.EnumerateArray().Select(v => v.GetString()));

        var salary = setup.Settings[2];
        Assert.Equal((null, null, true, false, "Lowest salary."), (salary.Question, salary.Hint, salary.Advanced, salary.Required, salary.Description));

        // The shape web reads, as the check route serialises it.
        var json = JsonNode.Parse(JsonSerializer.Serialize(check.Plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!;
        Assert.Equal("Upload your resume", json["setup"]!["documents"]![0]!["question"]!.GetValue<string>());
        Assert.Equal(14, json["setup"]!["settings"]![0]!["default"]!.GetValue<int>());
        Assert.Equal("imap", json["setup"]!["connections"]![0]!["providers"]![2]!.GetValue<string>());
        Assert.Empty(check.Plan!.Ignored);
    }

    [Fact]
    public void A_setting_question_with_no_default_of_its_own_carries_the_plugins_default()
    {
        var folder = WithSetup();
        SolutionSamples.Edit(folder, m => m["setup"]!["settings"]![1]!.AsObject().Remove("default"));

        var check = Check(folder);

        Assert.True(check.Ok, string.Join("\n", check.Refusals));
        Assert.Equal("[]", check.Plan!.Setup!.Settings[1].Default!.Value.GetRawText());
    }

    [Fact]
    public void A_package_without_setup_checks_exactly_as_today()
    {
        // The sample has no setup block and a required document input with no question: it passes,
        // and the plan says there is no setup, so the web shows today's wizard.
        var folder = SolutionSamples.JobTracker("1.0.0", _scratch);
        var check = Check(folder);

        Assert.True(check.Ok, string.Join("\n", check.Refusals));
        Assert.Null(check.Plan!.Setup);
        Assert.Empty(check.Plan.Ignored);

        var json = JsonNode.Parse(JsonSerializer.Serialize(check.Plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
        Assert.True(json.ContainsKey("setup"));
        Assert.Null(json["setup"]);

        // And a broken one is refused exactly as before: one refusal, nothing about setup.
        SolutionSamples.Edit(folder, m => m["inputs"]!["documents"]![0]!["required"] = "yes");
        var refusal = Assert.Single(Check(folder).Refusals);
        Assert.Equal("inputs.documents[0].required", refusal.Field);
    }

    [Fact]
    public void An_unknown_key_inside_setup_is_ignored_and_listed()
    {
        var folder = WithSetup();
        SolutionSamples.Edit(folder, m =>
        {
            m["later"] = true;
            m["setup"]!["colour"] = "teal";
            m["setup"]!["settings"]![1]!["tooltip"] = "x";
        });

        var check = Check(folder);

        Assert.True(check.Ok, string.Join("\n", check.Refusals));
        Assert.Equal(["later", "setup.colour", "setup.settings[1].tooltip"], check.Plan!.Ignored);
    }

    // ---- Refusals, one per case ------------------------------------------------------------------

    public sealed record Breakage(string Case, Action<JsonObject> Break, string Field, string Says)
    {
        public override string ToString() => Case;
    }

    private static JsonNode Setup(JsonObject m) => m["setup"]!;

    private static readonly Breakage[] All =
    [
        new("setup not an object", m => m["setup"] = "yes", "setup", "must be an object"),
        new("welcome missing", m => Setup(m).AsObject().Remove("welcome"), "setup.welcome", "is required"),
        new("welcome empty", m => Setup(m)["welcome"] = "  ", "setup.welcome", "must be a non-empty string"),
        new("welcome four lines", m => Setup(m)["welcome"] = "a\nb\nc\nd", "setup.welcome", "at most 3 lines"),
        new("welcome too long", m => Setup(m)["welcome"] = new string('w', 281), "setup.welcome", "at most 280 characters"),
        new("needs empty", m => Setup(m)["needs"] = "", "setup.needs", "must be a non-empty string"),
        new("needs too long", m => Setup(m)["needs"] = new string('n', 121), "setup.needs", "at most 120 characters"),
        new("required connection unasked", m => Setup(m).AsObject().Remove("connections"), "setup.connections", "inputs.connections[0]"),
        new("required document unasked", m => Setup(m)["documents"] = new JsonArray(), "setup.documents", "inputs.documents[0]"),
        new("required setting unasked", m => Setup(m)["settings"]!.AsArray().RemoveAt(0), "setup.settings", "inputs.settings[1]"),
        new("question missing", m => Setup(m)["documents"]![0]!.AsObject().Remove("question"), "setup.documents[0].question", "is required"),
        new("question empty", m => Setup(m)["connections"]![0]!["question"] = "", "setup.connections[0].question", "must be a non-empty string"),
        new("question too long", m => Setup(m)["settings"]![0]!["question"] = new string('q', 101), "setup.settings[0].question", "at most 100 characters"),
        new("question two lines", m => Setup(m)["settings"]![0]!["question"] = "a\nb", "setup.settings[0].question", "one line"),
        new("hint empty", m => Setup(m)["documents"]![0]!["hint"] = " ", "setup.documents[0].hint", "must be a non-empty string"),
        new("hint too long", m => Setup(m)["documents"]![0]!["hint"] = new string('h', 201), "setup.documents[0].hint", "at most 200 characters"),
        new("default below a number setting's min", m => Setup(m)["settings"]![0]!["default"] = 0, "setup.settings[0].default", "must be at least 1"),
        new("default not whole for an integer setting", m => Setup(m)["settings"]![0]!["default"] = 1.5, "setup.settings[0].default", "whole number"),
        new("default not one of a list's choices", m => Setup(m)["settings"]![1]!["default"] = new JsonArray("linkedin"), "setup.settings[1].default", "must be one of"),
        new("default on a document", m => Setup(m)["documents"]![0]!["default"] = "x", "setup.documents[0].default", "only a setting"),
        new("advanced not a flag", m => Setup(m)["settings"]![1]!["advanced"] = "yes", "setup.settings[1].advanced", "true or false"),
        new("setting naming no input", m => Setup(m)["settings"]![1]!["setting"] = "keywords", "setup.settings[1].setting", "no `inputs.settings` entry"),
        new("slot naming no input", m => Setup(m)["connections"]![0]!["slot"] = "calendar", "setup.connections[0].slot", "no `inputs.connections` entry"),
        new("folder naming no input", m => Setup(m)["documents"]![0]!["folder"] = "Letters", "setup.documents[0].folder", "no `inputs.documents` entry"),
        new("setting asked twice", m => Setup(m)["settings"]!.AsArray().Add(JsonNode.Parse("""{"member":"Scout","setting":"archiveAfterDays","question":"Again?"}""")), "setup.settings[2].setting", "asked twice"),
        new("entry not an object", m => Setup(m)["documents"]![0] = "Resume", "setup.documents[0]", "must be an object"),
        new("list not an array", m => Setup(m)["settings"] = "all", "setup.settings", "must be an array"),
    ];

    public static TheoryData<string> Breakages() => [.. All.Select(b => b.Case)];

    [Theory]
    [MemberData(nameof(Breakages))]
    public void Each_setup_refusal_names_solution_json_and_its_field(string @case)
    {
        var breakage = All.Single(b => b.Case == @case);
        var folder = WithSetup();
        SolutionSamples.Edit(folder, breakage.Break);

        Assert.Contains(breakage.Says, Refused(folder, breakage.Field).Reason);
    }

    [Fact]
    public void Every_refusal_in_a_broken_setup_block_is_named_at_once()
    {
        var folder = WithSetup();
        SolutionSamples.Edit(folder, m =>
        {
            var setup = Setup(m);
            setup["welcome"] = "";
            setup["needs"] = new string('n', 121);
            setup["documents"]![0]!["question"] = new string('q', 101);
            setup["settings"]![0]!["default"] = 0;
            setup["settings"]![1]!["hint"] = "";
            setup["connections"]![0]!["advanced"] = 1;
        });

        var fields = Refusals(folder).Where(r => r.File == "solution.json").Select(r => r.Field).Order(StringComparer.Ordinal);

        Assert.Equal(
            ["setup.connections[0].advanced", "setup.documents[0].question", "setup.needs", "setup.settings[0].default", "setup.settings[1].hint", "setup.welcome"],
            fields);
    }

    [Fact]
    public void A_required_input_with_no_question_is_named_beside_another_refused_entry_in_its_list()
    {
        var folder = WithSetup();
        SolutionSamples.Edit(folder, m =>
        {
            m["inputs"]!["settings"]![2]!["required"] = true;
            Setup(m)["settings"]![1]!["hint"] = "";
        });

        var refusals = Refusals(folder).Where(r => r.File == "solution.json").ToList();

        Assert.Equal(["setup.settings", "setup.settings[1].hint"], refusals.Select(r => r.Field).Order(StringComparer.Ordinal));
        Assert.Contains("inputs.settings[2]", refusals.Single(r => r.Field == "setup.settings").Reason);
    }

    [Fact]
    public void An_input_whose_own_question_is_refused_is_not_also_named_unasked()
    {
        var folder = WithSetup();
        SolutionSamples.Edit(folder, m => Setup(m)["settings"]![0]!["hint"] = "");

        Assert.Equal(["setup.settings[0].hint"], Refusals(folder).Where(r => r.File == "solution.json").Select(r => r.Field));
    }
}
