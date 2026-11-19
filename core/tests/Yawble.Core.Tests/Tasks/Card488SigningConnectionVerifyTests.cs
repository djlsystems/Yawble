using Yawble.Core.Connections;
using Yawble.Core.Tasks;
using Xunit;

namespace Yawble.Core.Tests.Tasks;

/// <summary>
/// Card 488 (verify B077, Marlo's card 486): a release host task that takes SIGNING_DLIB and SIGNTOOL from a Connection is saved
/// through the real HostTaskService (Validate + ValidateBindings against a Connection), and a run would read both values from it;
/// the old YAWBLE_SIGNING_DLIB / YAWBLE_SIGNTOOL bound the same way are still refused. Fakes only: the in-memory credential store
/// of <see cref="TaskFixture"/>; nothing runs, no signtool.
/// </summary>
public class Card488SigningConnectionVerifyTests
{
    private const string Dlib = @"D:\tools\signing\Azure.CodeSigning.Dlib.dll";
    private const string Signtool = @"D:\tools\sdk\x64\signtool.exe";

    private static long AddSigningConnection(TaskFixture f) =>
        f.Conns!.Save(null, "Signing tools", [new ConnectionField("dlib", "Dlib path", true, null), new ConnectionField("signtool", "Signtool path", true, null)],
            new Dictionary<string, string> { ["dlib"] = Dlib, ["signtool"] = Signtool }).Id;

    private static HostTaskDef Release(TaskFixture f, params EnvBinding[] bindings) => f.Def("release") with { EnvFromConnections = bindings };

    [Fact]
    public async Task A_release_task_binding_SIGNING_DLIB_and_SIGNTOOL_from_a_Connection_is_saved_and_reads_both()
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
    public async Task Both_new_names_from_the_apps_environment_are_saved_too()
    {
        using var f = new TaskFixture(withConnections: true);
        await f.SaveAsync(f.Def("release", env: ["SIGNING_DLIB", "SIGNTOOL"]));
        Assert.Equal(["SIGNING_DLIB", "SIGNTOOL"], f.Svc.Store.Task(f.TeamId, "release")!.Def.EnvNames.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("YAWBLE_SIGNING_DLIB", "dlib")]
    [InlineData("YAWBLE_SIGNTOOL", "signtool")]
    public async Task The_old_names_bound_from_the_same_Connection_are_refused_and_nothing_is_saved(string name, string field)
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
