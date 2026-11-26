using Xunit;

namespace Yawble.Core.Tests;

/// <summary>The core's start-up options: the window's token comes in a variable, which is removed as soon as it is read.</summary>
public class CoreOptionsTests
{
    private static readonly string Token = new('a', 40);

    [Fact]
    public void TheTokenIsRead_AndItsVariableIsRemovedFromTheProcess()
    {
        var env = new Dictionary<string, string?> { [CoreOptions.TokenVariable] = Token, ["PATH"] = "/bin" };
        var options = CoreOptions.Parse(["--ipc", "x", "--data-dir", "d"], n => env.GetValueOrDefault(n), (n, v) => { if (v is null) env.Remove(n); else env[n] = v; });
        Assert.Null(options.Error);
        Assert.Equal(Token, options.Token);
        Assert.False(env.ContainsKey(CoreOptions.TokenVariable));
        Assert.Equal("/bin", env["PATH"]); // nothing else is touched
    }

    [Fact]
    public void TheVariableIsRemoved_AlsoWhenTheStartIsRefused()
    {
        var env = new Dictionary<string, string?> { [CoreOptions.TokenVariable] = Token };
        var options = CoreOptions.Parse(["--data-dir", "d"], n => env.GetValueOrDefault(n), (n, v) => { if (v is null) env.Remove(n); else env[n] = v; });
        Assert.NotNull(options.Error);
        Assert.Empty(env);
    }

    [Fact]
    public void TheVariableHasTheNameTheShellUses() => Assert.Equal("YAWBLE_IPC_TOKEN", CoreOptions.TokenVariable);
}
