using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Yawble.Core.Ipc;

/// <summary>
/// The DACL of a pipe only this account can open: what PipeOptions.CurrentUserOnly gives (the current user, full
/// control), plus, only when this process itself runs in an AppContainer, that same container's SID. Windows checks an
/// AppContainer caller twice, once as the user and once as the container, so a pipe that grants only the user refuses
/// even a client in the very container that created it, and the creator itself when it opens the next instance ("Access
/// to the path is denied"); so the container gets read, write and create-instance. Outside an AppContainer the
/// DACL is exactly CurrentUserOnly's; inside one it lets in that container's processes and no other container.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class PipeAcl
{
    public static PipeSecurity CurrentUserOnly()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        if (ContainerSid(identity) is { } container)
            security.AddAccessRule(new PipeAccessRule(container, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance | PipeAccessRights.Synchronize, AccessControlType.Allow));
        return security;
    }

    /// <summary>The AppContainer SID of the token, or null when it is not an AppContainer token.</summary>
    internal static SecurityIdentifier? ContainerSid(WindowsIdentity identity)
    {
        const int TokenAppContainerSid = 31;
        GetTokenInformation(identity.Token, TokenAppContainerSid, IntPtr.Zero, 0, out var needed);
        if (needed <= 0) return null;
        var buffer = Marshal.AllocHGlobal(needed);
        try
        {
            if (!GetTokenInformation(identity.Token, TokenAppContainerSid, buffer, needed, out _)) return null;
            var sid = Marshal.ReadIntPtr(buffer); // TOKEN_APPCONTAINER_INFORMATION: one PSID, null outside a container
            return sid == IntPtr.Zero ? null : new SecurityIdentifier(sid);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
}
