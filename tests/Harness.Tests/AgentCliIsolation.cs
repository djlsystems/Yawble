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
///
/// NOR A REAL UPDATE of one. A built-in preset's update is `npm install -g <package>@latest` (or the
/// CLI's own updater), run as a child of a worker the test started. With the CLIs stubbed it still
/// found the real `npm`, which read the instance's own prefix from the environment the suite was
/// started with, so an update a test Host asked for wrote the instance's install - and the test's end
/// killed it half-way, leaving every agent of the instance without a program. So, for this test
/// process and every process it starts:
/// - every built-in preset's update program resolves to a stub that refuses (its own folder,
///   <see cref="UpdateRoot"/>, so a test that hands a child a PATH of its own can put it first
///   without the CLI stubs - <see cref="Guard"/>);
/// - npm's global prefix is a scratch folder (<see cref="NpmPrefix"/>), so an `npm` reached some other
///   way - by its full path, on a PATH a test built - installs there and never into the instance's.
/// A test that builds a child's environment itself (clears it, or replaces PATH or the prefix) passes
/// it through <see cref="Guard"/>; `AgentCliIsolationTests` holds both, and that every such test does.
/// </summary>
internal static class AgentCliIsolation
{
    public const string StubMessage = "agent CLI stubbed by the test assembly: a test Host never starts a real agent";

    public const string UpdateStubMessage = "agent CLI update stubbed by the test assembly: no test runs a real agent CLI update";

    /// <summary>npm's own settings for its global prefix: it reads either spelling.</summary>
    public static IReadOnlyList<string> NpmPrefixVariables { get; } = ["NPM_CONFIG_PREFIX", "npm_config_prefix"];

    public static string Root { get; } =
        Path.Combine(Path.GetTempPath(), "harness-tests-agent-clis", Environment.ProcessId.ToString());

    /// <summary>The update programs' stubs, apart from the CLIs', so a child's own PATH can carry them alone.</summary>
    public static string UpdateRoot { get; } = Path.Combine(Root, "update");

    /// <summary>The npm global prefix of this test process and every process it starts.</summary>
    public static string NpmPrefix { get; } = Path.Combine(Root, "npm-prefix");

    [ModuleInitializer]
    internal static void Isolate()
    {
        // The suite runs on Linux (scripts/test-in-container.sh, the release). A Windows run fails
        // on platform grounds anyway, and a shell stub means nothing there.
        if (OperatingSystem.IsWindows()) return;

        Directory.CreateDirectory(Root);

        foreach (var command in Commands())
        {
            Stub(Path.Combine(Root, command), $"#!/bin/sh\necho '{StubMessage}' >&2\nexit 1\n");
        }

        Directory.CreateDirectory(UpdateRoot);
        Directory.CreateDirectory(NpmPrefix);
        foreach (var program in UpdatePrograms())
        {
            // It says which prefix it was given, so a test can see the redirect reached the update's process.
            Stub(Path.Combine(UpdateRoot, program),
                $"#!/bin/sh\necho \"{UpdateStubMessage} (npm prefix: ${{NPM_CONFIG_PREFIX:-${{npm_config_prefix:-unset}}}})\" >&2\nexit 1\n");
        }

        foreach (var variable in NpmPrefixVariables) Environment.SetEnvironmentVariable(variable, NpmPrefix);

        var path = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, new[] { Root, UpdateRoot, path }.Where(p => !string.IsNullOrEmpty(p))));

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
    /// A child environment a test built itself, guarded as this process's is: the update stubs first on
    /// its PATH (unless it already has them) and npm's prefix the scratch one, whatever the test set.
    /// </summary>
    public static void Guard(IDictionary<string, string?> environment)
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var variable in NpmPrefixVariables) environment[variable] = NpmPrefix;

        var path = environment.TryGetValue("PATH", out var given) ? given : null;
        if (path is null || !path.Split(Path.PathSeparator).Contains(UpdateRoot))
        {
            environment["PATH"] = string.IsNullOrEmpty(path) ? UpdateRoot : $"{UpdateRoot}{Path.PathSeparator}{path}";
        }
    }

    /// <summary>
    /// Every distinct bare program a built-in preset's platform update runs first (<c>npm</c>, a CLI's
    /// own updater), whatever the preset launches.
    /// </summary>
    public static IReadOnlyList<string> UpdatePrograms() =>
        [.. AgentCatalogFile.BuiltIns()
            .Select(preset => preset.Updates?.Update is [var program, ..] ? program : null)
            .OfType<string>()
            .Where(program => !program.Contains('/'))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

    private static void Stub(string path, string body)
    {
        File.WriteAllText(path, body);
        File.SetUnixFileMode(path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
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
