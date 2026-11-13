using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Yawble.Core.Launch;
using Yawble.Core.Sandboxing;
using Yawble.Core.Policy;
using Xunit;

namespace Yawble.Core.Tests.Launch;

/// <summary>
/// The real sandbox of THIS host, the way the core launches it (LaunchWiring): wxc-exec.exe on Windows (beside
/// the test assembly, copied there by the core's project from .toolchain/), the native mxc_ffi on Linux and macOS.
/// Real-sandbox tests use <see cref="Backend"/> and <see cref="SkipUnlessAvailable"/>, and build a probe's command line
/// with <see cref="Script"/> in this OS's own shell.
/// </summary>
internal static class RealSandboxHost
{
    private static readonly Lazy<string?> s_refusal = new(Probe);

    public static TargetOs Os => TargetOsExtensions.Host;

    /// <summary>The product's backend on this OS (LaunchWiring.cs).</summary>
    public static ISandboxBackend Backend() => OperatingSystem.IsWindows()
        ? new WxcExecBackend(Path.Combine(AppContext.BaseDirectory, "wxc-exec.exe"),
            Directory.CreateTempSubdirectory("yawble-requests-").FullName)
        : new NativeSandboxBackend();

    /// <summary>The backend's run-record name ("wxc-exec" on Windows, "yawble-ffi" elsewhere).</summary>
    public static string BackendName => OperatingSystem.IsWindows() ? "wxc-exec" : "yawble-ffi";

    /// <summary>Null when the sandbox starts a proxy-mode sandbox on this host (the mode every agent run uses); else why not.</summary>
    public static string? Refusal => s_refusal.Value;

    public static void SkipUnlessAvailable()
    {
        if (Refusal is { } why) Assert.Skip(why);
    }

    /// <summary>
    /// A probe command line in this OS's shell: /bin/sh -c on Linux and macOS; on Windows, Windows PowerShell 5.1
    /// (System32, readable in a ProcessContainer) with the script as -EncodedCommand (Git Bash cannot start in a
    /// ProcessContainer, microsoft/mxc#1061).
    /// </summary>
    public static string Script(TargetOs os, string posix, string powershell) => os == TargetOs.Windows
        ? CommandLine.Join(os, [@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", "-NoProfile", "-NonInteractive",
            "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(powershell))])
        : CommandLine.Join(os, ["/bin/sh", "-c", posix]);

    /// <summary>
    /// A minimal Windows ProcessContainer request in the compiler's Windows shape (UI limits, drive-root enumeration,
    /// identity-less proxy mode on <paramref name="proxyPort"/>): <paramref name="cwd"/> is the only read/write grant.
    /// </summary>
    public static JsonObject WindowsRequest(string commandLine, string cwd, int proxyPort, int timeoutMs = 60_000)
    {
        var root = Path.GetPathRoot(cwd)!.ToUpperInvariant();
        return new JsonObject
        {
            ["version"] = SandboxPin.RequestContract,
            ["containment"] = "processcontainer",
            ["containerId"] = "yawble-test-" + Guid.NewGuid().ToString("N")[..8],
            ["process"] = new JsonObject
            {
                ["commandLine"] = commandLine, ["cwd"] = cwd, ["timeout"] = timeoutMs,
                ["env"] = new JsonArray($"SYSTEMROOT={SandboxRequestCompiler.WindowsSystemRoot}", $"WINDIR={SandboxRequestCompiler.WindowsSystemRoot}",
                    $@"PATH={SandboxRequestCompiler.WindowsSystemRoot}\System32;{SandboxRequestCompiler.WindowsSystemRoot}",
                    $"LOCALAPPDATA={cwd}", $"APPDATA={cwd}", $"USERPROFILE={cwd}", $"TEMP={cwd}", $"TMP={cwd}"),
            },
            ["filesystem"] = new JsonObject { ["readonlyPaths"] = new JsonArray(), ["readwritePaths"] = new JsonArray(cwd), ["deniedPaths"] = new JsonArray() },
            ["network"] = new JsonObject { ["egress"] = new JsonObject { ["default"] = "deny" }, ["ingress"] = new JsonObject { ["default"] = "allow", ["hostLoopback"] = "allow" } },
            ["runtimeConfig"] = new JsonObject { ["networkProxy"] = $"http://127.0.0.1:{proxyPort}" },
            ["ui"] = new JsonObject { ["disable"] = false, ["clipboard"] = "none", ["injection"] = false },
            ["processContainer"] = new JsonObject
            {
                ["ui"] = SandboxRequestCompiler.WindowsUiLimits(),
                ["filesystem"] = new JsonObject { ["enumeratePaths"] = new JsonArray(root) },
            },
            ["fallback"] = new JsonObject { ["allowDaclMutation"] = false },
            ["telemetry"] = new JsonObject { ["enabled"] = false },
        };
    }

    private static string? Probe()
    {
        if (OperatingSystem.IsMacOS()) return "the real-sandbox tests have not been run on macOS yet";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        JsonObject request;
        if (OperatingSystem.IsWindows())
            request = WindowsRequest("cmd.exe /c exit 0", Directory.CreateTempSubdirectory("yawble-probe-").FullName, port);
        else
        {
            request = new JsonObject
            {
                ["version"] = SandboxPin.RequestContract,
                ["containment"] = "bubblewrap",
                ["process"] = new JsonObject { ["commandLine"] = "/bin/true", ["cwd"] = "/", ["env"] = new JsonArray("PATH=/usr/bin:/bin"), ["timeout"] = 60_000 },
                ["filesystem"] = new JsonObject { ["readonlyPaths"] = new JsonArray(), ["readwritePaths"] = new JsonArray(), ["deniedPaths"] = new JsonArray() },
                ["network"] = new JsonObject { ["egress"] = new JsonObject { ["default"] = "deny" }, ["ingress"] = new JsonObject { ["default"] = "deny", ["hostLoopback"] = "deny" } },
                ["runtimeConfig"] = new JsonObject { ["networkProxy"] = $"http://127.0.0.1:{port}" },
                ["telemetry"] = new JsonObject { ["enabled"] = false },
            };
        }
        try
        {
            using var p = Backend().Spawn(request.ToJsonString());
            var deadline = DateTime.UtcNow.AddSeconds(30);
            SandboxExit? exit;
            while ((exit = p.TryWait()) is null && DateTime.UtcNow < deadline) Thread.Sleep(20);
            if (exit is null) { p.Kill(); return "the proxy-mode sandbox did not finish its probe in 30 s"; }
            return exit.Value.ExitCode == 0 ? null : $"the probe exited {exit.Value.ExitCode} in the proxy-mode sandbox";
        }
        catch (SandboxStartException e)
        {
            return "the sandbox refused a proxy-mode request: " + e.Message;
        }
        catch (DllNotFoundException e)
        {
            return "libmxc_ffi is not loadable: " + e.Message;
        }
    }
}
