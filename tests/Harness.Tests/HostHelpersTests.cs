using System.Security.Claims;
using System.Text.Json;
using Harness.Host;
using Harness.Host.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

namespace Harness.Tests;

/// <summary>
/// One copy each of what the routes said in many places: the JSON options a response is written
/// with, the refusal for a missing team or member, the caller's two claims, and a configured interval.
/// </summary>
public sealed class HostHelpersTests
{
    private enum Shade { Light, Dark }

    [Fact]
    public void One_options_instance_serialises_enums_as_strings_and_is_read_only()
    {
        Assert.Same(HostJson.WebEnums, HostJson.WebEnums);
        Assert.Equal("""{"shade":"Dark"}""", JsonSerializer.Serialize(new { shade = Shade.Dark }, HostJson.WebEnums));
        Assert.Equal("""{"shade":1}""", JsonSerializer.Serialize(new { shade = Shade.Dark }, HostJson.Web));
        Assert.Throws<InvalidOperationException>(() => HostJson.WebEnums.PropertyNameCaseInsensitive = false);
        Assert.Throws<InvalidOperationException>(() => HostJson.Web.PropertyNameCaseInsensitive = false);
    }

    [Fact]
    public void The_refusals_answer_exactly_what_the_routes_answered()
    {
        Assert.Equal(Body(Results.NotFound(new { error = "No team 'alpha'." })), Body(Refusals.NoTeam("alpha")));
        Assert.Equal(Body(Results.NotFound(new { error = "No member 'Dev'." })), Body(Refusals.NoMember("Dev")));
        Assert.IsAssignableFrom<IStatusCodeHttpResult>(Refusals.NoTeam("alpha"));
        Assert.Equal(404, ((IStatusCodeHttpResult)Refusals.NoTeam("alpha")).StatusCode);
    }

    [Fact]
    public void The_actor_claims_are_the_id_and_the_email_or_null()
    {
        var signedIn = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "u1"), new Claim(ClaimTypes.Email, "p@example.test")]));

        Assert.Equal(("u1", "p@example.test"), ActorClaims.Of(signedIn));
        Assert.Equal((null, null), ActorClaims.Of(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public void An_interval_is_read_from_the_configuration_or_falls_back()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Sweep"] = "00:00:30",
            ["Broken"] = "soon",
            ["Zero"] = "00:00:00",
            ["Off"] = "false",
            ["Garbled"] = "maybe",
        }).Build();

        Assert.Equal(TimeSpan.FromSeconds(30), Intervals.Read(configuration, "Sweep", TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromMinutes(1), Intervals.Read(configuration, "Broken", TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromMinutes(1), Intervals.Read(configuration, "Missing", TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.Zero, Intervals.Read(configuration, "Zero", TimeSpan.FromMinutes(1)));
        Assert.Equal(TimeSpan.FromDays(7), Intervals.ReadPositive(configuration, "Zero", TimeSpan.FromDays(7)));
        Assert.True(Intervals.Enabled(configuration, "Missing"));
        Assert.False(Intervals.Enabled(configuration, "Off"));
        Assert.True(Intervals.Enabled(configuration, "Garbled"));
    }

    private static string Body(IResult result) => JsonSerializer.Serialize(((IValueHttpResult)result).Value, HostJson.Web);
}
