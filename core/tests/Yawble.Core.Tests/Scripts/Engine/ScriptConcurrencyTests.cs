using System.Text.Json.Nodes;
using Xunit;
using Yawble.Core.Scripts.Engine;
using Yawble.Core.Scripts.Language;

namespace Yawble.Core.Tests.Scripts.Engine;

/// <summary>
/// One script document run by several script members at once (two teams, or two members of one team): each run has its
/// own settings, saved variables and data folder, and neither sees the other's or changes the document.
/// </summary>
public sealed class ScriptConcurrencyTests : IDisposable
{
    private readonly TempFolder _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>A member's mailbox, texts sent and list calls; the actions wait at <paramref name="meet"/> on their first text.</summary>
    private sealed class Member(string folder, IEnumerable<JsonObject> mailbox, Rendezvous? meet)
    {
        public List<JsonObject> Texts { get; } = [];
        public List<JsonObject> ListCalls { get; } = [];
        private bool _met;

        public FakeActions Actions() => new FakeActions
        {
            Before = async (call, ct) =>
            {
                if (meet is null || _met || call.Use != "sms") return;
                _met = true;
                await meet.ArriveAsync(ct);
            },
        }
        .On("mail", "list", args =>
        {
            lock (ListCalls) ListCalls.Add((JsonObject)args.DeepClone());
            Assert.Equal(folder, (string?)args["folder"]);
            var since = (string?)args["since"];
            var list = mailbox.Where(m => since is null || ScriptValues.ToInstant((string)m["receivedAt"]!) > ScriptValues.ToInstant(since));
            return new JsonArray(list.Select(m => (JsonNode)m.DeepClone()).ToArray());
        })
        .On("sms", FakeCatalog.SmsAction, args =>
        {
            lock (Texts) Texts.Add((JsonObject)args.DeepClone());
            return JsonNode.Parse("""{ "status": 201, "body": { "sid": "SM1" } }""");
        });
    }

    /// <summary>Holds each arriving run until <c>count</c> runs have arrived, so the runs are inside the engine together.</summary>
    private sealed class Rendezvous(int count)
    {
        private readonly TaskCompletionSource _all = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public Task ArriveAsync(CancellationToken ct)
        {
            if (Interlocked.Increment(ref _arrived) == count) _all.TrySetResult();
            return _all.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
    }

    private static ScriptRunRequest Request(ScriptDocument doc, Member member, string data, JsonObject settings, JsonObject? saved) => new()
    {
        Document = doc,
        Actions = member.Actions(),
        Catalog = FakeCatalog.MailAndSms(),
        DataFolder = data,
        Input = JsonNode.Parse("""{ "triggerId": "every-5m", "kind": "every", "firedAt": "2026-11-23T18:00:00Z" }"""),
        Settings = settings,
        SavedVariables = saved,
    };

    [Fact]
    public async Task TwoRunsOfOneDocumentAtOnce_GiveIndependentResults_AndLeaveTheDocumentAsItWas()
    {
        // The document carries a designer layout too: the runs ignore it and it is unchanged afterwards.
        var doc = ScriptDocument.Parse(ScriptSamples.WithLayout());
        var before = doc.ToJsonString();
        var ct = TestContext.Current.CancellationToken;
        var meet = new Rendezvous(2);

        // Member A (one team): INBOX, two phones, nothing seen yet; both messages are new.
        var a = new Member("INBOX",
        [
            ScriptSamples.Email("a1", "Order 1001", "shop@example.com", "2026-11-23T10:00:00Z"),
            ScriptSamples.Email("a2", "Proof approved", "ann@example.org", "2026-11-23T12:30:00Z"),
        ], meet);
        var aSettings = JsonNode.Parse("""{ "recipients": ["+15551230001", "+15551230002"], "fromNumber": "+15559990000" }""")!.AsObject();

        // Member B (another team, or a second member of the same one): folder Work, one phone, last saw 11:00; one new message.
        var b = new Member("Work",
        [
            ScriptSamples.Email("b1", "Old news", "old@example.net", "2026-11-23T09:00:00Z"),
            ScriptSamples.Email("b2", "Invoice 7", "billing@example.net", "2026-11-23T16:15:00Z"),
        ], meet);
        var bSettings = JsonNode.Parse("""{ "recipients": ["+447700900123"], "fromNumber": "+447700900999", "folder": "Work" }""")!.AsObject();
        var bSaved = new JsonObject { ["lastSeen"] = "2026-11-23T11:00:00Z" };

        var aData = _temp.Sub("team-a-member");
        var bData = _temp.Sub("team-b-member");
        var aJournal = new RecordingJournal();
        var bJournal = new RecordingJournal();

        var aRun = Task.Run(() => ScriptEngine.RunAsync(Request(doc, a, aData, aSettings, null), aJournal, ct), ct);
        var bRun = Task.Run(() => ScriptEngine.RunAsync(Request(doc, b, bData, bSettings, bSaved), bJournal, ct), ct);
        var results = await Task.WhenAll(aRun, bRun);
        var (ra, rb) = (results[0], results[1]);

        Assert.True(ra.Status == ScriptRunStatus.Succeeded, ra.Reason);
        Assert.True(rb.Status == ScriptRunStatus.Succeeded, rb.Reason);

        // Each run listed its own folder, from its own saved lastSeen.
        var aList = Assert.Single(a.ListCalls);
        Assert.Equal("INBOX", (string?)aList["folder"]);
        Assert.Null(aList["since"]);
        var bList = Assert.Single(b.ListCalls);
        Assert.Equal("Work", (string?)bList["folder"]);
        Assert.Equal("2026-11-23T11:00:00Z", (string?)bList["since"]);

        // Each texted only its own phones, from its own number, about its own mail.
        Assert.Equal(
            [
                ("+15551230001", "+15559990000", "New email: Order 1001 (shop@example.com)"),
                ("+15551230002", "+15559990000", "New email: Order 1001 (shop@example.com)"),
                ("+15551230001", "+15559990000", "New email: Proof approved (ann@example.org)"),
                ("+15551230002", "+15559990000", "New email: Proof approved (ann@example.org)"),
            ],
            a.Texts.Select(t => ((string?)t["To"], (string?)t["From"], (string?)t["Body"])));
        Assert.Equal(
            [("+447700900123", "+447700900999", "New email: Invoice 7 (billing@example.net)")],
            b.Texts.Select(t => ((string?)t["To"], (string?)t["From"], (string?)t["Body"])));

        // Each got its own saved variables back; B's input object was not changed.
        Assert.Equal("2026-11-23T12:30:00Z", (string?)ra.SavedVariables!["lastSeen"]);
        Assert.Equal("2026-11-23T16:15:00Z", (string?)rb.SavedVariables!["lastSeen"]);
        Assert.Equal("2026-11-23T11:00:00Z", (string?)bSaved["lastSeen"]);
        Assert.Equal(["+15551230001", "+15551230002"], aSettings["recipients"]!.AsArray().Select(n => (string?)n));

        // Each wrote its own run log in its own data folder only.
        var aLog = File.ReadAllLines(Path.Combine(aData, "run.log"));
        var bLog = File.ReadAllLines(Path.Combine(bData, "run.log"));
        Assert.EndsWith(" 2 new, 4 texts", Assert.Single(aLog));
        Assert.EndsWith(" 1 new, 1 texts", Assert.Single(bLog));

        // The journals hold only their own run's steps and values.
        Assert.Equal(4, aJournal.Completed.Count(c => c.Record.StepId == "text"));
        Assert.Single(bJournal.Completed, c => c.Record.StepId == "text");
        Assert.Equal(4L, (long?)aJournal.LastCheckpoint.Vars["sent"]);
        Assert.Equal(1L, (long?)bJournal.LastCheckpoint.Vars["sent"]);

        // The document both ran is exactly as it was given, layout and all.
        Assert.Equal(before, doc.ToJsonString());
    }

