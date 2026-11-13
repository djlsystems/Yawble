using Yawble.Core.Integrations;
using Xunit;

namespace Yawble.Core.Tests.Integrations;

public class LoginTerminalTests
{
    private const string Home = @"C:\Users\someone";

    [Fact]
    public void KnownCli_OpensItsLoginCommand_InItsFolder()
    {
        var start = LoginTerminal.Build(@"C:\Program Files\GitHub CLI\gh.exe", CliCatalog.Of("gh")!, Home);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), start.FileName);
        Assert.Contains(@"/D ""C:\Program Files\GitHub CLI""", start.Arguments);
        Assert.Contains("/k", start.Arguments);
        Assert.Contains("auth login", start.Arguments);
        Assert.Contains(@"""""C:\Program Files\GitHub CLI\gh.exe"" auth login""", start.Arguments);
        Assert.Equal(@"C:\Program Files\GitHub CLI", start.WorkingDirectory);
        Assert.Equal("gh auth login", start.Command);
    }

    [Fact]
    public void UnknownCli_OpensAPlainTerminal()
    {
        var start = LoginTerminal.Build(@"C:\Tools\foo.exe", CliCatalog.Generic("foo"), Home);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), start.FileName);
        Assert.DoesNotContain("/k", start.Arguments);
        Assert.Null(start.Command);
        Assert.Contains(@"/D ""C:\Tools""", start.Arguments);
    }

    [Fact]
    public void NoPath_StartsInHome()
    {
        var start = LoginTerminal.Build(null, CliCatalog.Generic("foo"), Home);
        Assert.Equal(Home, start.WorkingDirectory);
        Assert.Contains($@"/D ""{Home}""", start.Arguments);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("^")]
    [InlineData("%")]
    public void APathWithShellCharactersIsRefused(string bad)
    {
        var e = Assert.Throws<ArgumentException>(() => LoginTerminal.Build($@"C:\Program Files\a{bad}b\gh.exe", CliCatalog.Of("gh")!, Home));
        Assert.Contains("cannot be opened in a terminal safely", e.Message);
    }
}
