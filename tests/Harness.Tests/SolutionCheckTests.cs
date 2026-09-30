using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Solutions;

namespace Harness.Tests;

/// <summary>
/// THE SOLUTION CHECK: the committed Job Tracker package passes and its plan is what an install would
/// create; every way a package can be wrong is refused naming the file and the field. Each refusal
/// case is its own test, made by changing one thing in a copy of the sample.
/// </summary>
public sealed class SolutionCheckTests : IDisposable
{
    private readonly string _scratch = Path.Combine(Path.GetTempPath(), $"solution-check-{Guid.NewGuid():N}");

    /// <summary>A platform with one model preset, one program preset, the real event catalog and
    /// built-in skills, and every runtime present.</summary>
    private static readonly SolutionPlatform Platform = new(
        name => name.ToLowerInvariant() switch { "model" => true, "program" => false, _ => null },
        EventCatalog.For,
        _ => true,
        BuiltInSkills.IsBuiltIn);

    private static SolutionCheck Check(string folder) => new SolutionChecker(Platform).Check(folder);

    private string Sample(string version = "1.0.0") => SolutionSamples.JobTracker(version, _scratch);

    /// <summary>The check refused, and among its refusals is one on this file and field.</summary>
    private static SolutionRefusal Refused(SolutionCheck check, string file, string field)
    {
        Assert.False(check.Ok);
        Assert.Null(check.Package);
        Assert.Null(check.Plan);
        return Assert.Single(check.Refusals, r => r.File == file && r.Field == field);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---- The sample -------------------------------------------------------------------------

    [Fact]
    public void The_job_tracker_sample_passes_and_its_plan_is_what_an_install_would_create()
    {
        var folder = Sample();
        var check = Check(folder);

        Assert.True(check.Ok, string.Join("\n", check.Refusals));
        var plan = check.Plan!;

        Assert.Equal(("job-tracker", "Job Tracker", "1.0.0"), (plan.Package.Id, plan.Package.Name, plan.Package.Version));
        Assert.True(plan.Package.Readme);
        Assert.Equal("Job Tracker", plan.Team.Name);
        Assert.Contains("Drafts folder", plan.Team.Instructions);

        Assert.Equal(["Coordinator", "Scout", "Writer"], plan.Members.Select(m => m.Name));
        var coordinator = plan.Members[0];
        Assert.Equal((MemberRef.AgentKind, SolutionManifest.RoleManager), (coordinator.Kind, coordinator.Role));
        var scout = plan.Members[1];
        Assert.Equal((MemberRef.PluginKind, "job-board", "0.1.0"), (scout.Kind, scout.PluginId, scout.PluginVersion));
        Assert.Equal("[\"engineer\",\"developer\"]", JsonSerializer.Serialize(scout.Settings["keywords"]));

        var plugin = Assert.Single(plan.Plugins);
        Assert.Equal(("job-board", "0.1.0", "plugins/job-board"), (plugin.Id, plugin.Version, plugin.Folder));
        Assert.Equal(["plugin.job-board.posting-found"], plugin.Events);

        // EVERY TRIGGER WITH ITS FULL INSTRUCTION, its wake setting and its cap: the person reads
        // exactly what will become a prompt.
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "solution.json")))!;
        Assert.Equal(5, plan.Triggers.Count);
        for (var index = 0; index < plan.Triggers.Count; index++)
        {
            Assert.Equal(manifest["triggers"]![index]!["instruction"]!.GetValue<string>(), plan.Triggers[index].Instruction);
        }

        var scan = plan.Triggers[0];
        Assert.Equal(("schedule", "Every", 3600, "never", (long?)null), (scan.Kind, scan.PlatformKind, scan.EverySeconds!.Value, scan.WakeManager, scan.DailyTokenCap));
        var apply = plan.Triggers[2];
        Assert.Equal(("event", "Event", "site.action", "siteAction eq tracker/apply", "Writer", (long?)400000),
            (apply.Kind, apply.PlatformKind, apply.EventType, apply.Filter, apply.Member, apply.DailyTokenCap));
        Assert.Contains("{solution}/make-cover-letter.py", apply.Instruction);
        var resume = plan.Triggers[3];
        Assert.Equal(("folder", "FolderChange", "Resume", "*"), (resume.Kind, resume.PlatformKind, resume.FolderPath, resume.FolderGlob));
        var morning = plan.Triggers[4];
        Assert.Equal(("Cron", "0 0 8 * * 1-5", "Europe/London", "Coordinator"), (morning.PlatformKind, morning.Cron, morning.Timezone, morning.Member));
        Assert.Equal(WakeManagerPolicy.OnHandbackOrFailure, plan.Triggers[2].WakeManager);

        var skill = Assert.Single(plan.Skills);
        Assert.Equal(("job-search-playbook", "skills/job-search-playbook.md"), (skill.Name, skill.File));
        Assert.Equal([SkillRoles.Manager, SkillRoles.Member], skill.Roles);
        Assert.Contains("# Job search playbook", skill.Body);

        var site = Assert.Single(plan.Sites);
        Assert.Equal(["app.js", "index.html", "site.css"], site.Files);

        Assert.Equal(("tools", "solution"), (plan.Tools!.Folder, plan.Tools.InstalledAs));
        Assert.Equal(["make-cover-letter.py"], plan.Tools.Files);

        var document = Assert.Single(plan.Inputs.Documents);
        Assert.Equal(("Resume", true), (document.Folder, document.Required));
        var setting = Assert.Single(plan.PersonSettings);
        Assert.Equal(("Scout", "sources", "list", false), (setting.Member, setting.Setting, setting.Type, setting.Required));
        Assert.Equal(["sample", "adzuna", "usajobs", "themuse"], setting.Choices!);
        Assert.Empty(plan.Ignored);

        Assert.Equal(folder, check.Package!.Folder);
    }

    [Fact]
    public void The_1_1_0_variant_passes_and_differs_from_1_0_0_as_its_overlay_says()
    {
        var before = Check(Sample()).Plan!;
        var after = Check(SolutionSamples.JobTracker("1.1.0", _scratch));

        Assert.True(after.Ok, string.Join("\n", after.Refusals));
        var plan = after.Plan!;

        Assert.Equal(("job-tracker", "1.1.0"), (plan.Package.Id, plan.Package.Version));
        Assert.Equal(["Reviewer"], plan.Members.Select(m => m.Name).Except(before.Members.Select(m => m.Name)));
        Assert.Equal(["Weekly review"], plan.Triggers.Select(t => t.Name).Except(before.Triggers.Select(t => t.Name)));
        Assert.Equal(["Resume changed"], before.Triggers.Select(t => t.Name).Except(plan.Triggers.Select(t => t.Name)));
        Assert.Equal(250000, plan.Triggers.Single(t => t.Name == "New posting").DailyTokenCap);
        Assert.Equal(["interview-prep", "job-search-playbook"], plan.Skills.Select(s => s.Name).Order());
        Assert.Equal("0.2.0", Assert.Single(plan.Plugins).Version);
    }

    [Fact]
    public void Unknown_top_level_keys_are_kept_and_ignored()
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m =>
        {
            m["controlPanel"] = new JsonObject { ["tiles"] = new JsonArray("jobs") };
            m["statusLines"] = new JsonArray();
        });

        var check = Check(folder);

        Assert.True(check.Ok, string.Join("\n", check.Refusals));
        Assert.Equal(["controlPanel", "statusLines"], check.Plan!.Ignored);
        Assert.Equal("[\"jobs\"]", JsonSerializer.Serialize(check.Package!.Manifest.Extra["controlPanel"].GetProperty("tiles")));
    }

    [Fact]
    public void The_check_writes_nothing()
    {
        var folder = Sample();
        var before = Snapshot(folder);

        Check(folder);
        SolutionSamples.Edit(folder, m => m["triggers"]![0]!["member"] = "Nobody");
        var afterEdit = Snapshot(folder);
        Check(folder);

        Assert.Equal(afterEdit, Snapshot(folder));
        Assert.Equal(before.Keys, afterEdit.Keys);
    }

    private static Dictionary<string, DateTime> Snapshot(string folder) =>
        Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(p => p, p => File.GetLastWriteTimeUtc(p));

    // ---- The five named refusals ------------------------------------------------------------

    [Fact]
    public void A_trigger_naming_a_member_the_package_does_not_have_is_refused()
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m => m["triggers"]![1]!["member"] = "Recruiter");

        var refusal = Refused(Check(folder), "solution.json", "triggers[1].member");
        Assert.Contains("'Recruiter' names no member of this package", refusal.Reason);
        Assert.Contains("Coordinator, Scout, Writer", refusal.Reason);
    }

    [Fact]
    public void A_plugin_member_naming_a_plugin_the_package_does_not_ship_is_refused()
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m => m["members"]![1]!["pluginId"] = "linkedin-scraper");

        var refusal = Refused(Check(folder), "solution.json", "members[1].pluginId");
        Assert.Contains("'linkedin-scraper' is not a plugin in this package; it ships job-board", refusal.Reason);
    }

    [Fact]
    public void A_skill_named_like_a_built_in_is_refused()
    {
        var folder = Sample();
        var builtIn = BuiltInSkills.All.First().Name;
        File.Move(Path.Combine(folder, "skills", "job-search-playbook.md"), Path.Combine(folder, "skills", builtIn + ".md"));
        SolutionSamples.Edit(folder, m => m["skills"] = new JsonArray(builtIn));

        var refusal = Refused(Check(folder), "solution.json", "skills[0]");
        Assert.Contains($"'{builtIn}' is a built-in skill's name", refusal.Reason);
    }

    [Theory]
    [InlineData("no.such.event", "GET /api/events")]
    [InlineData("plugin.job-board.posting-lost", "events.publishes")]
    [InlineData("plugin.not-shipped.found", "events.publishes")]
    public void An_event_type_the_catalog_does_not_know_is_refused(string type, string hint)
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m => m["triggers"]![1]!["event"]!["type"] = type);

        var refusal = Refused(Check(folder), "solution.json", "triggers[1].event.type");
        Assert.Contains($"'{type}' is not an event type the catalog knows", refusal.Reason);
        Assert.Contains(hint, refusal.Reason);
    }

    [Fact]
    public void A_folder_trigger_path_leaving_the_documents_is_refused()
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m => m["triggers"]![3]!["folder"]!["path"] = "../other-team/Resume");

        var refusal = Refused(Check(folder), "solution.json", "triggers[3].folder.path");
        Assert.Contains("must be a relative path that stays inside", refusal.Reason);
    }

    [Theory]
    [InlineData("/etc")]
    [InlineData("Resume/../../keys")]
    public void A_document_input_folder_leaving_the_documents_is_refused(string path)
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m => m["inputs"]!["documents"]![0]!["folder"] = path);

        Refused(Check(folder), "solution.json", "inputs.documents[0].folder");
    }

    [Fact]
    public void A_link_leaving_the_package_is_refused_naming_the_link()
    {
        var folder = Sample();
        File.WriteAllText(Path.Combine(_scratch, "outside.txt"), "not the package's");
        File.CreateSymbolicLink(Path.Combine(folder, "sites", "tracker", "secrets.txt"), "../../../outside.txt");
        File.CreateSymbolicLink(Path.Combine(folder, "tools", "passwd"), "/etc/passwd");

        var check = Check(folder);

        Assert.Contains("leading outside the package", Refused(check, "sites/tracker/secrets.txt", "(link)").Reason);
        Assert.Contains("absolute path", Refused(check, "tools/passwd", "(link)").Reason);
    }

    /// <summary>What a file outside the package holds, which no refusal may repeat.</summary>
    private const string Outside = "OUTSIDE-3f9c1e-not-the-packages";

    [Fact]
    public void A_solution_json_linked_outside_the_package_is_refused_alone_and_never_read()
    {
        var folder = Sample();
        var manifest = Path.Combine(folder, "solution.json");
        var target = Path.Combine(_scratch, "outside-solution.json");
        // A manifest that, were it read, would be refused naming the marker.
        File.WriteAllText(target, File.ReadAllText(manifest).Replace("\"member\": \"Scout\"", $"\"member\": \"{Outside}\"", StringComparison.Ordinal));
        File.Delete(manifest);
        File.CreateSymbolicLink(manifest, "../outside-solution.json");

        var check = Check(folder);

        var refusal = Assert.Single(check.Refusals);
        Assert.Equal(("solution.json", "(link)"), (refusal.File, refusal.Field));
        Assert.DoesNotContain(check.Refusals, r => r.ToString().Contains(Outside, StringComparison.Ordinal));
        Assert.False(check.Ok);
    }

    [Fact]
    public void A_skill_file_linked_outside_the_package_is_refused_alone_and_never_read()
    {
        var folder = Sample();
        var skill = Path.Combine(folder, "skills", "job-search-playbook.md");
        // A skill that, were it read, would be refused naming the marker.
        File.WriteAllText(Path.Combine(_scratch, "outside-skill.md"), $"---\nname: {Outside}\ndescription: {Outside}\nroles: {Outside}\n---\n\n{Outside}\n");
        File.Delete(skill);
        File.CreateSymbolicLink(skill, "../../outside-skill.md");

        var check = Check(folder);

        var refusal = Assert.Single(check.Refusals);
        Assert.Equal(("skills/job-search-playbook.md", "(link)"), (refusal.File, refusal.Field));
        Assert.DoesNotContain(check.Refusals, r => r.ToString().Contains(Outside, StringComparison.Ordinal));
        Assert.False(check.Ok);
    }

    [Fact]
    public void A_link_that_stays_inside_the_package_is_allowed()
    {
        var folder = Sample();
        File.CreateSymbolicLink(Path.Combine(folder, "tools", "letter.py"), "make-cover-letter.py");

        Assert.True(Check(folder).Ok);
    }

    [Fact]
    public void A_plugin_executable_path_leaving_its_folder_is_refused()
    {
        var folder = Sample();
        SolutionSamples.EditJson(Path.Combine(folder, "plugins", "job-board", "plugin.json"),
            p => p["executable"]!["path"] = "../../tools/make-cover-letter.py");

        var refusal = Refused(Check(folder), "plugins/job-board/plugin.json", "executable.path");
        Assert.Contains("relative path inside the plugin's directory", refusal.Reason);
    }

    [Theory]
    [InlineData("skills", "../tools/make-cover-letter")]
    [InlineData("sites", "../tools")]
    public void A_skill_or_site_named_by_a_path_is_refused(string key, string name)
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m => m[key] = new JsonArray(name));

        Assert.Contains("is a path", Refused(Check(folder), "solution.json", $"{key}[0]").Reason);
    }

    // ---- Secrets: key names, never values ------------------------------------------------------

    [Fact]
    public void A_valid_secrets_block_passes_and_the_plan_names_each_key_and_what_it_is_for()
    {
        var check = Check(Sample());

        Assert.True(check.Ok, string.Join("\n", check.Refusals));
        Assert.Equal(
            [
                ("Scout", "adzunaAppId", "ADZUNA_APP_ID", "adzuna"),
                ("Scout", "adzunaAppKey", "ADZUNA_APP_KEY", "adzuna"),
                ("Scout", "usajobsApiKey", "USAJOBS_API_KEY", "usajobs"),
                ("Scout", "usajobsUserAgent", "USAJOBS_USER_AGENT", "usajobs"),
                ("Scout", "themuseApiKey", "THEMUSE_API_KEY", "themuse"),
            ],
            check.Plan!.Secrets.Select(s => (s.Member, s.Field, s.Key, s.When!.Value)));
        Assert.All(check.Plan.Secrets, s => Assert.Equal("sources", s.When!.Setting));
        Assert.Contains("developer.adzuna.com", check.Plan.Secrets[0].Description);
    }

    [Fact]
    public void A_secret_field_the_plugin_manifest_does_not_declare_is_refused_naming_file_and_field()
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m => m["members"]![1]!["secrets"]!["linkedinToken"] = "LINKEDIN_TOKEN");

        var refusal = Refused(Check(folder), "solution.json", "members[1].secrets.linkedinToken");
        Assert.Contains("declares no secret 'linkedinToken'", refusal.Reason);
        Assert.Contains("adzunaAppId", refusal.Reason);
    }

    [Theory]
    [InlineData("adzuna_app_id")]
    [InlineData("1ADZUNA")]
    [InlineData("ADZUNA APP ID")]
    [InlineData("HARNESS_KEY")]
    [InlineData("ANTHROPIC_API_KEY")]
    public void A_secret_key_that_is_not_a_legal_environment_variable_name_is_refused_naming_file_and_field(string key)
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m => m["members"]![1]!["secrets"]!["adzunaAppId"] = key);

        var refusal = Refused(Check(folder), "solution.json", "members[1].secrets.adzunaAppId");
        Assert.Matches("not a legal environment variable name|keeps for itself", refusal.Reason);
        Assert.DoesNotContain(key, refusal.Reason);
    }

    public static TheoryData<string> ValueLooking => new()
    {
        "sk-ant-api03-Zx9Qw7Lm2Np4Rt6Vy8Bc0Df",
        "AKIAIOSFODNN7EXAMPLE",
        "ghp_16C7e42F292c6912E7710c838347Ae178B4a",
        "eyJhbGciOiJIUzI1NiJ9.e30.ZRrHA1JJJW8opsbCGfG_HACGpVUMN_a9IV7pAx_Zmeo",
        "{\"value\": \"hunter2hunter2\"}",
    };

    [Theory]
    [MemberData(nameof(ValueLooking))]
    public void A_value_looking_secret_entry_is_refused_naming_file_and_field_and_never_repeated(string entry)
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m => m["members"]![1]!["secrets"]!["adzunaAppKey"] =
            entry.StartsWith('{') ? JsonNode.Parse(entry) : JsonValue.Create(entry));

        var check = Check(folder);
        var refusal = Refused(check, "solution.json", "members[1].secrets.adzunaAppKey");
        Assert.Contains("looks like a secret's value", refusal.Reason);

        // WHAT WAS WRITTEN THERE MAY BE THE CREDENTIAL: no refusal, and nothing the route answers, repeats it.
        var body = JsonSerializer.Serialize(SolutionEndpoints.Body(check), JsonSerializerOptions.Web);
        Assert.DoesNotContain(entry.StartsWith('{') ? "hunter2hunter2" : entry, body);
    }

    [Fact]
    public void Every_secrets_refusal_is_named_at_once()
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m =>
        {
            var secrets = m["members"]![1]!["secrets"]!;
            secrets["linkedinToken"] = "LINKEDIN_TOKEN";
            secrets["adzunaAppId"] = "adzuna-app-id";
            secrets["adzunaAppKey"] = "AKIAIOSFODNN7EXAMPLE";
        });

        var fields = Check(folder).Refusals.Where(r => r.File == "solution.json").Select(r => r.Field).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(["members[1].secrets.adzunaAppId", "members[1].secrets.adzunaAppKey", "members[1].secrets.linkedinToken"], fields);
    }

    [Theory]
    [InlineData("keywordz", "adzuna", "secrets.adzunaAppId.when", "not a `config` field")]
    [InlineData("sources", "linkedin", "secrets.adzunaAppId.when.sources", "must be one of: sample, adzuna, usajobs, themuse")]
    public void A_manifest_secret_needed_when_an_unknown_setting_or_value_is_refused(string setting, string value, string field, string says)
    {
        var folder = Sample();
        SolutionSamples.EditJson(Path.Combine(folder, "plugins", "job-board", "plugin.json"),
            p => p["secrets"]!["adzunaAppId"]!["when"] = new JsonObject { [setting] = value });

        Assert.Contains(says, Refused(Check(folder), "plugins/job-board/plugin.json", field).Reason);
    }

    [Fact]
    public void An_agent_member_naming_secrets_is_refused()
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m => m["members"]![2]!["secrets"] = new JsonObject { ["x"] = "X_KEY" });

        Assert.Contains("belongs to a plugin member", Refused(Check(folder), "solution.json", "members[2].secrets").Reason);
    }

    // ---- Malformed or missing, one row per case ---------------------------------------------

    /// <summary>One way to break a copy of the sample, and the file and field its refusal names.</summary>
    public sealed record Breakage(string Case, Action<string> Break, string File, string Field, string Says)
    {
        public override string ToString() => Case;
    }

    private static Action<string> Json(Action<JsonObject> change) => folder => SolutionSamples.Edit(folder, change);

    private static Action<string> PluginJson(Action<JsonObject> change) =>
        folder => SolutionSamples.EditJson(Path.Combine(folder, "plugins", "job-board", "plugin.json"), change);

    private static Action<string> Skill(Func<string, string> change) => folder =>
    {
        var path = Path.Combine(folder, "skills", "job-search-playbook.md");
        File.WriteAllText(path, change(File.ReadAllText(path)));
    };

    private static readonly Breakage[] All =
    [
        new("solution.json missing", f => File.Delete(Path.Combine(f, "solution.json")), "solution.json", "(file)", "holds no solution.json"),
        new("not JSON", f => File.WriteAllText(Path.Combine(f, "solution.json"), "{ nope"), "solution.json", "(file)", "is not JSON"),
        new("not an object", f => File.WriteAllText(Path.Combine(f, "solution.json"), "[]"), "solution.json", "(file)", "must be a JSON object"),
        new("format missing", Json(m => m.Remove("format")), "solution.json", "format", "is required"),
        new("format from the future", Json(m => m["format"] = 2), "solution.json", "format", "`format` 2 is not one this Host reads"),
        new("id missing", Json(m => m.Remove("id")), "solution.json", "id", "`id` is required"),
        new("id not a slug", Json(m => m["id"] = "Job Tracker"), "solution.json", "id", "must be a slug"),
        new("name missing", Json(m => m.Remove("name")), "solution.json", "name", "`name` is required"),
        new("version missing", Json(m => m.Remove("version")), "solution.json", "version", "`version` is required"),
        new("version not a version", Json(m => m["version"] = "latest"), "solution.json", "version", "plain version"),
        new("description empty", Json(m => m["description"] = " "), "solution.json", "description", "non-empty string"),
        new("team not an object", Json(m => m["team"] = "Jobs"), "solution.json", "team", "must be an object"),
        new("team name with no letters", Json(m => m["team"]!["name"] = "!!!"), "solution.json", "team.name", "no letters or digits"),
        new("members not an array", Json(m => m["members"] = new JsonObject()), "solution.json", "members", "must be an array"),
        new("member name missing", Json(m => m["members"]![2]!.AsObject().Remove("name")), "solution.json", "members[2].name", "is required"),
        new("two members of one name", Json(m => m["members"]![2]!["name"] = "scout"), "solution.json", "members[2].name", "same member name as 'Scout'"),
        new("second manager", Json(m => m["members"]![2]!["role"] = "manager"), "solution.json", "members[2].role", "second manager"),
        new("role unknown", Json(m => m["members"]![2]!["role"] = "boss"), "solution.json", "members[2].role", "must be manager or member"),
        new("kind unknown", Json(m => m["members"]![2]!["kind"] = "robot"), "solution.json", "members[2].kind", "must be agent or plugin"),
        new("plugin member with a preset", Json(m => m["members"]![1]!["preset"] = "model"), "solution.json", "members[1].preset", "runs no model"),
        new("agent member with settings", Json(m => m["members"]![2]!["settings"] = new JsonObject()), "solution.json", "members[2].settings", "belongs to a plugin member"),
        new("preset unknown", Json(m => m["members"]![2]!["preset"] = "no-such-agent"), "solution.json", "members[2].preset", "not a headless Agent preset"),
        new("plugin setting unknown", Json(m => m["members"]![1]!["settings"]!["salary"] = 1), "solution.json", "members[1].settings.salary", "has no setting 'salary'"),
        new("plugin setting of the wrong type", Json(m => m["members"]![1]!["settings"]!["keywords"] = "engineer"), "solution.json", "members[1].settings.keywords", "must be a list of strings"),
        new("person-only setting set by the package", Json(m => m["members"]![1]!["settings"]!["sources"] = new JsonArray("sample")), "solution.json", "members[1].settings.sources", "set by a person only"),
        new("triggers not an array", Json(m => m["triggers"] = "hourly"), "solution.json", "triggers", "must be an array"),
        new("trigger name missing", Json(m => m["triggers"]![0]!.AsObject().Remove("name")), "solution.json", "triggers[0].name", "is required"),
        new("two triggers of one name", Json(m => m["triggers"]![1]!["name"] = "scan for postings"), "solution.json", "triggers[1].name", "used by another trigger"),
        new("trigger instruction missing", Json(m => m["triggers"]![2]!.AsObject().Remove("instruction")), "solution.json", "triggers[2].instruction", "is required"),
        new("trigger member missing", Json(m => m["triggers"]![2]!.AsObject().Remove("member")), "solution.json", "triggers[2].member", "is required"),
        new("trigger kind unknown", Json(m => m["triggers"]![0]!["kind"] = "cron"), "solution.json", "triggers[0].kind", "must be schedule, event or folder"),
        new("schedule missing", Json(m => m["triggers"]![0]!.AsObject().Remove("schedule")), "solution.json", "triggers[0].schedule", "is required for a schedule trigger"),
        new("schedule with both clocks", Json(m => m["triggers"]![0]!["schedule"]!["cron"] = "0 0 * * * *"), "solution.json", "triggers[0].schedule", "exactly one of"),
        new("every below the floor", Json(m => m["triggers"]![0]!["schedule"]!["everySeconds"] = 5), "solution.json", "triggers[0].schedule.everySeconds", "at least 10"),
        new("cron that does not parse", Json(m => m["triggers"]![4]!["schedule"]!["cron"] = "every morning"), "solution.json", "triggers[4].schedule.cron", "invalid"),
        new("timezone unknown", Json(m => m["triggers"]![4]!["schedule"]!["timezone"] = "Mars/Olympus"), "solution.json", "triggers[4].schedule.timezone", "Mars/Olympus"),
        new("event missing", Json(m => m["triggers"]![1]!.AsObject().Remove("event")), "solution.json", "triggers[1].event", "is required for an event trigger"),
        new("event type missing", Json(m => m["triggers"]![1]!["event"]!.AsObject().Remove("type")), "solution.json", "triggers[1].event.type", "is required"),
        new("filter that does not parse", Json(m => m["triggers"]![2]!["event"]!["filter"] = "tracker/apply"), "solution.json", "triggers[2].event.filter", "`field op value`"),
        new("filter on a field the event lacks", Json(m => m["triggers"]![2]!["event"]!["filter"] = "button eq apply"), "solution.json", "triggers[2].event.filter", "carries no field named \"button\""),
        new("filter on a field the package's plugin event lacks", Json(m => m["triggers"]![1]!["event"]!["filter"] = "salary eq 1"), "solution.json", "triggers[1].event.filter", "It carries: source, id, title, company, url"),
        new("high-volume event on a model", Json(m => m["triggers"]![1]!["event"]!["type"] = MessageTypes.Progress), "solution.json", "triggers[1].event.type", MessageTypes.Progress),
        new("folder missing", Json(m => m["triggers"]![3]!.AsObject().Remove("folder")), "solution.json", "triggers[3].folder", "is required for a folder trigger"),
        new("wakeManager unknown", Json(m => m["triggers"]![2]!["wakeManager"] = "sometimes"), "solution.json", "triggers[2].wakeManager", "must be one of: always, onHandbackOrFailure, never"),
        new("daily cap of zero", Json(m => m["triggers"]![2]!["dailyTokenCap"] = 0), "solution.json", "triggers[2].dailyTokenCap", "at least 1"),
        new("idleOnly not a flag", Json(m => m["triggers"]![0]!["idleOnly"] = "yes"), "solution.json", "triggers[0].idleOnly", "true or false"),
        new("{solution} without tools", f => Directory.Delete(Path.Combine(f, "tools"), recursive: true), "solution.json", "triggers[2].instruction", "no tools/ folder"),
        new("{solution} in instructions without tools", f => { Directory.Delete(Path.Combine(f, "tools"), recursive: true); SolutionSamples.Edit(f, m => m["triggers"]![2]!["instruction"] = "draft"); }, "solution.json", "members[2].instructions", "no tools/ folder"),
        new("skills not an array", Json(m => m["skills"] = "job-search-playbook"), "solution.json", "skills", "must be an array"),
        new("skill named twice", Json(m => m["skills"] = new JsonArray("job-search-playbook", "job-search-playbook")), "solution.json", "skills[1]", "twice"),
        new("skill name not legal", Json(m => m["skills"] = new JsonArray("Job_Search")), "solution.json", "skills[0]", "not a legal skill name"),
        new("skill name in the plugins' namespace", Json(m => m["skills"] = new JsonArray("plugin-job-board")), "solution.json", "skills[0]", "reserved"),
        new("skill file missing", f => File.Delete(Path.Combine(f, "skills", "job-search-playbook.md")), "solution.json", "skills[0]", "has no file skills/job-search-playbook.md"),
        new("skill without front matter", Skill(_ => "# Just a body"), "skills/job-search-playbook.md", "(front matter)", "frontmatter"),
        new("skill front matter naming another", Skill(t => t.Replace("name: job-search-playbook", "name: job-hunt")), "skills/job-search-playbook.md", "name", "`name: job-hunt`"),
        new("skill without a description", Skill(t => t.Replace("description: Use when", "summary: Use when")), "skills/job-search-playbook.md", "description", "description"),
        new("skill without roles", Skill(t => t.Replace("roles: manager member\n", "")), "skills/job-search-playbook.md", "roles", "must say who it is for"),
        new("skill with an unknown role", Skill(t => t.Replace("roles: manager member", "roles: manager recruiter")), "skills/job-search-playbook.md", "roles", "recruiter"),
        new("skill without a body", Skill(t => t[..(t.IndexOf("# Job search", StringComparison.Ordinal))]), "skills/job-search-playbook.md", "(body)", "needs a body"),
        new("site not a slug", Json(m => m["sites"] = new JsonArray("Tracker")), "solution.json", "sites[0]", "not a valid site name"),
        new("site folder missing", f => Directory.Delete(Path.Combine(f, "sites", "tracker"), recursive: true), "solution.json", "sites[0]", "has no folder sites/tracker/"),
        new("site without index.html", f => File.Delete(Path.Combine(f, "sites", "tracker", "index.html")), "sites/tracker/index.html", "(file)", "has no index.html"),
        new("plugin folder without a manifest", f => File.Delete(Path.Combine(f, "plugins", "job-board", "plugin.json")), "plugins/job-board/plugin.json", "(file)", "holds no plugin.json"),
        new("plugin folder not named after its id", PluginJson(p => p["id"] = "job-boards"), "plugins/job-board/plugin.json", "id", "its folder is plugins/job-board/"),
        new("plugin manifest missing a field", PluginJson(p => p.Remove("protocol")), "plugins/job-board/plugin.json", "protocol", "`protocol` is required"),
        new("plugin executable missing", f => File.Delete(Path.Combine(f, "plugins", "job-board", "job-board")), "plugins/job-board/plugin.json", "executable", "does not exist"),
        new("inputs not an object", Json(m => m["inputs"] = new JsonArray()), "solution.json", "inputs", "must be an object"),
        new("input setting of an agent", Json(m => m["inputs"]!["settings"]![0]!["member"] = "Writer"), "solution.json", "inputs.settings[0].member", "is an agent member"),
        new("input setting of nobody", Json(m => m["inputs"]!["settings"]![0]!["member"] = "Nobody"), "solution.json", "inputs.settings[0].member", "names no member"),
        new("input setting the plugin lacks", Json(m => m["inputs"]!["settings"]![0]!["setting"] = "salary"), "solution.json", "inputs.settings[0].setting", "has no setting 'salary'"),
        new("input setting that is not a person's", Json(m => m["inputs"]!["settings"]![0]!["setting"] = "keywords"), "solution.json", "inputs.settings[0].setting", "not a person-only setting"),
        new("input connection slot the plugin lacks", Json(m => m["inputs"]!["connections"] = new JsonArray(new JsonObject { ["member"] = "Scout", ["slot"] = "mail", ["description"] = "x" })), "solution.json", "inputs.connections[0].slot", "has no connection slot 'mail'"),
        new("required connection slot not asked for", PluginJson(p => p["connections"] = new JsonObject { ["board"] = new JsonObject { ["providers"] = new JsonArray("google"), ["required"] = true } }), "solution.json", "inputs.connections", "slot 'board'"),
        new("document input without a description", Json(m => m["inputs"]!["documents"]![0]!.AsObject().Remove("description")), "solution.json", "inputs.documents[0].description", "is required"),
        new("document input required not a flag", Json(m => m["inputs"]!["documents"]![0]!["required"] = "yes"), "solution.json", "inputs.documents[0].required", "true or false"),
    ];

    /// <summary>The cases by name: a string row is its own test case in discovery and in the report.</summary>
    public static TheoryData<string> Breakages() => [.. All.Select(b => b.Case)];

    [Theory]
    [MemberData(nameof(Breakages))]
    public void A_malformed_or_missing_field_is_refused_naming_its_file_and_field(string @case)
    {
        var breakage = All.Single(b => b.Case == @case);
        var folder = Sample();
        breakage.Break(folder);

        var refusal = Refused(Check(folder), breakage.File, breakage.Field);
        Assert.Contains(breakage.Says, refusal.Reason);
    }

    [Fact]
    public void A_folder_that_is_not_there_is_refused()
    {
        var check = Check(Path.Combine(_scratch, "nowhere"));

        var refusal = Assert.Single(check.Refusals);
        Assert.Equal((".", "(folder)"), (refusal.File, refusal.Field));
        Assert.False(check.Ok);
    }

    [Fact]
    public void Every_refusal_in_a_badly_broken_package_is_named_at_once()
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m =>
        {
            m["triggers"]![0]!["member"] = "Nobody";
            m["triggers"]![1]!["wakeManager"] = "sometimes";
            m["members"]![2]!["role"] = "boss";
        });

        var fields = Check(folder).Refusals.Select(r => r.Field).ToList();

        Assert.Contains("triggers[0].member", fields);
        Assert.Contains("triggers[1].wakeManager", fields);
        Assert.Contains("members[2].role", fields);
    }

    [Fact]
    public void A_refusal_in_solution_json_does_not_hide_the_refusals_found_against_the_catalog_and_the_files()
    {
        var folder = Sample();
        SolutionSamples.Edit(folder, m =>
        {
            m["triggers"]![1]!["member"] = "Nobody";
            m["triggers"]![2]!["event"]!["type"] = "no.such.event";
            m["members"]![1]!["pluginId"] = "linkedin-scraper";
        });

        var check = Check(folder);

        Assert.Contains("'Nobody' names no member of this package", Refused(check, "solution.json", "triggers[1].member").Reason);
        // The refused trigger is left out, and the next one is still named by its own index.
        Assert.Contains("'no.such.event' is not an event type the catalog knows", Refused(check, "solution.json", "triggers[2].event.type").Reason);
        Assert.Contains("'linkedin-scraper' is not a plugin in this package", Refused(check, "solution.json", "members[1].pluginId").Reason);
        Assert.Equal(3, check.Refusals.Count);
    }

    [Fact]
    public void A_refusal_reads_as_its_file_its_field_and_its_sentence()
    {
        Assert.Equal("solution.json triggers[1].member: why", new SolutionRefusal("solution.json", "triggers[1].member", "why").ToString());
    }

    [Fact]
    public void The_plan_serialises_as_the_route_answers_it()
    {
        var body = JsonSerializer.SerializeToElement(SolutionEndpoints.Body(Check(Sample())), JsonSerializerOptions.Web);

        Assert.True(body.GetProperty("ok").GetBoolean());
        Assert.Equal(0, body.GetProperty("refusals").GetArrayLength());
        var trigger = body.GetProperty("plan").GetProperty("triggers")[2];
        Assert.Equal(400000, trigger.GetProperty("dailyTokenCap").GetInt64());
        Assert.StartsWith("{event.by} pressed Apply", trigger.GetProperty("instruction").GetString());
        Assert.Equal("[\"engineer\",\"developer\"]", body.GetProperty("plan").GetProperty("members")[1].GetProperty("settings").GetProperty("keywords").GetRawText().Replace(" ", ""));
    }
}

