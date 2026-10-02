using Harness.Contracts;
using Harness.Host;

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
        new WorkerHello(new WorkerId("w1"), "1.2.3", "session", "nonce", new WorkerHelloCapacity(4, 8_000_000_000), [Run], 41),
        new WorkerWelcome("nonce", 40, new RunWorkerSettings(new MemoryFigure(2048, "computed", false), 1024)),
        new WorkerRefused("This worker is version 0.0.0 and control is version 1.2.3; a worker must run control's version."),
        new CommandFrame(7, new CancelRun(Run)),
        new AppliedFrame(7, null),
        new EventFrame(new WorkerEnvelope(new WorkerId("w1"), 42, new RunOutput(Run, "out"))),
        new HandledFrame(42),
        new PingFrame(3),
        new PongFrame(3),
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

    private static StartRun Start() =>
        new(Run, "agent", "system", "prompt", "context", "/work", new Dictionary<string, string> { ["A"] = "1" }, null,
            new RunLaunch("cli", ["-p"], null, null, null, true, null, null, 600, []), null, "/tmp", null,
            Credential: new RunCredential(
                CredentialSource.Issued, new Dictionary<string, string> { ["ISSUED_KEY"] = Value }, ["OTHER"], ["THIRD"], true, null),
            Redaction: ValueRedactor.For([Value]),
            CredentialNames: ["ISSUED_KEY"]);
}