    [Fact]
    public async Task ManyRunsOfOneFreshlyParsedDocumentInParallel_EachSeeOnlyTheirOwnSettings()
    {
        // A freshly parsed document, first touched by all runs at once: any lazily built JSON in it is built before sharing.
        var doc = ScriptSamples.Script("""
            [ { "id": "a", "type": "forEach", "items": "{settings.items}", "as": "x", "body": "b", "then": "w" },
              { "id": "b", "type": "set", "var": "total", "value": "{vars.total + x}", "next": null },
              { "id": "w", "type": "file.writeAllText", "path": "out/{settings.name}.txt", "text": "{settings.name}={vars.total}", "next": "c" },
              { "id": "c", "type": "end", "outputs": { "total": "{vars.total}", "name": "{settings.name}" } } ]
            """,
            variables: """[ { "name": "total", "type": "int", "scope": "saved", "initial": 0 } ]""",
            settings: """[ { "name": "name", "type": "string" }, { "name": "items", "type": "list<int>", "default": [ 1 ] } ]""",
            extra: """ "outputs": [ { "name": "total", "type": "int" }, { "name": "name", "type": "string" } ], "layout": { "nodes": { "a": { "x": 1 } } }, """);
        var before = doc.ToJsonString();
        var ct = TestContext.Current.CancellationToken;

        const int runs = 16;
        var tasks = Enumerable.Range(0, runs).Select(i => Task.Run(async () =>
        {
            var data = _temp.Sub($"m{i}");
            var items = new JsonArray(Enumerable.Range(1, i + 1).Select(n => (JsonNode)JsonValue.Create(n)).ToArray());
            var settings = i % 4 == 0 ? new JsonObject { ["name"] = $"m{i}" } : new JsonObject { ["name"] = $"m{i}", ["items"] = items };
            var r = await ScriptEngine.RunAsync(new ScriptRunRequest
            {
                Document = doc,
                Actions = new FakeActions(),
                DataFolder = data,
                Settings = settings,
                SavedVariables = new JsonObject { ["total"] = i * 1000 },
            }, new RecordingJournal(), ct);
            return (i, data, r);
        }, ct)).ToList();

        foreach (var (i, data, r) in await Task.WhenAll(tasks))
        {
            Assert.True(r.Status == ScriptRunStatus.Succeeded, $"run {i}: {r.Reason}");
            var expected = i * 1000 + (i % 4 == 0 ? 1 : (i + 1) * (i + 2) / 2);
            Assert.Equal(expected, (long?)r.Outputs!["total"]);
            Assert.Equal($"m{i}", (string?)r.Outputs["name"]);
            Assert.Equal(expected, (long?)r.SavedVariables!["total"]);
            Assert.Equal([$"m{i}.txt"], Directory.GetFiles(Path.Combine(data, "out")).Select(Path.GetFileName));
            Assert.Equal($"m{i}={expected}", File.ReadAllText(Path.Combine(data, "out", $"m{i}.txt")));
        }

        Assert.Equal(before, doc.ToJsonString());
    }
}
