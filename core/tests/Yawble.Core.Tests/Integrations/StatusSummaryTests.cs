using Yawble.Core.Integrations;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>A CLI's sign-in status in a few words, also when its status command prints JSON (az account show).</summary>
public class StatusSummaryTests
{
    [Fact]
    public void AzAccountShow_IsTheSubscriptionAndTheUser()
    {
        const string az = """
            {
              "environmentName": "AzureCloud",
              "id": "00000000-0000-0000-0000-000000000000",
              "isDefault": true,
              "name": "Visual Studio Enterprise",
              "state": "Enabled",
              "user": { "name": "person@example.com", "type": "user" }
            }
            """;
        Assert.Equal("Visual Studio Enterprise (person@example.com)", IntegrationService.StatusSummary(az));
    }

    [Theory]
    [InlineData("github.com\n  ✓ Logged in to github.com")]
    [InlineData("")]
    [InlineData("{ not json")]
    [InlineData("{}")]
    public void AnythingElse_FallsBackToTheFirstLine(string stdout) => Assert.Null(IntegrationService.StatusSummary(stdout));
}
