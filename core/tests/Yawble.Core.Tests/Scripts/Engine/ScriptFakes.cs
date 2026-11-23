using System.Text.Json.Nodes;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Tests.Scripts;

/// <summary>An action catalog over fixed (kind, action) pairs; the changing ones are listed separately.</summary>
internal sealed class FakeCatalog : IScriptActionCatalog
{
    private readonly HashSet<(string, string)> _actions = [];
    private readonly HashSet<(string, string)> _changing = [];

    public FakeCatalog Read(string kind, string action) { _actions.Add((kind, action)); return this; }
    public FakeCatalog Change(string kind, string action) { _actions.Add((kind, action)); _changing.Add((kind, action)); return this; }

    public bool HasAction(string kind, string action) => _actions.Contains((kind, action));
    public bool IsChanging(string kind, string action) => _changing.Contains((kind, action));

    public const string SmsAction = "POST /2010-04-01/Accounts/{AccountSid}/Messages.json";

    /// <summary>The design example's catalog: mail list (a read), the SMS send (a change).</summary>
    public static FakeCatalog MailAndSms() => new FakeCatalog().Read("email", "list").Read("email", "get").Change("email", "send").Change("api", SmsAction);
}

/// <summary>Fake integration actions: each (use, action) answers with a function of the args; every call is recorded.</summary>
internal sealed class FakeActions : IScriptActions
{
    private readonly Dictionary<(string, string), Func<JsonObject, JsonNode?>> _handlers = [];
    public List<ScriptActionCall> Calls { get; } = [];

    public FakeActions On(string use, string action, Func<JsonObject, JsonNode?> handler) { _handlers[(use, action)] = handler; return this; }

    public Func<ScriptActionCall, CancellationToken, Task>? Before { get; set; }

    public async Task<JsonNode?> InvokeAsync(ScriptActionCall call, CancellationToken ct)
    {
        Calls.Add(call);
        if (Before is not null) await Before(call, ct);
        if (!_handlers.TryGetValue((call.Use, call.Action), out var handler)) throw new InvalidOperationException($"no fake for {call.Use} {call.Action}");
        return handler(call.Args)?.DeepClone();
    }
}

/// <summary>A journal that keeps what it is told; optionally cancels the run after a number of completed steps.</summary>
internal sealed class RecordingJournal : IScriptRunJournal
{
    public List<(string StepId, string Type)> Started { get; } = [];
    public List<(ScriptStepRecord Record, string Checkpoint)> Completed { get; } = [];
    public List<string> Lines { get; } = [];

    public int? CancelAfterCompleted { get; init; }
    public CancellationTokenSource? Cancel { get; init; }

    public Task StepStartedAsync(string stepId, string type, JsonNode? input)
    {
        Started.Add((stepId, type));
        return Task.CompletedTask;
    }

    public Task StepCompletedAsync(ScriptStepRecord rec, ScriptRunCheckpoint cp)
    {
        // Through JSON: what a store would keep.
        Completed.Add((rec, cp.ToJson()));
        if (CancelAfterCompleted is { } n && Completed.Count >= n) Cancel?.Cancel();
        return Task.CompletedTask;
    }

    public Task LogAsync(string line)
    {
        Lines.Add(line);
        return Task.CompletedTask;
    }

    public ScriptRunCheckpoint LastCheckpoint => ScriptRunCheckpoint.FromJson(Completed[^1].Checkpoint);
    public IEnumerable<string> CompletedSteps => Completed.Where(c => c.Record.Status == "succeeded").Select(c => c.Record.StepId);
}

/// <summary>A clock the test sets and moves.</summary>
internal sealed class FakeClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A temp folder for a test's data folder, removed afterwards.</summary>
internal sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "yawble-script-" + Guid.NewGuid().ToString("N"));
    public TempFolder() => Directory.CreateDirectory(Path);
    public string Sub(string name) { var p = System.IO.Path.Combine(Path, name); Directory.CreateDirectory(p); return p; }
    public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

internal static class ScriptSamples
{
    /// <summary>The design's example script (inbox-sms-alert), exactly as written there.</summary>
    public const string InboxSmsAlert = """
        {
          "format": "yawble-script/1",
          "id": "inbox-sms-alert",
          "name": "Inbox SMS alert",
          "description": "Texts the subject of every new email to a list of phones.",
          "trigger": { "accepts": ["every", "cron", "once"] },
          "settings": [
            { "name": "recipients", "type": "list<string>", "label": "Phones to text (E.164, e.g. +15551234567)" },
            { "name": "fromNumber", "type": "string", "label": "Twilio number that sends" },
            { "name": "folder", "type": "string", "label": "Mail folder", "default": "INBOX" }
          ],
          "requires": [
            { "name": "mail", "kind": "email", "actions": ["list"] },
            { "name": "sms", "kind": "api", "actions": ["POST /2010-04-01/Accounts/{AccountSid}/Messages.json"] }
          ],
          "variables": [
            { "name": "lastSeen", "type": "datetime", "scope": "saved" },
            { "name": "messages", "type": "list<EmailMessage>", "scope": "run" },
            { "name": "sent", "type": "int", "scope": "run", "initial": 0 }
          ],
          "start": "list",
          "steps": [
            { "id": "list", "type": "integration", "use": "mail", "action": "list",
              "args": { "folder": "{settings.folder}", "since": "{vars.lastSeen}" }, "into": "messages", "next": "any" },
            { "id": "any", "type": "if", "when": { "left": "{count(vars.messages)}", "op": "gt", "right": 0 },
              "then": "each", "else": "done" },
            { "id": "each", "type": "forEach", "items": "{vars.messages}", "as": "m", "body": "phones", "then": "mark" },
            { "id": "phones", "type": "forEach", "items": "{settings.recipients}", "as": "to", "body": "text", "then": null },
            { "id": "text", "type": "integration", "use": "sms",
              "action": "POST /2010-04-01/Accounts/{AccountSid}/Messages.json",
              "args": { "To": "{to}", "From": "{settings.fromNumber}", "Body": "New email: {m.subject} ({m.from.address})" },
              "next": "count" },
            { "id": "count", "type": "set", "var": "sent", "value": "{vars.sent + 1}", "next": null },
            { "id": "mark", "type": "set", "var": "lastSeen", "value": "{max(vars.messages, 'receivedAt')}", "next": "log" },
            { "id": "log", "type": "file.appendLine", "path": "run.log",
              "text": "{now()} {count(vars.messages)} new, {vars.sent} texts", "next": "done" },
            { "id": "done", "type": "end" }
          ]
        }
        """;

    public static JsonObject Email(string id, string subject, string from, string receivedAt) => new()
    {
        ["id"] = id,
        ["threadId"] = "t-" + id,
        ["folder"] = "INBOX",
        ["from"] = new JsonObject { ["name"] = null, ["address"] = from },
        ["to"] = new JsonArray(new JsonObject { ["address"] = "service@example.com" }),
        ["subject"] = subject,
        ["receivedAt"] = receivedAt,
        ["snippet"] = "...",
        ["hasAttachments"] = false,
        ["unread"] = true,
    };

    /// <summary>A small script around the given steps (JSON array text), with the given variables and settings.</summary>
    public static ScriptDocument Script(string steps, string variables = "[]", string settings = "[]", string requires = "[]",
        string start = "a", string extra = "") =>
        ScriptDocument.Parse($$"""
            { "format": "yawble-script/1", "id": "t", "name": "T", "trigger": { "accepts": ["once"] },
              "settings": {{settings}}, "requires": {{requires}}, "variables": {{variables}}, {{extra}}
              "start": "{{start}}", "steps": {{steps}} }
            """);
}
