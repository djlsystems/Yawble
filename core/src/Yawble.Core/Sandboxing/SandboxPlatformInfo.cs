using System.Reflection;
using Microsoft.Mxc.Sdk.V1;

namespace Yawble.Core.Sandboxing;

/// <summary>The sandbox SDK calls the shell can reach, behind an interface so IPC tests can fake them.</summary>
public interface ISandboxPlatformInfo
{
    string SdkVersion { get; }
    string NativeVersion { get; }
    IReadOnlyList<AvailableBackend> GetAvailableBackends();
    PlatformSupport GetPlatformSupport();
}

public sealed class SandboxPlatformInfo : ISandboxPlatformInfo
{
    /// <summary>The Microsoft.Mxc.Sdk package version, from the managed assembly.</summary>
    public static string ManagedSdkVersion { get; } =
        typeof(MxcPlatform).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? typeof(MxcPlatform).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    public string SdkVersion => ManagedSdkVersion;
    public string NativeVersion => MxcPlatform.NativeVersion;
    public IReadOnlyList<AvailableBackend> GetAvailableBackends() => MxcPlatform.GetAvailableBackends();
    public PlatformSupport GetPlatformSupport() => MxcPlatform.GetPlatformSupport();
}
