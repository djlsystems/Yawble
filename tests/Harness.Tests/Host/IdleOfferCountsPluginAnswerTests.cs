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
/// The check of the `IdleWorkflowOffer.UndeliveredAsync` arm: a plugin's answer
/// naming the member that asked (`answersTell`) counts as still coming to that member, though it
/// holds neither `completed` nor `failed`, so the idle offer is not made in the moment between the
/// Manager's cursor passing the answer and the asker's. Reached through the public
/// `IdleWorkflowOffer.OnRunEndingAsync` on the real Host, with the team paused so the pump holds
/// the asker's cursor below the answer for certain, as
/// `TriggerCostControlTests.A_quiet_run_ending_before_the_pump_moves_its_cursor_is_still_declared` does.
/// </summary>
public sealed class IdleOfferCountsPluginAnswerTests : IAsyncLifetime
{
    private const string Email = "person@example.test";
    private const string Password = "correct horse battery";

    private readonly string _dataRoot = Path.Combine(Path.GetTempPath(), $"harness-idle-answer-{Guid.NewGuid():N}");
    private readonly FakeAgent _agents = new();
    private WebApplicationFactory<Program> _factory = null!;
    private string _team = "";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private IServiceProvider Services => _factory.Services;

    private IMessageLog Log => Services.GetRequiredService<IMessageLog>();

    private ContainerId Dev => new(_team, "Dev");

    private ContainerId Echo => new(_team, "Echo");

    private ContainerId Manager => new(_team, TeamRegistry.DefaultManagerName);

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_dataRoot);
        PluginMemberEndToEndTests.InstallSampleEcho(_dataRoot);

        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(host => host
            .UseSetting("DataRoot", _dataRoot)
            .UseSetting("Logging:LogLevel:Default", "Warning")
            .ConfigureTestServices(services => services.AddSingleton<IAgentRunner>(_agents)));

        _team = (await Services.GetRequiredService<TeamRegistry>()
            .CreateAsync("IdleAnswer", "claude-headless", memberAgent: "claude-headless", ct: Ct)).Id;

        await Services.GetRequiredService<IUserStore>().CreateAsync(Email, Password);
        using var person = _factory.CreateClient();
        (await person.PostAsJsonAsync("/api/auth/login", new { email = Email, password = Password }, Ct)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync($"/api/teams/{_team}/containers", new
        {
            name = "Echo", agent = "plugin:sample-echo", config = new { mode = "upper" },
        }, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await person.PostAsJsonAsync(
            $"/api/teams/{_team}/containers", new { name = "Dev", agent = "claude-headless" }, Ct)).StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        await _factory.DisposeAsync();
        try { Directory.Delete(_dataRoot, recursive: true); }
        catch (IOException) { }
    }

    /// <summary>
    /// Writes, with the team paused: a person's instruction to Dev (the workflow's root, which Dev
    /// has run), Dev's tell to Echo, and Echo's completed row answering it - naming Dev when
    /// <paramref name="namesDev"/>. Every cursor but Dev's is moved past the answer, as the pump moves
    /// the Manager's past a row it passes over. Answers the root.
    /// </summary>
    private async Task<Message> AnswerOnTheLogAsync(bool namesDev)
    {
        var host = Services.GetRequiredService<ContainerHost>();
        var cursors = Services.GetRequiredService<ICursors>();
        await host.SetPausedAsync(_team, true);

        var root = await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Dev), JsonSerializer.Serialize(new { instruction = "drive Echo" }), "console"), Ct);
        await cursors.AdvanceAsync(Dev, root.Seq, Ct);

        var tell = await Log.AppendAsync(new NewMessage(
            MessageTypes.InstructionFor(Echo), JsonSerializer.Serialize(new { instruction = "first" }), Dev.ToString(), root.Seq), Ct);

        var payload = namesDev
            ? $"{{\"output\":\"FIRST\",\"{PayloadFields.AnswersTell}\":{JsonSerializer.Serialize(Dev.ToString())}}}"
            : "{\"output\":\"FIRST\"}";
        var answer = await Log.AppendAsync(new NewMessage(MessageTypes.Completed, payload, Echo.ToString(), tell.Seq), Ct);
        Assert.Equal(root.CorrelationId, answer.CorrelationId);

        await cursors.AdvanceAsync(Echo, answer.Seq, Ct);
        await cursors.AdvanceAsync(Manager, answer.Seq, Ct);
        Assert.True(await cursors.PositionAsync(Dev, Ct) < answer.Seq);
        return root;
    }

    [Fact]
    public async Task An_answer_still_coming_to_the_member_that_asked_keeps_the_idle_offer_from_being_made()
    {
        var root = await AnswerOnTheLogAsync(namesDev: true);

        var offered = await Services.GetRequiredService<IdleWorkflowOffer>()
            .OnRunEndingAsync(Dev, root.Seq, succeeded: true, Ct);

        Assert.False(offered);
        Assert.DoesNotContain(await Log.ReadCorrelationAsync(root.CorrelationId, Ct), IdleWorkflowOffer.IsOffer);
    }

    /// <summary>THE CONTROL: the same log with an answer naming nobody is offered, so the test above
    /// reaches the undelivered check and nothing earlier refused it.</summary>
    [Fact]
    public async Task The_same_answer_naming_nobody_is_offered()
    {
        var root = await AnswerOnTheLogAsync(namesDev: false);

        var offered = await Services.GetRequiredService<IdleWorkflowOffer>()
            .OnRunEndingAsync(Dev, root.Seq, succeeded: true, Ct);

        Assert.True(offered);
        Assert.Single(await Log.ReadCorrelationAsync(root.CorrelationId, Ct), IdleWorkflowOffer.IsOffer);
    }
}
