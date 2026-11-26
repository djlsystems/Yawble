using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit;
using Yawble.Core.Email;
using Yawble.Core.Integrations;
using Yawble.Core.Workflows.Actions;
using Yawble.Core.Workflows.Language;
using Yawble.Core.Workflows.Library;
using Yawble.Core.Workflows.Running;
using Yawble.Core.Skills;
using Yawble.Core.Tests.Bus;
using Yawble.Core.Tests.Gateway;
using IntegrationCaller = Yawble.Core.Integrations.WorkflowCaller;

namespace Yawble.Core.Tests.Workflows;

/// <summary>
/// The Inbox SMS alert as the Workflows place offers it (the shell's template) and as the Concierge's workflows skill shows it: both name the
/// integrations by their real names, and both check clean with the real checker and the real email and api catalogs, through the library
/// too (their requires is already what the steps give: no warning).
/// </summary>
public sealed class InboxSmsAlertTemplateTests
{
    /// <summary>The shell template's object literal (INBOX_SMS_ALERT in workflows/api.ts) as JSON.</summary>
    private static string Template()
    {
        var source = File.ReadAllText(Path.Combine(Binaries.RepoRoot, "shell", "src", "renderer", "src", "workflows", "api.ts"));
        var start = source.IndexOf("export const INBOX_SMS_ALERT", StringComparison.Ordinal);
        Assert.True(start >= 0, "the shell has the Inbox SMS alert template");
        start = source.IndexOf('{', start);
        return LiteralToJson(source, start);
    }

    private static string SkillExample()
    {
        var body = Assert.Single(SkillCatalog.BuiltIns, s => s.Name == "workflows").Body;
        var m = Regex.Match(body, @"## Example[^\n]*\n+```json\n(?<json>.*?)\n```", RegexOptions.Singleline);
        Assert.True(m.Success);
        return m.Groups["json"].Value;
    }

    /// <summary>A TypeScript object literal (unquoted keys, 'single quoted' text, trailing commas) from <paramref name="start"/> as JSON.</summary>
    private static string LiteralToJson(string s, int start)
    {
        var sb = new StringBuilder();
        var depth = 0;
        for (var i = start; i < s.Length; i++)
        {
            var c = s[i];
            if (c is '\'' or '"')
            {
                var text = new StringBuilder();
                for (i++; s[i] != c; i++) text.Append(s[i] == '\\' ? s[++i] : s[i]);
                sb.Append(JsonValue.Create(text.ToString()).ToJsonString());
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                var j = i;
                while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                var word = s[i..j];
                var k = j;
                while (char.IsWhiteSpace(s[k])) k++;
                sb.Append(s[k] == ':' ? JsonValue.Create(word).ToJsonString() : word);
                i = j - 1;
                continue;
            }
            if (c is '{' or '[') depth++;
            if (c is '}' or ']')
            {
                // a trailing comma before the close
                var t = sb.ToString().TrimEnd();
                if (t.EndsWith(',')) { sb.Clear().Append(t[..^1]); }
                depth--;
            }
            sb.Append(c);
            if (depth == 0) break;
        }
        return sb.ToString();
    }

    private static WorkflowActionKinds Kinds() => new(new Dictionary<string, IWorkflowActionsProvider>
    {
        [EmailActions.Kind] = new EmailWorkflowActionsProvider(new NoMail()),
        [ApiWorkflowActionsProvider.Kind] = new ApiWorkflowActionsProvider(new SmsApi()),
    });

    public static TheoryData<string> Documents() => new() { "template", "skill" };

    private static string Document(string which) => which == "template" ? Template() : SkillExample();

    [Theory]
    [MemberData(nameof(Documents))]
    public void ItNamesTheIntegrationsByTheirRealNames_AndChecksCleanWithTheRealCheckerAndCatalogs(string which)
    {
        var json = Document(which);
        var doc = WorkflowDocument.Parse(json);
        Assert.Equal("inbox-sms-alert", doc.Id);
        var uses = doc.Steps.Where(s => s.Type == "integration").Select(s => s.Text("use")).ToList();
        Assert.Equal(["Support inbox", "Twilio"], uses);
        Assert.Equal(["Support inbox", "Twilio"], doc.Requires.Select(r => r.Name));

        var check = WorkflowChecker.Check(doc, BuiltInSchemas.Lookup, Kinds().Catalog);
        Assert.True(check.Ok, string.Join("\n", check.Issues.Select(i => $"{i.StepId}: {i.Code}: {i.Message}")));
    }

    [Theory]
    [MemberData(nameof(Documents))]
    public void ThroughTheLibrary_ItsRequiresIsWhatTheStepsGive_WithOrWithoutTheIntegrations(string which)
    {
        using var db = new TempDb();
        var kinds = Kinds();
        var withThem = new WorkflowIntegrationSet([
            new WorkflowIntegrationInfo("Support inbox", "email", true, []), new WorkflowIntegrationInfo("Twilio", "api", true, [])]);
        foreach (var directory in new IWorkflowIntegrationDirectory[] { withThem, NoWorkflowIntegrations.Instance })
        {
            var library = new WorkflowLibrary(db.ConnectionString, () => kinds.Catalog, integrations: directory);
            var check = library.Check(Document(which));
            Assert.True(check["ok"]!.GetValue<bool>(), check.ToJsonString());
            Assert.Empty(check["warnings"]!.AsArray());
        }
    }

    private sealed class NoMail : IWorkflowEmail
    {
        public Task<T> UseAsync<T>(IntegrationCaller caller, string integration, string action, JsonObject args, Func<EmailMailbox, Task<T>> work, CancellationToken ct) =>
            throw new NotSupportedException("the check calls no mailbox");
    }

    private sealed class SmsApi : IWorkflowApi
    {
        public Task<WorkflowApiResult> CallAsync(IntegrationCaller caller, string integration, string action, JsonObject args, CancellationToken ct) =>
            throw new NotSupportedException("the check calls no API");

        public IReadOnlyList<string> Teams() => ["Alpha"];

        public IReadOnlyList<WorkflowOperation> Operations(string team) =>
        [
            new("CreateMessage", "POST", "/2010-04-01/Accounts/{AccountSid}/Messages.json", "Send a message", "allow",
                [new("To", "body", true, "string"), new("From", "body", true, "string"), new("Body", "body", true, "string")],
                "application/x-www-form-urlencoded"),
        ];
    }
}
