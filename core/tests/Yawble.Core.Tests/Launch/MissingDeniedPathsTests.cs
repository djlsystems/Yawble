using System.Text.Json.Nodes;
using Yawble.Core.Launch;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// A new Windows profile has no ~/.config, and the sandbox cannot start a run that denies ~/.config/gh there: the denied
/// paths that do not exist leave the request before it reaches wxc-exec. The existence check is a fake; no file is read.
/// </summary>
public sealed class MissingDeniedPathsTests
{
    private const string Request = """
        {"version":"1.0.0","filesystem":{"readonlyPaths":["C:\\ro-missing"],"readwritePaths":["C:\\rw"],
         "deniedPaths":["C:\\Users\\a\\.ssh","C:\\Users\\a\\.config\\gh","C:\\Users\\a\\Documents"]},"process":{"env":["K=V"]}}
        """;

    [Fact]
    public void TheMissingDeniedPathsLeave_TheRestOfTheRequestStays()
    {
        var existing = new HashSet<string> { @"C:\Users\a\Documents" };
        var json = JsonNode.Parse(WxcExecBackend.WithoutMissingDenied(Request, existing.Contains))!;

        Assert.Equal([@"C:\Users\a\Documents"], json["filesystem"]!["deniedPaths"]!.AsArray().Select(n => n!.GetValue<string>()));
        // Grants are not touched: a missing grant is the sandbox's own error to report.
        Assert.Equal([@"C:\ro-missing"], json["filesystem"]!["readonlyPaths"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("K=V", json["process"]!["env"]![0]!.GetValue<string>());
        Assert.Equal("1.0.0", json["version"]!.GetValue<string>());
    }

    [Fact]
    public void WhenEveryDeniedPathExists_TheRequestIsTheSameText()
    {
        Assert.Same(Request, WxcExecBackend.WithoutMissingDenied(Request, _ => true));
    }
}
