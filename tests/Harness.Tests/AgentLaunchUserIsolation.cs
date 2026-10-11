using System.Runtime.CompilerServices;

namespace Harness.Tests;

/// <summary>
/// A test Host launches its children as the test process's own user, wherever the suite runs.
/// The release runs the suite as root inside the product image, which has an `agent` user; left
/// alone, every test Host would switch its git and agents to `agent`, and git as `agent`
/// could not remove the worktrees the root-run test had created. The suite must not depend on
/// which users the image happens to carry. A test that exercises the real switch names its
/// user itself (`AgentLaunchUser.Resolve("nobody")`), which this does not touch.
/// </summary>
internal static class AgentLaunchUserIsolation
{
    public const string NoSuchUser = "harness-tests-no-such-user";

    [ModuleInitializer]
    internal static void Isolate() =>
        Environment.SetEnvironmentVariable("HARNESS_AGENT_USER", NoSuchUser);
}
