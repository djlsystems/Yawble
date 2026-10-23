using System.Net;
using System.Text.Json;
using System.Web;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// AN ACCOUNT MICROSOFT DOES NOT FIND IN THE DIRECTORY THE SIGN-IN USED. Each Entra code that means so
/// ends the sign-in - with a code, in the browser, or through the operator CLI's exchange - as
/// <c>refused</c> with <see cref="ConnectionProviders.MicrosoftNotInDirectory"/>: never the code, never
/// Microsoft's own words, and nothing stored. A Microsoft code that runs out says the same after the
/// expiry sentence, since Microsoft's own page turns such an account away without telling the Host;
/// any other provider's expiry is said as before.
/// </summary>
public sealed partial class ConnectionsTests
{
    public static TheoryData<string> NotInDirectoryCodes => ["AADSTS50020", "AADSTS50034", "AADSTS90072"];

    [Theory]
    [MemberData(nameof(NotInDirectoryCodes))]
    public async Task A_code_sign_in_refused_as_not_in_the_directory_settles_refused_with_the_sentence_and_stores_nothing(string entra)
    {
        await SetUpMicrosoftAsync();

        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite(entra);

        var refused = await FlowAsync(flowId, until: "refused");
        AssertTheSentence(refused.GetProperty("sentence").GetString());
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.DoesNotContain(TenantActions.ConnectionConnected, await TenantActionsAsync());

        // The operator CLI reads the same flow through its exchange, in the same words.
        var flowId2 = (await ExchangeAsync(new { op = "start", provider = "microsoft", scopes = new[] { GraphMail }, flow = "device" }))
            .GetProperty("start").GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite(entra);
        AssertTheSentence((await ExchangeFlowAsync(flowId2, until: "refused")).GetProperty("sentence").GetString());
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(NotInDirectoryCodes))]
    public async Task A_browser_sign_in_whose_exchange_is_refused_as_not_in_the_directory_is_refused_with_the_sentence_and_stores_nothing(string entra)
    {
        await SetUpAdvancedMicrosoftAsync();
        _provider.ExchangeRefusal = entra;

        var state = (await StartBrowserAsync(tenant: null)).GetProperty("state").GetString()!;
        var location = await CallbackLocationAsync(state);

        Assert.Contains("connection=refused", location);
        AssertTheSentence(HttpUtility.ParseQueryString(new Uri(new Uri("http://host"), location).Query)["reason"]);
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.DoesNotContain(TenantActions.ConnectionConnected, await TenantActionsAsync());
    }

    [Theory]
    [MemberData(nameof(NotInDirectoryCodes))]
    public async Task The_operator_clis_browser_sign_in_refused_as_not_in_the_directory_answers_the_sentence_and_stores_nothing(string entra)
    {
        await SetUpAdvancedMicrosoftAsync();
        var folder = Path.Combine(_dataRoot, ConnectRequests.Folder);
        await WaitUntilAsync(() => Directory.Exists(folder));

        // Refused at the token exchange.
        _provider.ExchangeRefusal = entra;
        var exchanged = await StartLoopbackAsync("http://127.0.0.1:53121/");
        var completed = await ExchangeAsync(new { op = "complete", state = exchanged, code = FakeProvider.GoodCode });
        Assert.Equal(400, completed.GetProperty("status").GetInt32());
        AssertTheSentence(completed.GetProperty("error").GetString());

        // Refused on Microsoft's redirect to the CLI's listener, which tells the Host instead of a code.
        var redirected = await StartLoopbackAsync("http://127.0.0.1:53122/");
        var told = await ExchangeAsync(new
        {
            op = "complete", state = redirected, error = "invalid_request",
            errorDescription = $"{entra}: The account was refused by the directory. Trace ID: 0000",
        });
        Assert.Equal(400, told.GetProperty("status").GetInt32());
        AssertTheSentence(told.GetProperty("error").GetString());

        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Fact]
    public async Task A_microsoft_code_that_expires_says_where_to_name_the_directory_and_any_other_expiry_does_not()
    {
        await SetUpMicrosoftAsync();

        // Run out on the clock.
        _provider.DeviceExpiresIn = 12;
        var first = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("pending");
        _provider.DeviceAnswers.Writer.TryWrite("pending");
        AssertMicrosoftExpiry((await FlowAsync(first, until: "expired")).GetProperty("sentence").GetString());

        // And as Microsoft says it.
        _provider.DeviceExpiresIn = 900;
        var second = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("expired_token");
        AssertMicrosoftExpiry((await FlowAsync(second, until: "expired")).GetProperty("sentence").GetString());

        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());

        // Google signs in through the browser only; its sign-in that ran out says nothing of a directory.
        var late = await StartAsync([MailScope]);
        _clock.Advance(TimeSpan.FromMinutes(11));
        var location = await CallbackLocationAsync(late.GetProperty("state").GetString()!);
        var reason = HttpUtility.ParseQueryString(new Uri(new Uri("http://host"), location).Query)["reason"]!;
        Assert.Contains("more than 10 minutes old", reason);
        Assert.DoesNotContain("Directory", reason);
        Assert.DoesNotContain(ConnectionProviders.MicrosoftExpiredNotInDirectory, reason);

        // Any provider but Microsoft keeps the expiry sentence alone, were its code to run out.
        var google = ConnectionProviders.Resolve(ConnectionProviders.Google, null);
        Assert.Equal(Connections.ExpiredSentence, Connections.ExpiredSentenceFor(google));
    }

    [Fact]
    public async Task A_refusal_needing_an_admin_still_says_the_admin_approval_sentence()
    {
        await SetUpMicrosoftAsync();

        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite("AADSTS90094");

        var refused = await FlowAsync(flowId, until: "refused");
        Assert.Equal(ConnectionProviders.MicrosoftAdminApproval, refused.GetProperty("sentence").GetString());
    }

    [Theory]
    [InlineData("AADSTS500200")]
    [InlineData("AADSTS50177")]
    [InlineData("AADSTS500208")]
    public void A_code_the_list_does_not_confirm_is_not_read_as_not_in_the_directory(string entra)
    {
        Assert.False(ConnectionProviders.NotInDirectory("invalid_grant", $"{entra}: Something else."));
    }

    private async Task<string> StartLoopbackAsync(string redirectUri)
    {
        var started = await ExchangeAsync(new { op = "start", provider = "microsoft", scopes = new[] { GraphMail }, redirectUri });
        Assert.Equal(200, started.GetProperty("status").GetInt32());
        return started.GetProperty("start").GetProperty("state").GetString()!;
    }

    /// <summary>The sentence, word for word: no Entra code, none of Microsoft's own words.</summary>
    private static void AssertTheSentence(string? sentence)
    {
        Assert.Equal(ConnectionProviders.MicrosoftNotInDirectory, sentence);
        Assert.DoesNotContain("AADSTS", sentence);
        Assert.DoesNotContain("Trace ID", sentence);
        Assert.DoesNotContain("refused by the directory", sentence);
    }

    /// <summary>The expiry sentence, then on its own line where to name the directory.</summary>
    private static void AssertMicrosoftExpiry(string? sentence)
    {
        Assert.Equal($"{Connections.ExpiredSentence}\n{ConnectionProviders.MicrosoftExpiredNotInDirectory}", sentence);
        Assert.Contains("username may be incorrect", sentence);
        Assert.Contains("under Directory", sentence);
    }
}
