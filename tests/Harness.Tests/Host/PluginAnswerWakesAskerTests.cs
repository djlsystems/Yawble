using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Harness.Containers;
using Harness.Contracts;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Harness.Tests.Host;

/// <summary>
/// A PLUGIN'S ANSWER WAKES THE MEMBER THAT ASKED, NOT THE MANAGER. A member steps through its own
/// team's plugin one command at a time: it tells the plugin, ends its run, and is woken by the
/// answer in the same workflow - no wake_me, and no Manager run to learn that the member reads the
/// answer itself. A failed answer wakes the asker too; a `needs-person` failure still reaches the
/// Manager (<see cref="Harness.Tests.PluginAnswerWakeRulesTests"/>); a plugin answering the
/// Manager's own `tell` still wakes the Manager.
///
/// On the real Host, with the real sample-echo plugin, and the member's tells sent through the real
/// `tell` route with a member's own key from inside its run.
/// </summary>
public sealed class PluginAnswerWakesAskerTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private static readonly HashSet<string> MemberPermits =
        new(StringComparer.Ordinal) { Permits.Progress, Permits.Skills, Permits.Read, Permits.Sites };

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-plugin-answer-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private readonly ConcurrentQueue<string> _devPrompts = new();
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _person = null!;
    private HttpClient _dev = null!;
    private HttpClient _manager = null!;
    private string _team = "";

    /// <summary>What Dev tells Echo on each of its runs, in order; past the end it tells nothing.</summary>
    private string[] _devSteps = [];

    /// <summary>What the Manager tells Echo on its first run; null tells nothing.</summary>
    private string? _managerStep;

    private int _devRun;
    private int _managerRun;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private ContainerId Echo => new(_team, "Echo");

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _agents.Behaviour = async invocation =>
        {
            if (invocation.Container == Dev)
            {
                _devPrompts.Enqueue(invocation.Prompt);
                var step = Interlocked.Increment(ref _devRun) - 1;
                if (step < _devSteps.Length) await TellEchoAsync(_dev, _devSteps[step]);
            }
            else if (invocation.Container == Manager && Interlocked.Increment(ref _managerRun) == 1 && _managerStep is { } told)
            {
                await TellEchoAsync(_manager, told);
            }

            return new AgentResult(0, $"noted by {invocation.Container.Name}");
        };

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("Answers", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        _person = _factory.CreateClient();
        (await _person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo", agent = "plugin:sample-echo", config = new { mode = "upper" },
        }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct)).StatusCode);

        _dev = await KeyedClientAsync(Dev, MemberPermits);
        _manager = await KeyedClientAsync(Manager, new HashSet<string>(MemberPermits, StringComparer.Ordinal) { Permits.Tell });
    }

    public async ValueTask DisposeAsync()
    {
        _person.Dispose();
        _dev.Dispose();
        _manager.Dispose();
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    private async Task<HttpClient> KeyedClientAsync(ContainerId who, IReadOnlySet<string> permits)
    {
        var key = await Services.GetRequiredService<IPrincipalStore>().MintAsync(
            who.ToString(), PrincipalKind.Container, _team, new HashSet<string>(permits, StringComparer.Ordinal), ct: Ct);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", key);
        return client;
    }

    /// <summary>A tell from inside a run, naming no causation: the route puts it in the run's workflow.</summary>
    private async Task TellEchoAsync(HttpClient client, string instruction)
    {
        var response = await client.PostAsJsonAsync($"/api/teams/{_team}/containers/Echo/tell", new { instruction }, Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    private async Task<IReadOnlyList<Message>> TerminalRowsOfAsync(ContainerId who) =>
        (await Log.ReadAfterAsync(0, [MessageTypes.Completed, MessageTypes.Failed], int.MaxValue, Ct))
            .Where(m => m.Source == who.ToString())
            .ToList();

    private async Task AwaitAsync(Func<Task<bool>> until, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (await until()) return;
            await Task.Delay(50, Ct);
        }

        throw new TimeoutException($"Never: {what}.");
    }

    /// <summary>Waits until every member is idle and every cursor has read the whole log, several
    /// polls running: after this, any wake the log was going to cause has happened.</summary>
    private async Task SettleAsync()
    {
        var host = Services.GetRequiredService<ContainerHost>();
        var cursors = Services.GetRequiredService<ICursors>();
        var deadline = DateTime.UtcNow.AddSeconds(30);

        for (var quiet = 0; quiet < 8 && DateTime.UtcNow < deadline;)
        {
            var head = await Log.HighestSeqAsync(Ct);
            var settled = true;
            foreach (var who in new[] { Manager, Dev, Echo })
            {
                settled &= host.Find(who)!.State == ContainerState.Idle
                    && host.Find(who)!.QueueDepth == 0
                    && await cursors.PositionAsync(who, Ct) >= head;
            }

            quiet = settled ? quiet + 1 : 0;
            await Task.Delay(100, Ct);
        }
    }

    private static string? AnswersTell(Message row) =>
        JsonDocument.Parse(row.Payload).RootElement.TryGetProperty(PayloadFields.AnswersTell, out var value)
            ? value.GetString()
            : null;

    [Fact]
    public async Task A_member_steps_through_a_plugin_three_times_woken_by_each_answer_and_the_manager_never_runs_for_them()
    {
        _devSteps = ["first", "second", "third"];

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/Dev/tell", new { instruction = "drive Echo" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        // Dev's first run asks, and each answer wakes it for the next: four runs in all.
        await AwaitAsync(async () => (await TerminalRowsOfAsync(Dev)).Count >= 4, "Dev's fourth run");
        await SettleAsync();

        var echo = await TerminalRowsOfAsync(Echo);
        var dev = await TerminalRowsOfAsync(Dev);
        var manager = await TerminalRowsOfAsync(Manager);

        // Three answers, each naming Dev, each the answer to the step Dev told after reading the last.
        Assert.Equal(3, echo.Count);
        Assert.All(echo, row => Assert.Equal(MessageTypes.Completed, row.Type));
        Assert.All(echo, row => Assert.Equal(Dev.ToString(), AnswersTell(row)));
        // Dev's first run was the person's tell; its next three were Echo's answers, in order, all
        // in the one workflow. A run after those is the platform's idle offer to the workflow's owner
        // once nobody is working it, as for any workflow a member owns - never a wake Dev set.
        var personsTell = dev[0].CausationSeq;
        var stepping = dev.Where(row => row.CausationSeq == personsTell || echo.Any(answer => answer.Seq == row.CausationSeq)).ToList();
        Assert.Equal(4, stepping.Count);
        Assert.Equal(echo.Select(row => (long?)row.Seq), stepping.Skip(1).Select(row => row.CausationSeq));
        Assert.Single(dev.Concat(echo).Select(row => row.CorrelationId).Distinct());
        Assert.Equal(dev.Count, _agents.RunsFor(Dev));
        foreach (var other in dev.Except(stepping))
        {
            var cause = (await Log.ReadCorrelationAsync(other.CorrelationId, Ct)).Single(row => row.Seq == other.CausationSeq);
            Assert.Equal(MessageTypes.InstructionFor(Dev), cause.Type);
            Assert.Equal("host", cause.Source);
        }

        // Each answer reached Dev's prompt: it read the last before telling the next.
        var prompts = _devPrompts.ToArray();
        Assert.Contains("FIRST", prompts[1], StringComparison.Ordinal);
        Assert.Contains("SECOND", prompts[2], StringComparison.Ordinal);
        Assert.Contains("THIRD", prompts[3], StringComparison.Ordinal);

        // No wake was set: nothing was waited on but the answers.
        Assert.DoesNotContain(await Log.ReadCorrelationAsync(dev[0].CorrelationId, Ct),
            row => row.Source.StartsWith("wake:", StringComparison.Ordinal));

        // The Manager ran ZERO times for the answers: no run of its was woken by an Echo row. It is
        // still woken by each of Dev's own run endings, exactly as before.
        var echoSeqs = echo.Select(row => (long?)row.Seq).ToHashSet();
        Assert.DoesNotContain(manager, row => echoSeqs.Contains(row.CausationSeq));
        Assert.Equal(dev.Count, manager.Count);
        Assert.Equal(dev.Count, _agents.RunsFor(Manager));
        Assert.Equal(dev.Select(row => (long?)row.Seq).Order(), manager.Select(row => row.CausationSeq).Order());
    }

    [Fact]
    public async Task A_failed_plugin_answer_wakes_the_member_that_asked_and_not_the_manager()
    {
        _devSteps = ["fail:the page is gone"];

        var told = await _person.PostAsJsonAsync($"/api/teams/{_team}/containers/Dev/tell", new { instruction = "drive Echo" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        await AwaitAsync(async () => (await TerminalRowsOfAsync(Dev)).Count >= 2, "Dev's second run");
        await SettleAsync();

        var failed = Assert.Single(await TerminalRowsOfAsync(Echo));
        Assert.Equal(MessageTypes.Failed, failed.Type);
        Assert.Equal(Dev.ToString(), AnswersTell(failed));

        var dev = await TerminalRowsOfAsync(Dev);
        Assert.Equal(failed.Seq, dev[1].CausationSeq);
        Assert.DoesNotContain(await TerminalRowsOfAsync(Manager), row => row.CausationSeq == failed.Seq);
    }

    [Fact]
    public async Task A_plugin_answering_the_managers_own_tell_still_wakes_the_manager()
    {
        _managerStep = "for the manager";

        var told = await _person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers/{TeamRegistry.DefaultManagerName}/tell", new { instruction = "ask Echo" }, Ct);
        Assert.True(told.IsSuccessStatusCode, await told.Content.ReadAsStringAsync(Ct));

        await AwaitAsync(async () => (await TerminalRowsOfAsync(Manager)).Count >= 2, "the Manager's second run");
        await SettleAsync();

        var answer = Assert.Single(await TerminalRowsOfAsync(Echo));
        Assert.Equal(MessageTypes.Completed, answer.Type);
        Assert.Equal(Manager.ToString(), AnswersTell(answer));
        Assert.Single(await TerminalRowsOfAsync(Manager), row => row.CausationSeq == answer.Seq);
        Assert.Equal(0, _agents.RunsFor(Dev));
    }
}
