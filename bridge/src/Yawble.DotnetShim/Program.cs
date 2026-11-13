using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Yawble.DotnetShim;

/// <summary>
/// `dotnet` inside a Windows sandbox. In a ProcessContainer a named pipe with an ordinary name can be created but never
/// opened (sandbox release 1.0.1, microsoft/mxc#1706), and MSBuild's worker nodes are reached through exactly such pipes: a
/// parallel build fails ("Build FAILED", 0 errors) and leaves idle workers that keep the agent's shell open. MSBuild
/// takes its worker count only from the command line (`dotnet` itself passes a bare -maxcpucount, which beats any
/// Directory.Build.rsp), so this wrapper, first on the sandbox's PATH, runs the real dotnet with -m:1 for the commands
/// that build. Everything else passes through unchanged.
/// Not covered (they reach pipes without passing a command line through here): `dotnet watch` (it starts the real
/// dotnet itself), a full path to the real dotnet.exe, and other tools that use ordinary named pipes. The wrapper is
/// needed only while microsoft/mxc#1706 stands; RealSandboxDotnetBuildTests' "ordinary pipes fail" check fails once it is fixed.
/// </summary>
public static partial class Program
{
    /// <summary>The dotnet commands that run MSBuild and take -m.</summary>
    internal static readonly HashSet<string> Building = new(StringComparer.OrdinalIgnoreCase)
    {
        "build", "test", "run", "pack", "publish", "clean", "restore", "msbuild", "build-server", "store",
    };

    public static int Main(string[] args)
    {
        var real = RealDotnet();
        if (real is null)
        {
            Console.Error.WriteLine("dotnet: the .NET SDK was not found on PATH or in Program Files");
            return 1;
        }
        var psi = new ProcessStartInfo(real) { UseShellExecute = false };
        foreach (var a in Rewrite(args)) psi.ArgumentList.Add(a);
        using var process = Process.Start(psi);
        if (process is null) return 1;
        // Ctrl+C reaches the child through the shared console; the wrapper just waits for it.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        process.WaitForExit();
        return process.ExitCode;
    }

    /// <summary>
    /// For a building command: any -m / -maxcpucount the caller gave becomes -m:1, and -m:1 is added when none was given.
    /// Arguments after "--" belong to the program being run and are never touched.
    /// </summary>
    internal static IReadOnlyList<string> Rewrite(IReadOnlyList<string> args)
    {
        // The command is the first argument that is not one of dotnet's own switches (`dotnet -d build`,
        // `dotnet --diagnostics test`): those switches take no value, so they are kept in front as given.
        var at = 0;
        while (at < args.Count && HostSwitches.Contains(args[at])) at++;
        if (at >= args.Count || !Building.Contains(args[at]) || args[at].Equals("build-server", StringComparison.OrdinalIgnoreCase))
            return args;
        var output = new List<string>(args.Take(at)) { args[at], "-m:1" };
        var passthrough = false;
        foreach (var a in args.Skip(at + 1))
        {
            if (!passthrough && a == "--") passthrough = true;
            if (!passthrough && MaxCpu().IsMatch(a)) continue; // the caller's own -m: replaced by ours
            output.Add(a);
        }
        return output;
    }

    /// <summary>dotnet's own switches that may come before an SDK command and take no value.</summary>
    internal static readonly HashSet<string> HostSwitches = new(StringComparer.OrdinalIgnoreCase) { "-d", "--diagnostics" };

    [GeneratedRegex(@"^[-/](m|maxcpucount)(:\d*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MaxCpu();

    /// <summary>The real dotnet: the first dotnet.exe on PATH that is not this wrapper, else Program Files.</summary>
    internal static string? RealDotnet()
    {
        var self = Path.GetFullPath(Environment.ProcessPath ?? "");
        var selfDir = Path.GetDirectoryName(self) ?? "";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(dir.Trim('"'), "dotnet.exe")); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (string.Equals(Path.GetDirectoryName(candidate), selfDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.Exists(candidate)) return candidate;
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        return File.Exists(fallback) ? fallback : null;
    }
}
