namespace Yawble.Core.Policy;

/// <summary>The OS a request is compiled for. Golden files compile every OS on any host.</summary>
public enum TargetOs
{
    Windows,
    MacOS,
    Linux,
}

public static class TargetOsExtensions
{
    public static TargetOs Host =>
        OperatingSystem.IsWindows() ? TargetOs.Windows
        : OperatingSystem.IsMacOS() ? TargetOs.MacOS
        : TargetOs.Linux;

    /// <summary>The default sandbox backend on each OS, named explicitly in every request.</summary>
    public static string Containment(this TargetOs os) => os switch
    {
        TargetOs.Windows => "processcontainer",
        TargetOs.MacOS => "seatbelt",
        _ => "bubblewrap",
    };

    public static string Slug(this TargetOs os) => os switch
    {
        TargetOs.Windows => "windows",
        TargetOs.MacOS => "macos",
        _ => "linux",
    };
}
