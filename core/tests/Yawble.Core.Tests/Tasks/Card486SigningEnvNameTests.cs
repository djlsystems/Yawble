using Yawble.Core.Tasks;
using Xunit;

namespace Yawble.Core.Tests.Tasks;

/// <summary>
/// B077: the signing tool paths packaging/windows-signing.cjs reads under their new names (SIGNING_DLIB, SIGNTOOL) can be given to a
/// release host task, from the app's environment or from a Connection; the old YAWBLE_ names stay refused (HostTaskRules is unchanged).
/// Pure validation: no task runs.
/// </summary>
public class Card486SigningEnvNameTests
{
    private static HostTaskDef Release(IEnumerable<string>? envNames = null, IEnumerable<EnvBinding>? bindings = null) =>
        HostTaskDef.Make("release", "pwsh", ["-NoProfile", "-File", "scripts/release.ps1"], timeoutSeconds: 3600,
            envNames: envNames, envFromConnections: bindings);

    [Theory]
    [InlineData("SIGNING_DLIB")]
    [InlineData("SIGNTOOL")]
    public void The_new_names_are_not_the_apps_own(string name)
    {
        Assert.False(HostTaskRules.IsCredentialName(name));
        Assert.False(HostTaskRules.IsRuntimeName(name));
    }

    [Fact]
    public void A_task_can_pass_both_from_the_apps_environment() =>
        HostTaskRules.Validate(Release(envNames: ["SIGNING_DLIB", "SIGNTOOL"]));

    [Fact]
    public void A_task_can_take_both_from_a_Connection() =>
        HostTaskRules.Validate(Release(bindings: [new EnvBinding("SIGNING_DLIB", "1", "dlib", ""), new EnvBinding("SIGNTOOL", "1", "signtool", "")]));

    [Theory]
    [InlineData("YAWBLE_SIGNING_DLIB")]
    [InlineData("YAWBLE_SIGNTOOL")]
    public void The_old_names_are_still_refused_either_way(string name)
    {
        Assert.True(HostTaskRules.IsCredentialName(name));
        Assert.Contains("app's own", Assert.Throws<ArgumentException>(() => HostTaskRules.Validate(Release(envNames: [name]))).Message);
        Assert.Contains("app's own", Assert.Throws<ArgumentException>(() =>
            HostTaskRules.Validate(Release(bindings: [new EnvBinding(name, "1", "dlib", "")]))).Message);
    }
}
