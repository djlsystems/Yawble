using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Yawble.DotnetShim;

/// <summary>
/// `dotnet` inside a sandbox, first on its PATH: it runs the real dotnet with one MSBuild worker (-m:1) for the
/// commands that build. In a Windows ProcessContainer an ordinary named pipe can be created but never opened
/// (microsoft/mxc#1706), and on macOS the sandbox refuses the sockets MSBuild puts in /tmp; MSBuild reaches its worker
/// nodes through exactly those, so a parallel build fails or hangs. -m:1 goes to MSBuild only: for `dotnet run` and a
/// Microsoft.Testing.Platform `dotnet test` the wrapper builds first and then runs the command with --no-build (see
/// <see cref="Plan"/>). Everything else passes through unchanged. Not covered: `dotnet watch`, a full path to the real
/// dotnet.exe, and other tools that use ordinary named pipes.
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
            Console.Error.WriteLine("dotnet: the .NET SDK was not found on PATH or where its installer puts it");
            return 1;
        }
        // Ctrl+C reaches the child through the shared console; the wrapper just waits for it.
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        foreach (var command in Plan(args, Environment.CurrentDirectory, ShimFiles.Real))
        {
            var psi = new ProcessStartInfo(real) { UseShellExecute = false };
            foreach (var a in command) psi.ArgumentList.Add(a);
            using var process = Process.Start(psi);
            if (process is null) return 1;
            process.WaitForExit();
            if (process.ExitCode != 0) return process.ExitCode; // a failed build step: the program is not run
        }
        return 0;
    }

    /// <summary>
    /// The dotnet command lines to run in order for what the caller typed: one, or for `dotnet run` and a
    /// Microsoft.Testing.Platform `dotnet test` that build, two (`build -m:1`, then the command with --no-build).
    /// <para>
    /// Why two: MSBuild takes its node count from the command line only (the SDK always passes a bare -maxcpucount,
    /// which means every core and beats a response file or an environment variable), and neither command passes an -m
    /// on to MSBuild: `dotnet run` gives every option it does not know to the application, and an MTP `dotnet test`
    /// gives it to the test app. Building first in one node leaves the program's arguments exactly as typed.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<IReadOnlyList<string>> Plan(IReadOnlyList<string> args, string currentDirectory, ShimFiles files)
    {
        var at = CommandAt(args);
        if (at < args.Count && args[at].Equals("run", StringComparison.OrdinalIgnoreCase))
            return BuildThen(args, at, RunOptions, RunNoBuild, RunPositional, nologo: true, currentDirectory, files);
        if (at < args.Count && args[at].Equals("test", StringComparison.OrdinalIgnoreCase) && IsTestingPlatform(currentDirectory, files))
            return BuildThen(args, at, TestOptions, TestNoBuild, TestPositional, nologo: false, currentDirectory, files);
        return [Rewrite(args)];
    }

    /// <summary>The index of the dotnet command: the first argument that is not one of dotnet's own switches.</summary>
    private static int CommandAt(IReadOnlyList<string> args)
    {
        // `dotnet -d build`, `dotnet --diagnostics test`: those switches take no value, so they are kept in front as given.
        var at = 0;
        while (at < args.Count && HostSwitches.Contains(args[at])) at++;
        return at;
    }

    /// <summary>
    /// For a building command: any -m / -maxcpucount the caller gave becomes -m:1, and -m:1 is added when none was given.
    /// Arguments after "--" belong to the program being run and are never touched. Used for every building command
    /// except `run` and an MTP `test` (see <see cref="Plan"/>): build, VSTest test (it forwards unknown options to
    /// MSBuild), restore, publish, pack, clean, msbuild, store.
    /// </summary>
    internal static IReadOnlyList<string> Rewrite(IReadOnlyList<string> args)
    {
        var at = CommandAt(args);
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

    /// <summary>What a run / MTP test option is to the wrapper.</summary>
    [Flags]
    internal enum Opt
    {
        /// <summary>The command's own option, not given to the build step.</summary>
        Own = 0,
        /// <summary>Takes the next token as its value unless given as name:value / name=value.</summary>
        Value = 1,
        /// <summary>Takes a next token only when it is true or false (--interactive, --self-contained).</summary>
        OptionalBool = 2,
        /// <summary>Given to the build step as typed.</summary>
        ToBuild = 4,
        /// <summary>Its value is the project, solution or file: the build step's positional argument.</summary>
        Project = 8,
        /// <summary>The verbosity: without one the build step is quiet, like the SDK's implicit build.</summary>
        Verbosity = 16,
    }

    private const Opt Shared = Opt.Value | Opt.ToBuild;

    private static Dictionary<string, Opt> Options(params (string[] Names, Opt Kind)[] entries)
    {
        var map = new Dictionary<string, Opt>(StringComparer.Ordinal);
        foreach (var (names, kind) in entries)
            foreach (var n in names) map[n] = kind;
        return map;
    }

    // dotnet/sdk v10.0.404: CommonOptions (aliases), TargetPlatformOptions (-r, -a, --os), RunCommandDefinition and
    // TestCommandDefinition.MicrosoftTestingPlatform (which options each command has).
    private static readonly string[] Property = ["--property", "-property", "/property", "/p", "-p", "--p"];
    private static readonly string[] VerbosityNames = ["--verbosity", "-v", "--v", "-verbosity", "/v", "/verbosity"];
    private static readonly string[] Platform = ["-c", "--configuration", "-f", "--framework", "-r", "--runtime", "-a", "--arch", "--os", "--artifacts-path"];

    /// <summary>`dotnet run`'s options.</summary>
    internal static readonly Dictionary<string, Opt> RunOptions = Options(
        (Platform, Shared), (Property, Shared), (VerbosityNames, Shared | Opt.Verbosity),
        (["--no-restore", "--disable-build-servers", "--no-self-contained"], Opt.ToBuild),
        (["--interactive", "--self-contained", "--sc"], Opt.OptionalBool | Opt.ToBuild),
        (["--project", "--file"], Opt.Value | Opt.Project),
        (["--launch-profile", "-lp", "--device", "--environment", "-e"], Opt.Value),
        (["--no-launch-profile", "--no-launch-profile-arguments", "--no-cache"], Opt.Own));

    /// <summary>`dotnet run` options after which there is no build to do (or the SDK builds only itself: stdin).</summary>
    private static readonly HashSet<string> RunNoBuild = new(StringComparer.Ordinal) { "--no-build", "--list-devices", "-h", "--help", "-?", "/?", "/h" };

    /// <summary>A Microsoft.Testing.Platform `dotnet test`'s options.</summary>
    internal static readonly Dictionary<string, Opt> TestOptions = Options(
        (Platform, Shared), (Property, Shared), (VerbosityNames, Shared | Opt.Verbosity),
        (["--no-restore"], Opt.ToBuild),
        (["--project", "--solution"], Opt.Value | Opt.Project),
        (["--root-directory", "--results-directory", "--results-directory-layout", "--config-file", "--diagnostic-output-directory",
          "--max-parallel-test-modules", "--minimum-expected-tests", "--output", "--environment", "-e"], Opt.Value),
        (["--no-ansi", "--no-progress", "--list-tests", "--no-launch-profile", "--no-launch-profile-arguments", "--nologo", "--no-logo", "-nologo", "/nologo"], Opt.Own));

    /// <summary>MTP `dotnet test` options after which there is no build to do (--test-modules runs built files).</summary>
    private static readonly HashSet<string> TestNoBuild = new(StringComparer.Ordinal) { "--no-build", "--test-modules", "-h", "--help", "-?", "/?", "/h" };

    internal enum Positional { None, Project, NoBuild }

    /// <summary>
    /// `dotnet run`'s first application argument is a file-based app when it is an existing .cs file; "-" reads one from
    /// stdin, which the SDK builds itself and refuses --no-build for (RunCommand: InvalidOptionForStdin).
    /// </summary>
    private static Positional RunPositional(string token, string currentDirectory, ShimFiles files) =>
        token == "-" ? Positional.NoBuild
        : token.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && files.FileExists(Path.Combine(currentDirectory, token)) ? Positional.Project
        : Positional.None;

    /// <summary>MTP `dotnet test`'s first positional argument, as in MSBuildUtility.GetPositionalArguments.</summary>
    private static Positional TestPositional(string token, string currentDirectory, ShimFiles files)
    {
        var path = Path.Combine(currentDirectory, token);
        string[] buildable = [".sln", ".slnf", ".slnx", ".csproj", ".vbproj", ".fsproj"];
        if (buildable.Any(e => token.EndsWith(e, StringComparison.OrdinalIgnoreCase)) && files.FileExists(path)) return Positional.Project;
        if ((token.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) && files.FileExists(path))
            return Positional.NoBuild;
        return files.DirectoryExists(path) ? Positional.Project : Positional.None;
    }

    [GeneratedRegex(@"^(-|--|/)(bl|binarylogger)([:=].*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex BinaryLogger();

    /// <summary>
    /// `[host switches] build -m:1 [project] [the caller's build options] [-v:q] [--nologo]`, then the command as typed
    /// with --no-build after the verb. Nothing the caller typed is removed from the command, so the program gets exactly
    /// its own arguments; the build step gets only options both commands share. When the command would not build
    /// (--no-build, --help, ...) it is run alone, unchanged.
    /// </summary>
    private static IReadOnlyList<IReadOnlyList<string>> BuildThen(
        IReadOnlyList<string> args, int at, Dictionary<string, Opt> options, HashSet<string> noBuild,
        Func<string, string, ShimFiles, Positional> positional, bool nologo, string currentDirectory, ShimFiles files)
    {
        var host = args.Take(at).ToList();
        var rest = args.Skip(at + 1).ToList();
        var toBuild = new List<string>();
        string? projectOption = null, projectPositional = null;
        var verbosity = false;
        var firstUnknown = true;
        for (var i = 0; i < rest.Count; i++)
        {
            var t = rest[i];
            if (t == "--") break;
            if (noBuild.Contains(t)) return [args];
            if (BinaryLogger().IsMatch(t)) { toBuild.Add(t); continue; } // the SDK gives -bl to MSBuild (LoggerUtility)
            var (name, attached) = SplitOption(t);
            if (options.TryGetValue(name, out var kind))
            {
                var tokens = new List<string> { t };
                var value = attached;
                var next = i + 1 < rest.Count && rest[i + 1] != "--" ? rest[i + 1] : null;
                if (attached is null && next is not null
                    && (kind.HasFlag(Opt.Value) || (kind.HasFlag(Opt.OptionalBool) && next is "true" or "false")))
                {
                    tokens.Add(next);
                    value = next;
                    i++;
                }
                if (kind.HasFlag(Opt.Project)) projectOption = value;
                if (kind.HasFlag(Opt.ToBuild)) toBuild.AddRange(tokens);
                if (kind.HasFlag(Opt.Verbosity)) verbosity = true;
                continue;
            }
            if (!firstUnknown) continue;
            firstUnknown = false;
            switch (positional(t, currentDirectory, files))
            {
                case Positional.NoBuild: return [args];
                case Positional.Project: projectPositional = t; break;
            }
        }
        var build = new List<string>(host) { "build", "-m:1" };
        if ((projectOption ?? projectPositional) is { } project) build.Add(project);
        build.AddRange(toBuild);
        if (!verbosity) build.Add("-v:q");
        if (nologo) build.Add("--nologo");
        var then = new List<string>(host) { args[at], "--no-build" };
        then.AddRange(rest);
        return [build, then];
    }

    /// <summary>An option token's name and its attached value: `-p:A=1` is ("-p", "A=1"), `--os=linux` ("--os", "linux").</summary>
    private static (string Name, string? Value) SplitOption(string token)
    {
        if (token.Length < 2 || token[0] is not ('-' or '/')) return (token, null);
        var cut = token.IndexOfAny([':', '=']);
        return cut < 0 ? (token, null) : (token[..cut], token[(cut + 1)..]);
    }

    /// <summary>
    /// Whether `dotnet test` here runs Microsoft.Testing.Platform: the nearest global.json from the current folder up
    /// says "test": { "runner": "Microsoft.Testing.Platform" } (dotnet/sdk v10.0.404 TestCommandDefinition.Create:
    /// GetGlobalJsonPath(Environment.CurrentDirectory) takes the first global.json found, comments skipped, property names
    /// as written, the runner name compared ignoring case). Unreadable or malformed counts as VSTest.
    /// </summary>
    internal static bool IsTestingPlatform(string currentDirectory, ShimFiles files)
    {
        for (var dir = currentDirectory; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
        {
            var path = Path.Combine(dir, "global.json");
            if (!files.FileExists(path)) continue;
            try
            {
                var text = files.ReadText(path);
                if (text is null) return false;
                using var doc = JsonDocument.Parse(text, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
                return doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty("test", out var test) && test.ValueKind == JsonValueKind.Object
                    && test.TryGetProperty("runner", out var runner) && runner.ValueKind == JsonValueKind.String
                    && string.Equals(runner.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>dotnet's own switches that may come before an SDK command and take no value.</summary>
    internal static readonly HashSet<string> HostSwitches = new(StringComparer.OrdinalIgnoreCase) { "-d", "--diagnostics" };

    [GeneratedRegex(@"^[-/](m|maxcpucount)(:\d*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex MaxCpu();

    /// <summary>
    /// The real dotnet: the first dotnet on PATH that is not this wrapper, else where the installer puts it (Program
    /// Files on Windows; /usr/local/share/dotnet on macOS).
    /// </summary>
    internal static string? RealDotnet() =>
        RealDotnet(Environment.GetEnvironmentVariable("PATH"), Environment.ProcessPath, OperatingSystem.IsWindows(), File.Exists);

    internal static string? RealDotnet(string? pathVariable, string? processPath, bool windows, Func<string, bool> exists)
    {
        if (!windows)
        {
            // POSIX paths as text: a run's PATH holds only absolute folders.
            var self = processPath ?? "";
            var selfFolder = self.Contains('/') ? self[..self.LastIndexOf('/')] : "";
            foreach (var dir in (pathVariable ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                var folder = dir.Length > 1 ? dir.TrimEnd('/') : dir;
                if (!folder.StartsWith('/') || folder == selfFolder) continue;
                var candidate = folder + "/dotnet";
                if (exists(candidate)) return candidate;
            }
            return exists("/usr/local/share/dotnet/dotnet") ? "/usr/local/share/dotnet/dotnet" : null;
        }
        var selfDir = Path.GetDirectoryName(Path.GetFullPath(processPath ?? "")) ?? "";
        foreach (var dir in (pathVariable ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(dir.Trim('"'), "dotnet.exe")); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { continue; }
            if (string.Equals(Path.GetDirectoryName(candidate), selfDir, StringComparison.OrdinalIgnoreCase)) continue;
            if (exists(candidate)) return candidate;
        }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        return exists(fallback) ? fallback : null;
    }
}

/// <summary>The files the wrapper reads (global.json, whether a positional argument is a project), injectable for tests.</summary>
internal sealed record ShimFiles(Func<string, bool> FileExists, Func<string, bool> DirectoryExists, Func<string, string?> ReadText)
{
    public static readonly ShimFiles Real = new(File.Exists, Directory.Exists, File.ReadAllText);
}