/// <summary>
/// THE CLI READS WHAT THE HOST PRINTS: its tests answer `--solution-check` with
/// <c>cli/internal/cli/testdata/solution-check-*.json</c>, and this pins those files to what the Host
/// prints for the same packages today, so the CLI is never tested against a shape the Host does not
/// send. Re-record with <c>HARNESS_UPDATE_GOLDENS=1</c>, only for a change meant to be visible.
/// </summary>
public sealed class SolutionCheckCliFixtureTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Fixtures() => ["solution-check-job-tracker.json", "solution-check-refused.json"];

    [Theory]
    [MemberData(nameof(Fixtures))]
    public async Task The_cli_fixture_is_what_the_host_prints(string fixture)
    {
        var scratch = Path.Combine(Path.GetTempPath(), $"solution-fixture-{Guid.NewGuid():N}");
        var folder = SolutionSamples.JobTracker("1.0.0", scratch);

        if (fixture == "solution-check-refused.json")
        {
            SolutionSamples.Edit(folder, m =>
            {
                m["triggers"]![1]!["member"] = "Recruiter";
                m["triggers"]![2]!["dailyTokenCap"] = 0;
            });
        }

        try
        {
            var output = new StringWriter();
            Assert.Equal(OperatorOutcome.Completed, await OperatorCommands.TryRunAsync(["--solution-check", folder], Path.Combine(scratch, "data"), output, Ct));
            var printed = output.ToString().TrimEnd().Split('\n')[^1].Replace(folder, "<folder>", StringComparison.Ordinal);
            var path = Path.Combine(SolutionSamples.RepoRoot(), "cli", "internal", "cli", "testdata", fixture);

            if (Environment.GetEnvironmentVariable("HARNESS_UPDATE_GOLDENS") == "1") File.WriteAllText(path, printed + "\n");

            Assert.Equal(File.ReadAllText(path).TrimEnd(), printed);
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); }
            catch (IOException) { }
        }
    }
}
