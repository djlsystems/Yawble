using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Identity;
using Harness.Messaging;

namespace Harness.Tests;

/// <summary>
/// The worker connection's frames are plain JSON that come back the same, and a run's secrets are
/// never in them in the clear: they are sealed under the connection's own key and open only with it.
/// </summary>
public sealed class WorkerFrameTests
{
    private const string Value = "fake-issued-value-Qz8p/+x";

    private static readonly RunId Run = new(new ContainerId("alpha", "worker"), "n1");

    private static readonly byte[] Key = WorkerFrameCodec.Key("the-worker-key", "control-nonce", "worker-nonce");

    private static IReadOnlyList<WorkerFrame> EveryFrame() =>
    [
        new WorkerHello(new WorkerId("w1"), "1.2.3", "session", "nonce", new WorkerHelloCapacity(4, 8_000_000_000), [Run], 41, Draining: true),
        new WorkerWelcome("nonce", 40, new RunWorkerSettings(new MemoryFigure(2048, "computed", false), 1024)),
        new WorkerRefused("This worker is version 0.0.0 and control is version 1.2.3; a worker must run control's version."),
        new CommandFrame(7, new CancelRun(Run)),
        new AppliedFrame(7, null),
        new EventFrame(new WorkerEnvelope(new WorkerId("w1"), 42, new RunOutput(Run, "out"))),
        new HandledFrame(42),
        new PingFrame(3),
        new PongFrame(3),
        new StreamFrame(new StreamChunk("terminal:t1", 1, [27, 91, 72], Gap: "Output was lost.")),
        new StreamInputFrame(new StreamInput("terminal:t1", "hello\r"u8.ToArray())),
        new WorkerDraining(true),
        new WorkerSettingsFrame(new RunWorkerSettings(new MemoryFigure(777, "runs.memoryLimitMb is set to 777 MB", true), 1024)),
    ];

    [Fact]
    public void A_command_list_never_holds_its_credential_in_clear_and_it_opens_on_the_other_end()
    {
        var codec = new WorkerFrameCodec(Key);
        var commands = new RunAgentCommands(
            "r1", [new AgentCliRun("claude", ["mcp", "list"], new Dictionary<string, string>(), [], 45, Scratch: true)], Start().Credential);

        var text = codec.Write(new CommandFrame(3, commands));

        foreach (var form in ValueRedactor.For([Value]).Forms) Assert.DoesNotContain(form, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Qz8p", text, StringComparison.Ordinal);

        var back = (RunAgentCommands)((CommandFrame)codec.Read(text)).Message;
        Assert.Equal(Value, back.Credential!.Environment["ISSUED_KEY"]);
        Assert.Equal(["mcp", "list"], back.Commands.Single().Arguments);

        // None to seal: the frame is plain, and no key is needed.
        var plain = new WorkerFrameCodec(null).Write(new CommandFrame(4, commands with { Credential = null }));
        Assert.Null(((RunAgentCommands)((CommandFrame)codec.Read(plain)).Message).Credential);
    }

    [Fact]
    public void Every_frame_round_trips_through_json_unchanged()
    {
        var codec = new WorkerFrameCodec(Key);
        var frames = EveryFrame();

        // A frame added without a case here fails, so none travels untested.
        Assert.Equal(
            typeof(WorkerFrame).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(WorkerFrame)) && !t.IsAbstract).OrderBy(t => t.Name),
            frames.Select(f => f.GetType()).OrderBy(t => t.Name));

