using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Xunit;
using Yawble.Core.Ipc;

namespace Yawble.Core.Tests;

/// <summary>
/// The Windows pipe the core listens on lets in the account that runs it and nobody else. The access list is read
/// from the live pipe (through a connected client), for the first instance and for the one made after an accept.
/// </summary>
public class IpcPipeAccessTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task OnWindows_EveryPipeInstance_AllowsOnlyTheCurrentUser()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("the named pipe is Windows's (the Unix socket's file mode has its own test)");
            return;
        }
        await using var endpoint = IpcEndpoint.Create(IpcEndpoint.NewAddress(Path.GetTempPath()));

        await using var first = await ConnectAsync(endpoint.Address);
        AssertCurrentUserOnly(first);
        await using var served = await endpoint.AcceptAsync(Ct).WaitAsync(Limit, Ct);

        await using var second = await ConnectAsync(endpoint.Address); // the instance made while the first is in use
        AssertCurrentUserOnly(second);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertCurrentUserOnly(NamedPipeClientStream pipe)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;
        // Inside an AppContainer the pipe also lets in that one container (Windows checks such a caller twice).
        var container = PipeAcl.ContainerSid(identity);

        var security = pipe.GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<PipeAccessRule>().ToList();
        Assert.NotEmpty(rules);
        Assert.All(rules, rule =>
        {
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.False(rule.IsInherited);
            var sid = (SecurityIdentifier)rule.IdentityReference;
            Assert.True(sid == user || sid == container, $"the pipe lets in {sid} ({rule.PipeAccessRights})");
        });
        Assert.Contains(rules, rule => (SecurityIdentifier)rule.IdentityReference == user && rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl));
        Assert.Equal(container is null ? 1 : 2, rules.Count);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<NamedPipeClientStream> ConnectAsync(string address)
    {
        var name = address[(address.LastIndexOf('\\') + 1)..];
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(Limit, Ct);
        return pipe;
    }
}
