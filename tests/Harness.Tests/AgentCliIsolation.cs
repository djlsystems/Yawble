using System.Runtime.CompilerServices;
using Harness.Host;

namespace Harness.Tests;

/// <summary>
/// A test Host never starts a real agent. Every built-in language-model preset's command resolves, for this
/// test process and every Host it starts, to a stub that says why and exits 1.
///
/// WHY: the release runs the suite inside the product image, where `claude` (and the others) are
/// installed and signed in through the shared agent home. A test that fires a trigger, uploads into
/// a watched folder or tells a member hands the work to the Host's real launcher, which found the
/// real CLI on PATH and started a real, paid run - with the signed-in account's own tools - that
/// outlived its test Host and hung once that Host's MCP endpoint was gone. Outside the image (the
/// SDK container) the commands were simply missing, which is why no test noticed.
///
/// The stubs come FIRST on PATH, so `PathSearch.Find` (the one resolution the launcher, the auth
/// probe and the install probe share) finds them before any real install. The list is the build's
/// own built-in presets, read at load, so a preset added later is stubbed without an edit here;
/// `AgentCliIsolationTests` holds that. A test that wants a real program names its own command.
/// </summary>
internal static class AgentCliIsolation
{
    public const string StubMessage = "agent CLI stubbed by the test assembly: a test Host never starts a real agent";

    public static string Root { get; } =
        Path.Combine(Path.GetTempPath(), "harness-tests-agent-clis", Environment.ProcessId.ToString());

    [ModuleInitializer]
    internal static void Isolate()
    {
        // The suite runs on Linux (scripts/test-in-container.sh, the release). A Windows run fails
        // on platform grounds anyway, and a shell stub means nothing there.
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Root);

        foreach (var command in Commands())
        {
            var stub = Path.Combine(Root, command);
            File.WriteAllText(stub, $"#!/bin/sh\necho '{StubMessage}' >&2\nexit 1\n");
            File.SetUnixFileMode(stub,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.IsNullOrEmpty(path) ? Root : $"{Root}{Path.PathSeparator}{path}");

        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                Directory.Delete(Path.GetDirectoryName(Root)!);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        };
    }

    /// <summary>
    /// Every distinct bare command a built-in LANGUAGE-MODEL preset launches. Not the others: the
    /// `echo` preset runs `cat` and the shell preset `bash`, which call no model and which the
    /// suite's own tests run on purpose.
    /// </summary>
    public static IReadOnlyList<string> Commands() =>
        [.. AgentCatalogFile.BuiltIns()
            .Where(preset => preset.Launch.LanguageModel)
            .Select(preset => preset.Launch.FileName)
            .Where(command => !command.Contains('/'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
}
