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

    [Theory]
    [InlineData("log", "--oneline", "-5")]
    [InlineData("diff", "HEAD~1")]
    [InlineData("grep", "foo")]
    [InlineData("grep", "-n", "-e", "FOO")]
    [InlineData("show", "HEAD")]
    [InlineData("blame", "file")]
    [InlineData("branch", "-v")]
    [InlineData("branch", "-a", "-vv")]
    [InlineData("branch", "--list", "team/*")]
    [InlineData("diff", "--text", "HEAD~1")]
    [InlineData("log", "-SOutput", "--stat")]
    [InlineData("log", "--no-merges", "--name-only")]
    [InlineData("log", "--", "--output=x")] // after "--" it is a path
    public void OrdinaryReadsStillRun(params string[] args) => Assert.Equal(Decisions.Allow, Decide(args));

    [Theory]
    [InlineData("difftool", "HEAD~1")] // not "diff"
    [InlineData("show-branch")] // not "show"
    [InlineData("show-ref")]
    [InlineData("grepx", "foo")]
    [InlineData("log-tree")]
    [InlineData("branch", "-v", "newname")] // a name after -v creates a branch
    public void AnAllowedCommandIsItsWholeWord_AndListingBranchesTakesNoName(params string[] args) => Assert.Equal(Decisions.Ask, Decide(args));

    [Theory]
    // a program of the caller's choice
    [InlineData("grep", "--open-files-in-pager=calc", "foo")]
    [InlineData("grep", "--open-files-in-pager", "foo")]
    [InlineData("grep", "--open=calc", "foo")] // git accepts a shortened long option
    [InlineData("grep", "-Ocalc", "foo")]
    [InlineData("grep", "-O", "foo")]
    [InlineData("grep", "-nOcalc", "foo")]
    [InlineData("difftool", "--extcmd=calc", "HEAD~1")]
    [InlineData("difftool", "--extcmd", "calc")]
    [InlineData("difftool", "-x", "calc", "HEAD~1")]
    [InlineData("difftool", "-yx", "calc")]
    [InlineData("show", "--ext-diff", "HEAD")]
    [InlineData("log", "-p", "--textconv")]
    [InlineData("rebase", "--exec", "calc", "main")]
    [InlineData("ls-remote", "--upload-pack=calc", "origin")]
    [InlineData("push", "--receive-pack=calc", "origin")]
    // a file written anywhere
    [InlineData("log", "--output=/home/me/.profile", "-1")]
    [InlineData("diff", "--output", "x", "HEAD~1")]
    [InlineData("show", "HEAD", "--output=x")]
    [InlineData("diff", "-Oorderfile", "HEAD~1")]
    // files read outside the repository
    [InlineData("diff", "--no-index", "/data/cloud-link.json", "/dev/null")]
    [InlineData("grep", "--no-index", "secret", "/")]
    [InlineData("blame", "--contents", "/data/cloud-link.json", "README.md")]
    [InlineData("blame", "--contents=/data/cloud-link.json", "README.md")]
    // a branch deleted, renamed or moved behind a listing option
    [InlineData("branch", "-v", "-D", "x")]
    [InlineData("branch", "-vD", "x")]
    [InlineData("branch", "-v", "-d", "x")]
    [InlineData("branch", "-v", "-m", "a", "b")]
    [InlineData("branch", "-v", "-M", "a", "b")]
    [InlineData("branch", "-v", "--delete", "x")]
    [InlineData("branch", "-a", "--move", "a", "b")]
    [InlineData("branch", "--list", "-f", "main", "HEAD~3")]
    public void OptionsThatRunAProgramOrReachOtherFilesAreNeverRun_WhateverThePersonAllows(params string[] args)
    {
        var allowAll = new CliRule[] { new("*", Decisions.Allow, "team"), new(string.Join(' ', args) + "*", Decisions.Allow, "team") };
        var (decision, rule) = CliRules.Decide(args, CliCatalog.Of("git"), allowAll, allowAll);
        Assert.Equal(Decisions.Deny, decision);
        Assert.Equal("floor", rule!.Layer);
    }
}
