using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A member that prints a large file must not have the Host hold it: each stream keeps its head
/// and its tail, with a marker between saying how much was dropped.
/// </summary>
public sealed class BoundedCaptureTests
{
    [Fact]
    public void Output_under_the_limit_is_kept_whole_and_unmarked()
    {
        var capture = new BoundedCapture(headLimit: 8, tailLimit: 8);

        Append(capture, "hello, world");

        Assert.Equal("hello, world", capture.ToString());
        Assert.Equal(0, capture.Dropped);
    }

    [Fact]
    public void Huge_output_keeps_head_and_tail_and_does_not_grow_memory()
    {
        var capture = new BoundedCapture(headLimit: 1024, tailLimit: 1024);
        var chunk = new char[4096];
        Array.Fill(chunk, 'x');

        Append(capture, "HEAD-START");

        // 64 MiB of characters through the same 4 KiB read buffer the pump uses. Measured on this
        // thread alone, so tests running beside it cannot move the figure.
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 16 * 1024; i++) capture.Append(chunk, 0, chunk.Length);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Append(capture, "TAIL-END");

        Assert.True(allocated < 64 * 1024, $"appending 64 MiB allocated {allocated} bytes");

        var text = capture.ToString();
        Assert.StartsWith("HEAD-START", text);
        Assert.EndsWith("TAIL-END", text);
        Assert.Contains("[harness] ", text);
        Assert.True(text.Length < 1024 + 1024 + 200, $"kept {text.Length} characters");
        Assert.Equal(10 + 64L * 1024 * 1024 + 8 - 2048, capture.Dropped);
    }

    [Fact]
    public void A_chunk_larger_than_the_tail_keeps_only_its_own_end()
    {
        var capture = new BoundedCapture(headLimit: 2, tailLimit: 4);

        Append(capture, "ab");
        Append(capture, "cd");
        Append(capture, "0123456789");

        Assert.Equal(8, capture.Dropped);
        Assert.StartsWith("ab", capture.ToString());
        Assert.EndsWith("6789", capture.ToString());
    }

    private static void Append(BoundedCapture capture, string text) =>
        capture.Append(text.ToCharArray(), 0, text.Length);
}

/// <summary>The same bound, through a real child process.</summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class BoundedRunOutputTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), $"harness-bounded-test-{Guid.NewGuid():N}");

    public BoundedRunOutputTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task A_child_printing_a_large_file_is_capped_with_head_and_tail()
    {
        var bin = Path.Combine(_directory, "bin");
        Directory.CreateDirectory(bin);
        await TestExecutable.WriteAsync(
            Path.Combine(bin, "grok"),
            "#!/bin/sh\nprintf 'HEAD-START\\n'\nhead -c 20000000 /dev/zero | tr '\\0' 'x'\nprintf '\\nTAIL-END\\n'\n");

        using var restore = new EnvironmentScope(
            [new("PATH", bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"))]);

        var runner = new ProcessAgentRunner(
            new AgentCatalog([new AgentDefinition("grok", AgentMode.Headless, new AgentLaunch("grok", []))]),
            new RunHeartbeat());

        var result = await runner.RunAsync(
            new AgentInvocation(
                new ContainerId("Alpha", "DeveloperRowan"), "You are a member.", "print a big file",
                _directory, new Dictionary<string, string>(), Agent: "grok"),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("HEAD-START", result.Output);
        Assert.Contains("TAIL-END", result.Output[^100..]);
        Assert.Contains("characters of output were not kept", result.Output);
        Assert.True(
            result.Output.Length < 2 * BoundedCapture.DefaultHalf + 4096,
            $"kept {result.Output.Length} characters of a 20 MB print");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
