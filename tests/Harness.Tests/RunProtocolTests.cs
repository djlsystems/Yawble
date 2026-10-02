using System.Text.Json;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// The run protocol's records are plain data: each one travels as JSON and comes back the same, and
/// a run's result crosses as its output, usage and end and is put back together unchanged.
/// </summary>
public sealed class RunProtocolTests
{
    private static readonly RunId Run = new(new ContainerId("alpha", "worker"), "n1");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static IReadOnlyList<ControlMessage> EveryControlMessage() =>
    [
        new StartRun(
            Run, "agent", "system", "prompt", "context", "/work",
            new Dictionary<string, string> { ["A"] = "1" },
            "/gone",
            new RunLaunch(
                "cli", ["-p", "{userPrompt}"], ["--system", "{systemPromptFile}"], "AGENTS.md", "fmt", true,
                new Dictionary<string, string> { ["ISOLATED"] = "1" },
                new Dictionary<string, string> { ["UPDATES"] = "off" },
                600,
                ["FORCE_COLOR", "OTHER_KEY"]),
            new RunMemoryAllowance(new MemoryFigure(2048, "set", true), new MemoryFigure(4096, "ceiling", false)),
            "/data/tmp",
            new RunLiveView(null, LiveViewNames.CodexRollout, new AgentLiveViewFind("~/s", "*/x.jsonl", LiveViewNames.CwdFromFirstLine))),
        new CancelRun(Run),
        new ChangeRunMemoryAllowance(["alpha/worker", "beta/other"]),
        new HoldIdleClock(Run.Member, true),
        new TouchIdleClock(Run.Member),
        new SampleCapacity(),
    ];

    private static readonly DateTimeOffset At = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static IReadOnlyList<WorkerEvent> EveryWorkerEvent() =>
    [
        new WorkerReady(WorkerId.Local),
        new RunStarted(Run, 42, At),
        new RunProgress(Run, "held"),
        new RunOutput(Run, "out"),
        new RunUsage(Run, new UsageFigures(7, 11, null, "src", 3, 2, 1)),
        new RunMeasured(Run, 4242, 3, 1024, 99, At),
        new RunChildStoppedByMemoryLimit(Run, "stopped", new MemoryFigure(64, "set", true)),
        new RunLiveViewChanged(Run, "/home/t.jsonl", LiveViewNames.ClaudeJsonl, "", false),
        new RunDiagnostic(Run, DiagnosticSeverity.Warning, "kind", "source", "message", "{}", "System.Exception"),
        new RunEnded(Run, 1, 42, "error", FailureClasses.Interrupted, At, "tail", new AgentTranscript("/t", "fmt"),
            new RunFault(false, "System.IO.IOException", "disk")),
        new WorkerCapacitySampled(
            At,
            new CapacityFigures(
                "v2", 2.5, false, 1, 2, 3, 4096, false, 100, 50, 40, 10,
                new PressureReading(new PressureLines(1, 2, 3, 4), null),
                new PressureReading(new PressureLines(5, 6, 7, 8), new PressureLines(9, 10, 11, 12)),
                5, 100, false, ["cpu.stat"]),
            [Run]),
    ];

    [Fact]
    public void Every_protocol_record_round_trips_through_json_unchanged()
    {
        var controls = EveryControlMessage();
        var events = EveryWorkerEvent();

        // A record added to the protocol without a case here fails, so none travels untested.
        Assert.Equal(Concrete<ControlMessage>(), controls.Select(m => m.GetType()).OrderBy(t => t.Name));
        Assert.Equal(Concrete<WorkerEvent>(), events.Select(e => e.GetType()).OrderBy(t => t.Name));

        foreach (var message in controls)
        {
            var json = JsonSerializer.Serialize(message, Json);
            var back = JsonSerializer.Deserialize<ControlMessage>(json, Json)!;

            Assert.Equal(message.GetType(), back.GetType());
            Assert.Equal(json, JsonSerializer.Serialize(back, Json));
        }

        foreach (var @event in events)
        {
            var envelope = new WorkerEnvelope(WorkerId.Local, 7, @event);
            var json = JsonSerializer.Serialize(envelope, Json);
            var back = JsonSerializer.Deserialize<WorkerEnvelope>(json, Json)!;

            Assert.Equal(@event.GetType(), back.Event.GetType());
            Assert.Equal(7, back.Seq);
            Assert.Equal(json, JsonSerializer.Serialize(back, Json));
        }
    }

    [Fact]
    public void An_agent_result_crosses_as_output_usage_and_end_and_comes_back_equal()
    {
        // Every field the result has. A field added to AgentResult fails this until the protocol
        // carries it, or says why it does not.
        Assert.Equal(
            ["ExitCode", "Output", "LaunchError", "Usage", "ProcessId", "ReachedThePlatform", "FailureClass", "RetryAfter", "AgentTranscript"],
            typeof(AgentResult).GetConstructors().Single().GetParameters().Select(p => p.Name));

        foreach (var usage in new InvocationUsage?[]
                 {
                     new InvocationUsage(7, 11, "src", 3, 2, 1),
                     InvocationUsage.Combined(14038, "total"),
                     InvocationUsage.NoModel,
                     null,
                 })
        {
            var result = new AgentResult(
                3, "the output", "it failed", usage, 42,
                FailureClass: FailureClasses.Rate,
                RetryAfter: At,
                AgentTranscript: new AgentTranscript("/home/t.jsonl", LiveViewNames.ClaudeJsonl));

            var (output, carried, ended) = RunResults.Events(Run, result);
            var outputBack = RoundTrip<WorkerEvent>(output) as RunOutput;
            var usageBack = carried is null ? null : RoundTrip<WorkerEvent>(carried) as RunUsage;
            var endedBack = RoundTrip<WorkerEvent>(ended) as RunEnded;

            Assert.Equal(result, RunResults.Result(outputBack!.Text, usageBack?.Usage, endedBack!));
        }
    }

    [Fact]
    public void Whether_a_run_reached_the_platform_is_not_carried()
    {
        var (_, _, ended) = RunResults.Events(Run, new AgentResult(0, "ok", ReachedThePlatform: false));

        Assert.Null(RunResults.Result("ok", null, ended).ReachedThePlatform);
    }

    private static T RoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Json), Json)!;

    private static IEnumerable<Type> Concrete<T>() =>
        typeof(T).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(T)) && !t.IsAbstract).OrderBy(t => t.Name);
}
