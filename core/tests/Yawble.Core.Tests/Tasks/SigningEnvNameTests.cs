using Yawble.Core.Connections;
using Yawble.Core.Tasks;
using Xunit;

namespace Yawble.Core.Tests.Tasks;

/// <summary>
/// The signing tool paths packaging/windows-signing.cjs reads (SIGNING_DLIB, SIGNTOOL) can be given to a release host
/// task, from the app's environment or from a Connection; the same names with the app's own YAWBLE_ prefix are refused.
/// The validation tests run nothing; the tests through the service save a task against the in-memory credential store
/// of <see cref="TaskFixture"/> and start nothing, no signtool.
/// </summary>
public class SigningEnvNameTests
{
    private const string Dlib = @"D:\tools\signing\Azure.CodeSigning.Dlib.dll";
    private const string Signtool = @"D:\tools\sdk\x64\signtool.exe";

    private static HostTaskDef Release(IEnumerable<string>? envNames = null, IEnumerable<EnvBinding>? bindings = null) =>
        HostTaskDef.Make("release", "pwsh", ["-NoProfile", "-File", "scripts/release.ps1"], timeoutSeconds: 3600,
            envNames: envNames, envFromConnections: bindings);

    private static long AddSigningConnection(TaskFixture f) =>
        f.Conns!.Save(null, "Signing tools", [new ConnectionField("dlib", "Dlib path", true, null), new ConnectionField("signtool", "Signtool path", true, null)],
            new Dictionary<string, string> { ["dlib"] = Dlib, ["signtool"] = Signtool }).Id;

    private static HostTaskDef Release(TaskFixture f, params EnvBinding[] bindings) => f.Def("release") with { EnvFromConnections = bindings };

    // ------------------------------------------------------------------ validation

    [Theory]
    [InlineData("SIGNING_DLIB")]
    [InlineData("SIGNTOOL")]
    public void TheSigningNames_AreNotTheAppsOwn(string name)
    {
        Assert.False(HostTaskRules.IsCredentialName(name));
        Assert.False(HostTaskRules.IsRuntimeName(name));
    }

    [Fact]
    public void ATaskThatPassesBothFromTheAppsEnvironment_IsValid() =>
        Assert.Null(Record.Exception(() => HostTaskRules.Validate(Release(envNames: ["SIGNING_DLIB", "SIGNTOOL"]))));

    [Fact]
    public void ATaskThatTakesBothFromAConnection_IsValid() =>
        Assert.Null(Record.Exception(() => HostTaskRules.Validate(
            Release(bindings: [new EnvBinding("SIGNING_DLIB", "1", "dlib", ""), new EnvBinding("SIGNTOOL", "1", "signtool", "")]))));

    [Theory]
    [InlineData("YAWBLE_SIGNING_DLIB")]
    [InlineData("YAWBLE_SIGNTOOL")]
    public void TheNamesWithTheAppsPrefix_AreRefusedEitherWay(string name)
    {
        Assert.True(HostTaskRules.IsCredentialName(name));
        Assert.Contains("app's own", Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(Release(envNames: [name]))).Message);
        Assert.Contains("app's own", Assert.Throws<ArgumentException>(() =>
            HostTaskRules.Validate(Release(bindings: [new EnvBinding(name, "1", "dlib", "")]))).Message);
    }

    // ------------------------------------------------------------------ through the service

    [Fact]
    public async Task AReleaseTaskBindingBothFromAConnection_IsSaved_AndARunWouldReadBoth()
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddSigningConnection(f);
        var saved = await f.SaveAsync(Release(f, new EnvBinding("SIGNING_DLIB", id.ToString(), "dlib", ""), new EnvBinding("SIGNTOOL", id.ToString(), "signtool", "")));

        var stored = f.Svc.Store.Task(f.TeamId, "release")!.Def;
        Assert.Equal(saved.Id, f.Svc.Store.Task(f.TeamId, "release")!.Id);
        Assert.Equal(["SIGNING_DLIB", "SIGNTOOL"], stored.EnvFromConnections.Select(b => b.Name).Order(StringComparer.Ordinal));
        Assert.All(stored.EnvFromConnections, b => Assert.Equal("Signing tools", b.ConnectionName));

        var (values, problems) = TaskProcess.ReadBindings(stored.EnvFromConnections, f.Conns);
        Assert.Empty(problems);
        Assert.Equal(Dlib, values["SIGNING_DLIB"]);
        Assert.Equal(Signtool, values["SIGNTOOL"]);
    }

    [Fact]
    public async Task BothNamesFromTheAppsEnvironment_AreSaved()
    {
        using var f = new TaskFixture(withConnections: true);
        await f.SaveAsync(f.Def("release", env: ["SIGNING_DLIB", "SIGNTOOL"]));
        Assert.Equal(["SIGNING_DLIB", "SIGNTOOL"], f.Svc.Store.Task(f.TeamId, "release")!.Def.EnvNames.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("YAWBLE_SIGNING_DLIB", "dlib")]
    [InlineData("YAWBLE_SIGNTOOL", "signtool")]
    public async Task TheNamesWithTheAppsPrefix_BoundFromAConnection_AreRefused_AndNothingIsSaved(string name, string field)
    {
        using var f = new TaskFixture(withConnections: true);
        var id = AddSigningConnection(f);
        var e = await Assert.ThrowsAsync<ArgumentException>(() => f.SaveAsync(Release(f, new EnvBinding(name, id.ToString(), field, ""))));
        Assert.Contains("app's own", e.Message);
        Assert.DoesNotContain(Dlib, e.Message);
        Assert.Null(f.Svc.Store.Task(f.TeamId, "release"));

        await Assert.ThrowsAsync<ArgumentException>(() => f.SaveAsync(f.Def("release", env: [name])));
        Assert.Null(f.Svc.Store.Task(f.TeamId, "release"));
    }
}