        foreach (var frame in frames)
        {
            var text = codec.Write(frame);
            var back = codec.Read(text);

            Assert.Equal(frame.GetType(), back.GetType());
            Assert.Equal(text, codec.Write(back));
        }
    }

    [Fact]
    public void Run_secrets_are_sealed_and_open_only_with_the_same_key_and_nonces()
    {
        var start = Start();
        var text = new WorkerFrameCodec(Key).Write(new CommandFrame(1, start));

        // The other end of the same connection opens them.
        var back = (StartRun)((CommandFrame)new WorkerFrameCodec(
            WorkerFrameCodec.Key("the-worker-key", "control-nonce", "worker-nonce")).Read(text)).Message;
        Assert.Equal(CredentialSource.Issued, back.Credential!.Source);
        Assert.Equal(Value, back.Credential.Environment["ISSUED_KEY"]);
        Assert.Equal(["OTHER"], back.Credential.Displace);
        Assert.Equal(["THIRD"], back.Credential.OtherProviders);
        Assert.True(back.Credential.PerRunHome);
        Assert.Null(back.Credential.Missing);
        Assert.Equal(start.Redaction!.Forms, back.Redaction!.Forms);
        Assert.Equal(
            System.Text.Json.JsonSerializer.Serialize<ControlMessage>(start with { Credential = null, Redaction = null }, WorkerFrameCodec.Json),
            System.Text.Json.JsonSerializer.Serialize<ControlMessage>(back with { Credential = null, Redaction = null }, WorkerFrameCodec.Json));

        // Another connection's nonces, or another worker key, do not.
        foreach (var other in new[]
        {
            WorkerFrameCodec.Key("the-worker-key", "another-control-nonce", "worker-nonce"),
            WorkerFrameCodec.Key("the-worker-key", "control-nonce", "another-worker-nonce"),
            WorkerFrameCodec.Key("another-worker-key", "control-nonce", "worker-nonce"),
        })
        {
            var refused = Assert.Throws<InvalidDataException>(() => new WorkerFrameCodec(other).Read(text));
            Assert.Equal(WorkerFrameCodec.UnsealableText, refused.Message);
        }

        // The worker's applied set crosses back the same way.
        var applied = new EventFrame(new WorkerEnvelope(new WorkerId("w1"), 5, new RunCredentialApplied(Run, ValueRedactor.For([Value]))));
        var opened = (RunCredentialApplied)((EventFrame)new WorkerFrameCodec(Key).Read(new WorkerFrameCodec(Key).Write(applied))).Envelope.Event;
        Assert.Equal(ValueRedactor.For([Value]).Forms, opened.Redaction!.Forms);
    }

    [Fact]
    public void A_tampered_sealed_secret_is_refused()
    {
        var codec = new WorkerFrameCodec(Key);
        var text = codec.Write(new CommandFrame(1, Start()));
        var frame = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        var blob = Convert.FromBase64String(frame["sealed"]!.GetValue<string>());
        blob[^1] ^= 0x01;
        frame["sealed"] = Convert.ToBase64String(blob);

        var refused = Assert.Throws<InvalidDataException>(() => codec.Read(frame.ToJsonString()));
        Assert.Equal(WorkerFrameCodec.UnsealableText, refused.Message);

        // A sealed field on a frame that carries no secret is refused too.
        var stray = Assert.Throws<InvalidDataException>(() => codec.Read(codec.Write(new CommandFrame(1, new CancelRun(Run), "AAAA"))));
        Assert.Equal(WorkerFrameCodec.UnsealableText, stray.Message);
    }

    [Fact]
    public void No_frame_holds_a_credential_or_redaction_value_in_clear()
    {
        var codec = new WorkerFrameCodec(Key);
        var texts = new[]
        {
            codec.Write(new CommandFrame(1, Start())),
            codec.Write(new EventFrame(new WorkerEnvelope(new WorkerId("w1"), 5, new RunCredentialApplied(Run, ValueRedactor.For([Value]))))),
            codec.Write(new CommandFrame(2, new CheckLaunch(
                "r1", Start().Launch!, ["--version"], [], new Dictionary<string, string>(), null, 60, null, Start().Credential, Start().Redaction))),
        };

        // A launch check's secrets open again on the other end.
        var check = (CheckLaunch)((CommandFrame)codec.Read(texts[2])).Message;
        Assert.Equal(Value, check.Credential!.Environment["ISSUED_KEY"]);

        foreach (var text in texts)
        {
            foreach (var form in ValueRedactor.For([Value]).Forms)
            {
                Assert.DoesNotContain(form, text, StringComparison.OrdinalIgnoreCase);
            }

            Assert.DoesNotContain("Qz8p", text, StringComparison.Ordinal);
        }

        // With no key, no secret crosses at all.
        Assert.Throws<InvalidOperationException>(() => new WorkerFrameCodec(null).Write(new CommandFrame(1, Start())));
    }

    [Fact]
    public void A_message_over_the_size_limit_is_refused()
    {
        var codec = new WorkerFrameCodec(Key);
        var huge = new string('x', WorkerFrameCodec.MaxBytes + 1);

        var written = Assert.Throws<InvalidDataException>(
            () => codec.Write(new EventFrame(new WorkerEnvelope(new WorkerId("w1"), 1, new RunOutput(Run, huge)))));
        Assert.Equal("A worker message over 16 MiB was refused.", written.Message);

        var read = Assert.Throws<InvalidDataException>(() => codec.Read(huge));
        Assert.Equal(WorkerFrameCodec.TooLargeText, read.Message);
    }

    [Fact]
    public void A_terminal_launch_and_a_read_never_hold_a_secret_in_clear_and_open_on_the_other_end()
    {
        var codec = new WorkerFrameCodec(Key);
        var launch = new TerminalLaunch(
            ["claude"], "/work", new Dictionary<string, string> { ["HARNESS_KEY"] = Value, ["ISSUED_KEY"] = Value + "-2" },
            [], null, null, new TerminalMcp("http://127.0.0.1:5000", "concierge-u1"));
        var texts = new[]
        {
            codec.Write(new CommandFrame(1, new StartTerminal("terminal:t1", launch, 100, 30))),
            codec.Write(new CommandFrame(2, new ReadAgentFile("r1", "/t.jsonl", ["ISSUED_KEY"], true, ValueRedactor.For([Value])))),
        };

        foreach (var text in texts) Assert.DoesNotContain("Qz8p", text, StringComparison.Ordinal);

        var start = (StartTerminal)((CommandFrame)codec.Read(texts[0])).Message;
        Assert.Equal(Value, start.Launch.Environment["HARNESS_KEY"]);
        Assert.Equal(Value + "-2", start.Launch.Environment["ISSUED_KEY"]);
        Assert.Equal("http://127.0.0.1:5000", start.Launch.Mcp!.BaseUrl);

        var read = (ReadAgentFile)((CommandFrame)codec.Read(texts[1])).Message;
        Assert.Equal(ValueRedactor.For([Value]).Forms, read.Redaction!.Forms);

        // With no key, a terminal's environment does not cross at all.
        Assert.Throws<InvalidOperationException>(() => new WorkerFrameCodec(null).Write(new CommandFrame(1, new StartTerminal("t", launch, 1, 1))));
    }

    private static StartRun Start() =>
        new(Run, "agent", "system", "prompt", "context", "/work", new Dictionary<string, string> { ["A"] = "1" }, null,
            new RunLaunch("cli", ["-p"], null, null, null, true, null, null, 600, []), null, "/tmp", null,
            Credential: new RunCredential(
                CredentialSource.Issued, new Dictionary<string, string> { ["ISSUED_KEY"] = Value }, ["OTHER"], ["THIRD"], true, null),
            Redaction: ValueRedactor.For([Value]),
            CredentialNames: ["ISSUED_KEY"]);

    // ---- a plugin run's site data, inside the StartRun frame ----------------------------------------

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const long Budget = PluginMemberRunner.SiteReadBudgetBytes;
    private const int Margin = PluginMemberRunner.EnvelopeMarginBytes;

    /// <summary>How many <see cref="Heavy"/> documents a frame test stores: together they frame at
    /// well over the 8 MiB budget.</summary>
    private const int Heavies = 80;

    /// <summary>A stored document of about 60 KB, full of what both encoders escape: quotes,
    /// <c>&lt;</c>, <c>&gt;</c>, <c>&amp;</c>, <c>'</c>, <c>+</c>, non-ASCII and a surrogate pair,
    /// under short keys.</summary>
    private static string Heavy()
    {
        var text = string.Concat(Enumerable.Repeat("\"<>&'+é😀 ab", 20));
        var document = new JsonObject();
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        for (var i = 0; Encoding.UTF8.GetByteCount(document.ToJsonString(options)) < 60_000; i++) document[$"k{i}"] = text;
        return document.ToJsonString(options);
    }

    /// <summary>A plugin run's whole frame, in bytes, as the codec writes it at the real limit.</summary>
    private static long FrameBytes(StartRun start, string stdin) =>
        Encoding.UTF8.GetByteCount(new WorkerFrameCodec(Key).Write(new CommandFrame(1, start with { Process = start.Process! with { Stdin = stdin } })));

    /// <summary>The request with its <c>sites</c> block replaced by <paramref name="sites"/>: the
    /// block is the request's last property.</summary>
    private static string WithSites(string stdin, string sites)
    {
        var at = stdin.LastIndexOf(",\"sites\":[", StringComparison.Ordinal);
        Assert.True(at > 0);
        return stdin[..at] + ",\"sites\":" + sites + "}\n";
    }

    /// <summary>The same block with every envelope's documents taken out.</summary>
    private static string EnvelopesOnly(string stdin)
    {
        var sites = JsonNode.Parse(stdin)!["sites"]!.AsArray();
        foreach (var envelope in sites) envelope!["documents"] = new JsonArray();
        return WithSites(stdin, sites.ToJsonString());
    }

    /// <summary>What one document entry costs in the frame, measured here as the frame measures it.</summary>
    private static long EntryBytes(SiteDocument document) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(new JsonObject
        {
            ["id"] = document.Id,
            ["doc"] = JsonNode.Parse(document.Json),
            ["updatedAt"] = document.UpdatedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture),
            ["updatedBy"] = document.UpdatedBy,
        }.ToJsonString(), WorkerFrameCodec.Json)) - 2;

    private static string Status(Message row) =>
        JsonDocument.Parse(row.Payload).RootElement.GetProperty("status").GetString()!;

    [Fact]
    public async Task A_run_cut_at_the_real_budget_still_fits_the_worker_frame()
    {
        await using var bed = await PluginReadBed.CreateAsync();
        var heavy = Heavy();
        for (var i = 0; i < Heavies; i++) await bed.PutAsync("items", $"h{i:00}", heavy, i);

        var run = await bed.RunAsync("""[{"site":"board","collection":"items"}]""");

        var stdin = run.Start.Process!.Stdin;
        var envelope = JsonNode.Parse(stdin)!["sites"]!.AsArray().Single()!;
        var delivered = envelope["documents"]!.AsArray().Count;
        Assert.InRange(delivered, 1, Heavies - 1);
        Assert.False(run.Delivery.FrameLimited);
        Assert.Equal(
            $"Only {delivered} of {Heavies} documents of board/items were delivered (newest first): this run's site data is limited to 8 MiB. The rest were cut.",
            (string)envelope["cut"]!);
        Assert.Equal([$"Site data for this run was cut: board/items {delivered} of {Heavies}. This run's site data is limited to 8 MiB."], run.Rows);

        // THE FRAME, at the real limit, does not refuse it.
        var frame = FrameBytes(run.Start, stdin);
        Assert.True(frame <= WorkerFrameCodec.MaxBytes);

        // And the runner counted exactly what the frame grew by.
        Assert.True(run.Delivery.DocumentBytes <= Budget);
        Assert.Equal(frame - FrameBytes(run.Start, WithSites(stdin, "[]")), run.Delivery.DocumentBytes + run.Delivery.EnvelopeBytes);
        Assert.Equal(FrameBytes(run.Start, EnvelopesOnly(stdin)) - FrameBytes(run.Start, WithSites(stdin, "[]")), run.Delivery.EnvelopeBytes);
    }

    [Fact]
    public async Task A_run_whose_rest_is_large_gets_the_room_left_in_the_frame_and_says_so()
    {
        await using var bed = await PluginReadBed.CreateAsync();
        var heavy = Heavy();
        for (var i = 0; i < Heavies; i++) await bed.PutAsync("items", $"h{i:00}", heavy, i);

        // THE REST, escape-heavy, split across the work batch, config and a secret.
        string Pad(int length) => string.Concat(Enumerable.Repeat("<\"é a", length / 5));
        var work = Pad(110_000);
        var config = Pad(650_000);
        var secret = Pad(650_000);

        var run = await bed.RunAsync("""[{"site":"board","collection":"items"}]""", work: [work, work, work], config: config, secret: secret);

        var stdin = run.Start.Process!.Stdin;
        var request = JsonNode.Parse(stdin)!;
        Assert.Equal(3, request["work"]!.AsArray().Count);

        // (a) PRECONDITIONS: the site data alone would reach a plain 8 MiB budget, and the rest
        // leaves less than that.
        var documents = (await bed.Store.ListDocumentsAsync("alpha", "board", "items", Ct))
            .OrderByDescending(d => d.UpdatedAt).ThenBy(d => d.Id, StringComparer.Ordinal).ToList();
        Assert.True(documents.Sum(EntryBytes) > Budget);
        var rest = FrameBytes(run.Start, WithSites(stdin, "[]"));
        Assert.True(rest > WorkerFrameCodec.MaxBytes - Budget);

        // (b) EACH PART alone fits beside a full budget; together they do not.
        long Part(string name) => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request[name]!.ToJsonString(), WorkerFrameCodec.Json)) - 2;
        foreach (var part in new[] { "work", "config", "secrets" })
        {
            Assert.True(Part(part) <= WorkerFrameCodec.MaxBytes - Budget - Margin, part);
        }

        Assert.True(Part("work") + Part("config") + Part("secrets") > WorkerFrameCodec.MaxBytes - Budget);

        // (c) ESCAPE-HEAVY: framed, the rest is more than 64 KiB over its raw size.
        var raw = Encoding.UTF8.GetByteCount(work) * 3 + Encoding.UTF8.GetByteCount(config) + Encoding.UTF8.GetByteCount(secret);
        Assert.True(Part("work") + Part("config") + Part("secrets") - raw > Margin);

        // THE FRAME holds, at the real limit.
        var frame = FrameBytes(run.Start, stdin);
        Assert.True(frame <= WorkerFrameCodec.MaxBytes);

        // (d) THE MAXIMAL PREFIX of what the frame left.
        var room = WorkerFrameCodec.MaxBytes - rest - Margin;
        var envelope = request["sites"]!.AsArray().Single()!;
        var delivered = envelope["documents"]!.AsArray().Count;
        var spent = frame - FrameBytes(run.Start, EnvelopesOnly(stdin));
        Assert.True(spent <= room);
        Assert.True(spent + EntryBytes(documents[delivered]) + (delivered > 0 ? 1 : 0) > room);
        Assert.True(run.Delivery.FrameLimited);
        Assert.Equal(room, run.Delivery.Budget);

        Assert.Equal(
            $"Only {delivered} of {Heavies} documents of board/items were delivered (newest first): this run's site data is limited to 8 MiB, less what the rest of the request takes; {room} bytes were left. The rest were cut.",
            (string)envelope["cut"]!);
        Assert.Equal(
            [$"Site data for this run was cut: board/items {delivered} of {Heavies}. This run's site data is limited to 8 MiB, less what the rest of the request takes; {room} bytes were left."],
            run.Rows);
    }

    [Fact]
    public async Task A_run_whose_rest_leaves_no_room_delivers_every_envelope_and_no_document()
    {
        await using var bed = await PluginReadBed.CreateAsync();
        for (var i = 0; i < 5; i++)
        {
            await bed.PutAsync("items", $"i{i}", $$"""{"title":"{{new string('a', 300)}}"}""", i);
            await bed.PutAsync("notes", $"n{i}", $$"""{"title":"{{new string('b', 300)}}"}""", i);
        }

        const string Reads = """[{"site":"board","collection":"items"},{"site":"board","collection":"notes"}]""";

        // CALIBRATED: one `<` in config costs 7 framed bytes, so the rest is sized just above
        // MaxBytes - 64 KiB from a first measured run.
        var first = await bed.RunAsync(Reads, config: new string('<', 2_000_000));
        var measured = FrameBytes(first.Start, WithSites(first.Start.Process!.Stdin, "[]"));
        var target = WorkerFrameCodec.MaxBytes - Margin + 16 * 1024;
        var run = await bed.RunAsync(Reads, config: new string('<', 2_000_000 + (int)((target - measured) / 7)));

        var stdin = run.Start.Process!.Stdin;
        var rest = FrameBytes(run.Start, WithSites(stdin, "[]"));
        Assert.True(WorkerFrameCodec.MaxBytes - Margin < rest);
        Assert.True(rest + 4096 < WorkerFrameCodec.MaxBytes);

        // Without the margin, the documents would have had room.
        Assert.True(WorkerFrameCodec.MaxBytes - rest > 10 * 400);

        Assert.True(FrameBytes(run.Start, stdin) <= WorkerFrameCodec.MaxBytes);

        var sites = JsonNode.Parse(stdin)!["sites"]!.AsArray();
        Assert.Equal(["items", "notes"], sites.Select(s => (string)s!["collection"]!));
        foreach (var envelope in sites)
        {
            Assert.Empty(envelope!["documents"]!.AsArray());
            Assert.Equal(5, (int)envelope["total"]!);
            Assert.Equal(
                $"Only 0 of 5 documents of board/{(string)envelope["collection"]!} were delivered (newest first): this run's site data is limited to 8 MiB, less what the rest of the request takes; 0 bytes were left. The rest were cut.",
                (string)envelope["cut"]!);
        }

        Assert.Equal(0, run.Delivery.Budget);
        Assert.Equal(
            ["Site data for this run was cut: board/items 0 of 5, board/notes 0 of 5. This run's site data is limited to 8 MiB, less what the rest of the request takes; 0 bytes were left."],
            run.Rows);
    }

    /// <summary>
    /// A plugin member on alpha whose runner is built as the Host's DI builds it - over an
    /// <see cref="IRunWorker"/>, with no budget passed - and whose worker keeps the
    /// <see cref="StartRun"/> it is given before handing it to a real in-process worker.
    /// </summary>
    private sealed class PluginReadBed : IAsyncDisposable
    {
        private static readonly ContainerId Plug = new("alpha", "plug");
        private static readonly DateTimeOffset Base = new(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);

        private readonly string _dataRoot = Directory.CreateTempSubdirectory("harness-frame-reads-").FullName;
        private readonly List<IDisposable> _registrations = [];
        private readonly List<ContainerTestBed> _beds = [];

        public SqliteSiteStore Store { get; private set; } = null!;

        public static async Task<PluginReadBed> CreateAsync()
        {
            var bed = new PluginReadBed();
            var database = Path.Combine(bed._dataRoot, "harness.db");
            await new SchemaMigrator(database).ApplyAsync(AuthSchema.Steps, ct: Ct);
            bed.Store = new SqliteSiteStore(database);
            Assert.True(await bed.Store.CreateAsync(
                new SiteRow("alpha", "board", null, 1, DateTimeOffset.UtcNow, "person@example.test"),
                new TriggerAudit(null, null, "site.created", "alpha/board", "board", null), Ct));
            return bed;
        }

        /// <summary>Document <paramref name="order"/> is older the higher it is: h00 is the newest.</summary>
        public async Task PutAsync(string collection, string id, string json, int order) =>
            Assert.Null((await Store.PutDocumentAsync("alpha", "board", collection, id, json, "person@example.test", Base.AddMinutes(-order), Ct)).Refusal);

        public sealed record Ran(StartRun Start, PluginSiteDelivery Delivery, string[] Rows);

        private sealed class Recording(IRunWorker inner) : IRunWorker
        {
            public StartRun? Start;

            public WorkerId Id => inner.Id;

            public Task Closed => inner.Closed;

            public Task SendAsync(ControlMessage message, CancellationToken ct = default)
            {
                if (message is StartRun start) Start = start;
                return inner.SendAsync(message, ct);
            }
        }

        private sealed class Secrets(string value) : ISecretStore
        {
            public string? TryGet(string logicalKey) => logicalKey == "FIXTURE_TOKEN" ? value : null;
        }

        private sealed class Settings(PluginMemberSettings settings) : IPluginMemberSettings
        {
            public Task<PluginMemberSettings> ForAsync(ContainerId member, CancellationToken ct = default) => Task.FromResult(settings);
        }

        public async Task<Ran> RunAsync(string reads, IReadOnlyList<string>? work = null, string config = "", string secret = "s3cr3tvalue")
        {
            PluginInstall.Write(_dataRoot, "fixture", manifest: PluginInstall.Manifest("fixture", edit: m =>
            {
                m["config"] = JsonNode.Parse("""{"pad":{"type":"string","default":""}}""");
                m["secrets"] = JsonNode.Parse("""{"token":{"required":true}}""");
                m["reads"] = JsonNode.Parse(reads);
            }));

            var catalog = new PluginCatalog(PluginInstall.PluginsRoot(_dataRoot));
            catalog.Rescan();
            _registrations.Add(EventCatalog.Register(catalog));

            var bed = new ContainerTestBed();
            _beds.Add(bed);
            var heartbeat = new RunHeartbeat();
            var reports = new MemberReports(bed.Host, bed.Store, heartbeat);
            var directory = new RunDirectory(reports);
            var worker = new Recording(InProcessWorker.Connect(
                WorkerId.Local,
                events => new WorkerHost(WorkerId.Local, events, new RunLauncher(heartbeat), heartbeat),
                directory.HandleAsync).Worker);
            var sites = new SiteService(
                Store, team => team == "alpha" ? "alpha" : null, new TeamPaths(_dataRoot), bed.Store);

            var runner = new PluginMemberRunner(
                catalog, reports, worker, directory, null,
                new Settings(new PluginMemberSettings(
                    new Dictionary<string, JsonElement> { ["pad"] = JsonSerializer.SerializeToElement(config) },
                    new Dictionary<string, string> { ["token"] = "FIXTURE_TOKEN" })),
                new Secrets(secret),
                null,
                sites);

            PluginSiteDelivery? delivery = null;
            runner.SiteDataDelivered = d => delivery = d;

            await bed.Host.AddAsync(
                ContainerTestBed.Definition(Plug) with { Agent = "plugin:fixture", WorkingDirectory = _dataRoot },
                new MemberRunnerRouter(bed.Runner, runner), Ct);

            // THE BATCH, handed to the runner whole: the runtime grows a batch only from what arrives
            // within a few milliseconds, which a test cannot hold to.
            var batch = (work ?? ["sync"]).Select((instruction, n) => new Message(
                n + 1, MessageTypes.InstructionFor(Plug), JsonSerializer.Serialize(new { instruction }), "alpha/manager", 1, n == 0 ? null : 1, 0,
                DateTimeOffset.UtcNow)).ToList();

            var result = await runner.RunAsync(
                new MemberInvocation(Plug, "plugin:fixture", batch, _dataRoot, new Dictionary<string, string>(),
                    new MemberRunContext(0, ArtifactLimits.Default, [], null, null, "")),
                Ct);
            Assert.True(result.Succeeded, result.Output + result.LaunchError + result.FailureReason);

            return new Ran(worker.Start!, delivery!, [.. (await bed.OfTypeAsync(MessageTypes.Progress)).Select(Status)]);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var bed in _beds) await bed.DisposeAsync();
            foreach (var registration in _registrations) registration.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(_dataRoot, recursive: true); }
            catch (IOException) { }
        }
    }
}
