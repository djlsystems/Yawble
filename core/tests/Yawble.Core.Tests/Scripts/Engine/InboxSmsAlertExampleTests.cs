using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Tests.Scripts.Engine;

/// <summary>The design's example script (inbox-sms-alert) run end to end against a fake mailbox and a fake SMS API.</summary>
public sealed class InboxSmsAlertExampleTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 23, 18, 0, 0, TimeSpan.Zero);
    private readonly TempFolder _temp = new();
    private readonly FakeClock _clock = new(T0);
    private readonly ScriptDocument _doc = ScriptDocument.Parse(ScriptSamples.InboxSmsAlert);

    /// <summary>The mailbox: messages after <c>since</c>, oldest first.</summary>
    private readonly List<JsonObject> _mailbox =
    [
        ScriptSamples.Email("m1", "Order 1001", "shop@example.com", "2026-11-23T10:00:00Z"),
        ScriptSamples.Email("m2", "Proof approved", "ann@example.org", "2026-11-23T12:30:00Z"),
    ];

    private readonly List<JsonObject> _texts = [];
    private readonly List<JsonObject> _listCalls = [];
    private int? _failTextNumber;

    public void Dispose() => _temp.Dispose();

    private string Data => _temp.Sub("data");

    private FakeActions Actions() => new FakeActions()
        .On("mail", "list", args =>
        {
            _listCalls.Add(args);
            var since = (string?)args["since"];
            var list = _mailbox.Where(m => since is null || ScriptValues.ToInstant((string)m["receivedAt"]!) > ScriptValues.ToInstant(since));
            return new JsonArray(list.Select(m => (JsonNode)m.DeepClone()).ToArray());
        })
        .On("sms", FakeCatalog.SmsAction, args =>
        {
            if (_texts.Count + 1 == _failTextNumber) throw new InvalidOperationException("Twilio answered 500");
            _texts.Add(args);
            return JsonNode.Parse("""{ "status": 201, "body": { "sid": "SM1" } }""");
        });

    private ScriptRunRequest Request(FakeActions actions, JsonObject? saved = null) => new()
    {
        Document = _doc,
        Actions = actions,
        Catalog = FakeCatalog.MailAndSms(),
        DataFolder = Data,
        Clock = _clock,
        Input = JsonNode.Parse("""{ "triggerId": "every-5m", "kind": "every", "firedAt": "2026-11-23T18:00:00Z" }"""),
        Settings = JsonNode.Parse("""{ "recipients": ["+15551230001", "+15551230002"], "fromNumber": "+15559990000" }""")!.AsObject(),
        SavedVariables = saved,
    };

    private Task<ScriptRunResult> Run(ScriptRunRequest request, IScriptRunJournal? journal = null) =>
        ScriptEngine.RunAsync(request with { Clock = _clock }, journal ?? new RecordingJournal(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task ItChecksClean_ThenTextsEveryNewEmailToEveryRecipient_AndMovesLastSeen()
    {
        Assert.True(ScriptChecker.Check(_doc, ScriptSchemaSet.Empty, FakeCatalog.MailAndSms()).Ok);

        var journal = new RecordingJournal();
        var r = await Run(Request(Actions()), journal);

        Assert.True(r.Status == ScriptRunStatus.Succeeded, r.Reason);
        var list = Assert.Single(_listCalls);
        Assert.Equal("INBOX", (string?)list["folder"]);
        Assert.Null(list["since"]);

        Assert.Equal(
        [
            ("+15551230001", "New email: Order 1001 (shop@example.com)"),
            ("+15551230002", "New email: Order 1001 (shop@example.com)"),
            ("+15551230001", "New email: Proof approved (ann@example.org)"),
            ("+15551230002", "New email: Proof approved (ann@example.org)"),
        ], _texts.Select(t => ((string)t["To"]!, (string)t["Body"]!)));
        Assert.All(_texts, t => Assert.Equal("+15559990000", (string?)t["From"]));

        Assert.Equal("2026-11-23T12:30:00Z", (string?)r.SavedVariables!["lastSeen"]);
        Assert.Equal(["lastSeen"], r.SavedVariables.Select(p => p.Key));
        Assert.Equal("2026-11-23T18:00:00Z 2 new, 4 texts\n", File.ReadAllText(Path.Combine(Data, "run.log")));
        Assert.Equal(["start", "list", "any", "each", "phones", "text", "count", "text", "count", "phones", "text", "count", "text", "count", "mark", "log", "done"],
            journal.CompletedSteps);
    }

    [Fact]
    public async Task TheNextRunTextsOnlyWhatCameAfterLastSeen_AndNothingWhenNothingCame()
    {
        var first = await Run(Request(Actions()));
        _texts.Clear();

        var second = await Run(Request(Actions(), first.SavedVariables));
        Assert.Equal(ScriptRunStatus.Succeeded, second.Status);
        Assert.Equal("2026-11-23T12:30:00Z", (string?)_listCalls[^1]["since"]);
        Assert.Empty(_texts);
        Assert.Equal("2026-11-23T12:30:00Z", (string?)second.SavedVariables!["lastSeen"]);

        _mailbox.Add(ScriptSamples.Email("m3", "Where is my order?", "bob@example.net", "2026-11-23T17:45:00Z"));
        _clock.Now = T0.AddMinutes(5);
        var third = await Run(Request(Actions(), second.SavedVariables));
        Assert.Equal(["New email: Where is my order? (bob@example.net)", "New email: Where is my order? (bob@example.net)"], _texts.Select(t => (string)t["Body"]!));
        Assert.Equal("2026-11-23T17:45:00Z", (string?)third.SavedVariables!["lastSeen"]);
        Assert.EndsWith("2026-11-23T18:05:00Z 1 new, 2 texts\n", File.ReadAllText(Path.Combine(Data, "run.log")));
    }

    [Fact]
    public async Task AFailedTextFailsTheRun_AndLastSeenDoesNotMove()
    {
        _failTextNumber = 3;
        var r = await Run(Request(Actions(), new JsonObject { ["lastSeen"] = null }));

        Assert.Equal(ScriptRunStatus.Failed, r.Status);
        Assert.Equal("text", r.FailedStepId);
        Assert.Contains("Twilio answered 500", r.Reason);
        Assert.Null(r.SavedVariables);
        Assert.Equal(2, _texts.Count);
        Assert.False(File.Exists(Path.Combine(Data, "run.log")));
    }

    [Fact]
    public async Task ADryRunReadsTheMailbox_SendsNoText_AndWritesTheLogToTheDryRunFolder()
    {
        var dry = Path.Combine(_temp.Path, "dry-run");
        var r = await Run(Request(Actions()) with { DryRun = true, DryRunFolder = dry });

        Assert.True(r.Status == ScriptRunStatus.Succeeded, r.Reason);
        Assert.Single(_listCalls);
        Assert.Empty(_texts);
        var would = r.Log.Where(l => l.StartsWith("would POST /2010-04-01/Accounts/{AccountSid}/Messages.json on sms with ", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, would.Count);
        Assert.Contains("\"Body\":\"New email: Order 1001 (shop@example.com)\"", would[0]);
        Assert.False(File.Exists(Path.Combine(Data, "run.log")));
        Assert.Equal("2026-11-23T18:00:00Z 2 new, 4 texts\n", File.ReadAllText(Path.Combine(dry, "run.log")));
    }
}
