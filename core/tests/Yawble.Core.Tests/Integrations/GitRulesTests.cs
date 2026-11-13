using Yawble.Core.Integrations;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

/// <summary>git on the host reads the person's checkout and never changes it; nothing reaches a credential or a config.</summary>
public class GitRulesTests
{
    private static string Decide(params string[] args) => CliRules.Decide(args, CliCatalog.Of("git"), [], []).Decision;

    [Theory]
    [InlineData("rev-parse", "HEAD")]
    [InlineData("log", "--oneline", "-5")]
    [InlineData("status", "--short")]
    [InlineData("show", "--stat", "HEAD")]
    [InlineData("diff", "main...team/alpha")]
    [InlineData("branch")]
    [InlineData("branch", "--list")]
    [InlineData("merge-base", "--is-ancestor", "a", "b")]
    [InlineData("--version")]
    public void ReadingRuns(params string[] args) => Assert.Equal(Decisions.Allow, Decide(args));

    [Theory]
    [InlineData("push", "origin", "main")]
    [InlineData("commit", "-m", "x")]
    [InlineData("checkout", "main")]
    [InlineData("reset", "--hard")]
    [InlineData("merge", "team/alpha")]
    [InlineData("fetch", "origin")]
    [InlineData("branch", "-D", "x")]
    [InlineData("clean", "-fdx")]
    [InlineData("tag", "v1")]
    public void ChangingTheCheckoutIsRefused(params string[] args) => Assert.Equal(Decisions.Deny, Decide(args));

    [Theory]
    [InlineData("config", "--get", "remote.origin.url")]
    [InlineData("credential", "fill")]
    [InlineData("-c", "core.sshCommand=evil", "log")]
    [InlineData("log", "-c", "x")]
    [InlineData("--git-dir=/elsewhere", "log")]
    [InlineData("-C", "C:/other", "status")]
    public void ConfigAndCredentialsCannotBeReached(params string[] args) => Assert.Equal(Decisions.Deny, Decide(args));

    [Fact]
    public void AnythingElseAsksThePerson() => Assert.Equal(Decisions.Ask, Decide("count-objects", "-v"));
}
