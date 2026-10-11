using System.Runtime.CompilerServices;

namespace Harness.Tests;

/// <summary>
/// Every launch writes grok's MCP entry into <c>$GROK_HOME/config.toml</c>, and that entry
/// follows the host that last launched grok. Without this, a test run in the live
/// container would point the live host's grok at a test host's port. The whole test assembly gets
/// a grok home of its own before any test runs.
/// </summary>
internal static class GrokHomeIsolation
{
    public static string Root { get; } =
        Path.Combine(Path.GetTempPath(), "harness-tests-grok-home", Environment.ProcessId.ToString());

    [ModuleInitializer]
    internal static void Isolate()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("GROK_HOME", Root);

        // Gone with the run, and the shared parent with it once no other run is using it.
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
}
