using System.Runtime.CompilerServices;

namespace Yawble.Core.Tests;

/// <summary>
/// The one temp root every test that makes folders, files or git repositories uses. It is set for the whole test
/// process before any test runs, by pointing the temp variables (TMP and TEMP on Windows, TMPDIR elsewhere) at it, so
/// <see cref="Path.GetTempPath"/>, <see cref="Directory.CreateTempSubdirectory(string?)"/> and every child process
/// (git above all) use it without each test knowing.
///
/// Why: inside an agent's sandbox the temp folder is deep (the member's home, then AppData\Local\Packages\sandbox.{guid}\AC\Temp,
/// close to 190 characters), and a test repository's object paths below it pass Windows' 260-character limit, so
/// git fails with "Filename too long". The root is:
/// <list type="bullet">
/// <item>the folder in <see cref="Variable"/> when it is set (a person or a CI run can choose any short folder);</item>
/// <item>otherwise, when the process's temp folder is deep (longer than <see cref="DeepTemp"/> characters), a short
///   folder in the user's home (in a sandbox, the member's own home, which the sandbox can write);</item>
/// <item>otherwise the process's temp folder, unchanged (on a person's computer nothing moves).</item>
/// </list>
/// Git's <c>core.longpaths=true</c> is also given (by GIT_CONFIG_COUNT) to every git the tests start that inherits the
/// environment, as a second guard for a repository that is still deep; the product's own git drops inherited GIT_*
/// variables, so it is not changed by this.
/// </summary>
internal static class TestTemp
{
    /// <summary>The environment variable naming the test temp root.</summary>
    public const string Variable = "YAWBLE_TEST_TEMP";

    /// <summary>Above this many characters the process's temp folder counts as deep.</summary>
    public const int DeepTemp = 100;

    /// <summary>The short folder made in the user's home when the temp folder is deep.</summary>
    public const string HomeFolderName = "yt";

    /// <summary>The temp root in use (with no trailing separator).</summary>
    public static string Root { get; private set; } = Path.TrimEndingDirectorySeparator(Path.GetTempPath());

    /// <summary>The git configuration given to child gits: core.longpaths=true (Windows only; elsewhere it means nothing).</summary>
    public static IReadOnlyList<string> GitOptions { get; } = OperatingSystem.IsWindows() ? ["-c", "core.longpaths=true"] : [];

#pragma warning disable CA2255 // A module initializer in a test assembly, on purpose: it must run before every test.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Initialize()
    {
        var chosen = Choose(
            Environment.GetEnvironmentVariable(Variable),
            Path.GetTempPath(),
            // The variables first: a sandbox sets them to the member's home, while the known folder (from the
            // account) is the person's own profile, which the sandbox cannot write.
            Environment.GetEnvironmentVariable(OperatingSystem.IsWindows() ? "USERPROFILE" : "HOME") is { Length: > 0 } home
                ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (chosen is not null)
        {
            try
            {
                Directory.CreateDirectory(chosen);
                Use(chosen);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT") is null)
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
            Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "core.longpaths");
            Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "true");
        }
    }

    /// <summary>
    /// The root to use instead of the current temp folder, or null to keep it: the variable when set, else a short
    /// folder in <paramref name="home"/> when <paramref name="currentTemp"/> is deep.
    /// </summary>
    public static string? Choose(string? variable, string currentTemp, string? home)
    {
        if (!string.IsNullOrWhiteSpace(variable)) return Path.GetFullPath(variable.Trim());
        var temp = Path.TrimEndingDirectorySeparator(currentTemp);
        if (temp.Length <= DeepTemp || string.IsNullOrEmpty(home)) return null;
        var shortRoot = Path.Combine(home, HomeFolderName);
        return shortRoot.Length < temp.Length ? shortRoot : null;
    }

    private static void Use(string root)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable("TMP", Root);
            Environment.SetEnvironmentVariable("TEMP", Root);
        }
        else
        {
            Environment.SetEnvironmentVariable("TMPDIR", Root);
        }
    }
}
