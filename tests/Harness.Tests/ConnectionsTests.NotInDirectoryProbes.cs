using System.Net;
using System.Web;
using Harness.Contracts;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// THE EDGES OF THE NOT-IN-THE-DIRECTORY READ. A code is read only whole, in Microsoft's error or its
/// description; a longer code that starts or ends with one is not it. The admin-approval codes keep their
/// own sentence, and say it first when both come together. Only Microsoft's refusal is read so: another
/// provider's refusal carrying the same text is said as before.
/// </summary>
public sealed partial class ConnectionsTests
{
    [Theory]
    [InlineData("AADSTS50034", null)]
    [InlineData(null, "AADSTS50034: The user account does not exist in the directory.")]
    [InlineData("invalid_grant", "Trace first. AADSTS90072: The external account doesn't exist.")]
    [InlineData("access_denied", "(AADSTS50020)")]
    public void A_whole_code_in_the_error_or_the_description_is_read_as_not_in_the_directory(string? error, string? description)
    {
        Assert.True(ConnectionProviders.NotInDirectory(error, description));
    }

    [Theory]
    [InlineData("AADSTS500200")]
    [InlineData("AADSTS500341")]
    [InlineData("AADSTS900720")]
    [InlineData("AADSTS150020")]
    [InlineData("XAADSTS50034")]
    [InlineData("AADSTS5003")]
    [InlineData("AADSTS51004")]
    public void A_code_inside_a_longer_code_or_another_code_is_not_read_as_not_in_the_directory(string entra)
    {
        Assert.False(ConnectionProviders.NotInDirectory(entra, null));
        Assert.False(ConnectionProviders.NotInDirectory("invalid_grant", $"{entra}: Something else."));
    }

    [Theory]
    [InlineData("AADSTS500200")]
    [InlineData("AADSTS500341")]
    public async Task A_code_sign_in_refused_with_a_longer_code_is_not_said_as_not_in_the_directory(string entra)
    {
        await SetUpMicrosoftAsync();

        var flowId = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite(entra);

        var sentence = (await FlowAsync(flowId, until: "refused")).GetProperty("sentence").GetString();
        Assert.NotEqual(ConnectionProviders.MicrosoftNotInDirectory, sentence);
        Assert.Contains(entra, sentence);
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Theory]
    [InlineData("AADSTS65001")]
    [InlineData("AADSTS90094")]
    [InlineData("AADSTS90095")]
    [InlineData("AADSTS900941")]
    [InlineData("AADSTS90099")]
    public async Task Every_admin_approval_code_still_says_the_admin_sentence_even_beside_a_not_in_directory_code(string admin)
    {
        await SetUpMicrosoftAsync();

        var alone = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite(admin);
        Assert.Equal(ConnectionProviders.MicrosoftAdminApproval, (await FlowAsync(alone, until: "refused")).GetProperty("sentence").GetString());

        var both = (await StartDeviceAsync()).GetProperty("flowId").GetString()!;
        _provider.DeviceAnswers.Writer.TryWrite($"{admin} AADSTS50020");
        Assert.Equal(ConnectionProviders.MicrosoftAdminApproval, (await FlowAsync(both, until: "refused")).GetProperty("sentence").GetString());
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Fact]
    public async Task Another_providers_exchange_refused_with_the_same_text_is_not_said_as_not_in_the_directory()
    {
        _provider.ExchangeRefusal = "AADSTS50034";

        var state = (await StartAsync([MailScope])).GetProperty("state").GetString()!;
        var location = await CallbackLocationAsync(state);

        Assert.Contains("connection=refused", location);
        var reason = HttpUtility.ParseQueryString(new Uri(new Uri("http://host"), location).Query)["reason"];
        Assert.NotEqual(ConnectionProviders.MicrosoftNotInDirectory, reason);
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
    }

    [Theory]
    [MemberData(nameof(NotInDirectoryCodes))]
    public async Task A_browser_sign_in_whose_redirect_carries_the_code_only_in_its_description_says_the_sentence(string entra)
    {
        await SetUpAdvancedMicrosoftAsync();

        var state = (await StartBrowserAsync(tenant: null)).GetProperty("state").GetString()!;
        var url = $"{Connections.CallbackPath}?state={Uri.EscapeDataString(state)}&error=access_denied"
            + $"&error_description={Uri.EscapeDataString($"{entra}: Selected user account does not exist in tenant. Trace ID: 0000")}";
        using var response = await _anonymous.GetAsync(url, Ct);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        _responses.Add(location);

        Assert.Contains("connection=refused", location);
        var reason = HttpUtility.ParseQueryString(new Uri(new Uri("http://host"), location).Query)["reason"];
        Assert.Equal(ConnectionProviders.MicrosoftNotInDirectory, reason);
        Assert.DoesNotContain("AADSTS", location);
        Assert.DoesNotContain("Trace", location);
        Assert.Empty((await GetAsync("/api/connections")).EnumerateArray());
        Assert.DoesNotContain(TenantActions.ConnectionConnected, await TenantActionsAsync());
    }
}
