using System.Diagnostics;
using Xunit;

namespace Yawble.Core.Tests;

/// <summary>
/// Whether git can move commits between two folders here (fetch, push, clone or ls-remote of a local path).
///
/// Git runs that local transport as <c>sh -c "git-upload-pack '&lt;path&gt;'"</c> (or git-receive-pack). Git for
/// Windows' sh is an MSYS program, and inside an agent's sandbox it dies at start: it cannot create its shared
/// objects ("NtCreateDirectoryObject(\BaseNamedObjects\msys-2.0...): 0xC0000022", access denied), and git reports
/// "Could not read from remote repository". Nothing a test sets changes that (git finds its sh by its own install
/// folder, not by PATH). A bundle needs no sh, which is why agents fetch the team's work from a bundle; the host
/// (where the product's own fetch and push run) has a working sh.
///
/// So a test that needs the local transport is skipped inside such a sandbox, with <see cref="Reason"/>, and runs
/// everywhere else.
/// </summary>
internal static class SandboxGit
{
    /// <summary>
    /// True when this test process runs inside a Windows AppContainer, as an agent's sandbox on Windows does
    /// (its token says so); false elsewhere.
    /// </summary>
    public static bool InsideWindowsSandbox => InsideAppContainer.Value;

    private static readonly Lazy<bool> InsideAppContainer = new(() =>
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (!OpenProcessToken(GetCurrentProcess(), 0x0008 /* TOKEN_QUERY */, out var token)) return false;
        try
        {
            // TokenIsAppContainer = 29: a DWORD, non-zero inside an AppContainer.
            return GetTokenInformation(token, 29, out var value, sizeof(uint), out _) && value != 0;
        }
        finally { CloseHandle(token); }
    });

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [System.Runtime.InteropServices.DllImport("advapi32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(nint token, int infoClass, out uint value, int length, out int returned);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    public const string Reason =
        "Needs git's local transport (fetch, push or clone between folders), which starts Git for Windows' sh; "
        + "inside an agent's sandbox that sh cannot start (NtCreateDirectoryObject access denied). Run it outside the sandbox.";

    /// <summary>What the MSYS sh prints when it cannot start in the sandbox.</summary>
    public const string ShStartFailure = "NtCreateDirectoryObject";

    private static readonly Lazy<bool> Probe = new(ProbeLocalTransport, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>True when git can reach another local repository here (probed once per test run).</summary>
    public static bool LocalTransportWorks => Probe.Value;

    /// <summary>Skips the test when git's local transport cannot run here.</summary>
    public static void RequireLocalTransport() => Assert.SkipUnless(LocalTransportWorks, Reason);

    /// <summary>What git says when the other side of its local transport never answered.</summary>
    public const string NoAnswer = "Could not read from remote repository";

    /// <summary>
    /// Skips the test when a git that failed printed the sandbox's sh start failure, or said the other side never
    /// answered where the local transport cannot run (a push does not always pass the sh's own message on).
    /// </summary>
    public static void SkipIfShCouldNotStart(string? gitOutput)
    {
        if (gitOutput is null) return;
        if (gitOutput.Contains(ShStartFailure, StringComparison.Ordinal)
            || (gitOutput.Contains(NoAnswer, StringComparison.Ordinal) && !LocalTransportWorks))
        {
            Assert.Skip(Reason);
        }
    }

    private static bool ProbeLocalTransport()
    {
        var dir = Directory.CreateTempSubdirectory("yawble-transport-").FullName;
        try
        {
            if (Git(dir, "init", "-q", "--bare", "probe.git") != 0) return true; // no git at all: not this cause
            return Git(dir, "ls-remote", Path.Combine(dir, "probe.git")) == 0;
        }
        finally
        {
            try { TestFiles.DeleteTree(dir); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static int Git(string cwd, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = cwd, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi)!;
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(TimeSpan.FromSeconds(30))) { try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } return -1; }
            Task.WaitAll(stdout, stderr);
            return p.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception) { return 0; } // no git: tests needing it fail on their own
    }
}
