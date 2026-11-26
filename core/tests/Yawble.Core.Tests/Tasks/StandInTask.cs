using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Yawble.Core.Tests.Tasks;

/// <summary>
/// A stand-in host task for the tests that need a real process: this test program itself, started with an argument
/// that makes it act as a task instead of running tests. No system program is involved, and every wait has a limit
/// in seconds, so a stand-in that is never killed ends by itself.
/// <para>
/// The script form (<see cref="ScriptArgument"/>) reads a text file of steps, one per line; a blank line and a line
/// that starts with '#' do nothing. In every step <c>${NAME}</c> stands for an environment variable's value (empty
/// when not set) and <c>${#NAME}</c> for its length.
/// </para>
/// <list type="bullet">
/// <item><c>print TEXT</c>: a line on standard output. <c>error TEXT</c>: a line on standard error.</item>
/// <item><c>lines N</c>: the lines "line 1" to "line N" on standard output.</item>
/// <item><c>write PATH TEXT</c>: a file with that text, relative to the working folder.</item>
/// <item><c>wait SECONDS</c>: waits that long.</item>
/// <item><c>wait-for PATH SECONDS</c>: waits until the file exists, at most that long.</item>
/// <item><c>read-line CODE</c>: reads a line of input and prints "got LINE"; at the end of input prints "no input" and exits with the code.</item>
/// <item><c>read-key CODE</c>: reads one character of input and prints "got a key"; at the end of input prints "no key" and exits with the code.</item>
/// <item><c>spawn PATH SECONDS</c>: starts a copy that only waits, and writes "own-id copy-id" to the file.</item>
/// <item><c>exit CODE</c>: ends with that exit code. A script that runs to its end exits 0.</item>
/// </list>
/// </summary>
internal static partial class StandInTask
{
    /// <summary>Followed by a file path and a number of seconds: start a waiting copy, write its process id to the file, wait.</summary>
    public const string TreeArgument = "--stand-in-task-tree";

    /// <summary>Followed by a number of seconds: wait that long and exit.</summary>
    public const string WaitArgument = "--stand-in-task-wait";

    /// <summary>Followed by the path of a script of steps: run them.</summary>
    public const string ScriptArgument = "--stand-in-task-script";

    /// <summary>
    /// The name a host task gives as its program to run this test program: the tests' output folder is put on this
    /// process's PATH, where the host-task service looks a program name up.
    /// </summary>
    public static string ProgramName { get; } = PutOnPath();

    /// <summary>The program and arguments of a task that starts one child process, writes the child's id to <paramref name="pidFile"/> and waits.</summary>
    public static (string Program, string[] Args) Tree(string pidFile, int seconds)
    {
        var self = Self(TreeArgument, pidFile, seconds.ToString(CultureInfo.InvariantCulture));
        return (self.FileName, [.. self.ArgumentList]);
    }

    /// <summary>The program and arguments that run the steps in <paramref name="scriptFile"/>.</summary>
    public static (string Program, string[] Args) Script(string scriptFile)
    {
        var self = Self(ScriptArgument, scriptFile);
        return (self.FileName, [.. self.ArgumentList]);
    }

    /// <summary>Writes <paramref name="steps"/> to a new file in <paramref name="dir"/> and gives the program and arguments that run them.</summary>
    public static (string Program, string[] Args) Script(string dir, params string[] steps)
    {
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".task");
        File.WriteAllLines(path, steps);
        return Script(path);
    }

    private static string PutOnPath()
    {
        var dir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!path.Split(Path.PathSeparator).Contains(dir, StringComparer.OrdinalIgnoreCase))
            Environment.SetEnvironmentVariable("PATH", dir + Path.PathSeparator + path);
        return typeof(StandInTask).Assembly.GetName().Name!;
    }

#pragma warning disable CA2255 // Runs before the test runner reads its arguments, on purpose.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RunIfStandIn()
    {
        var args = Environment.GetCommandLineArgs();
        var tree = Array.IndexOf(args, TreeArgument);
        if (tree > 0 && tree + 2 < args.Length)
        {
            using var child = Process.Start(Self(WaitArgument, args[tree + 2]))!;
            File.WriteAllText(args[tree + 1], child.Id.ToString(CultureInfo.InvariantCulture));
            WaitAndExit(args[tree + 2]);
        }
        var wait = Array.IndexOf(args, WaitArgument);
        if (wait > 0 && wait + 1 < args.Length) WaitAndExit(args[wait + 1]);
        var script = Array.IndexOf(args, ScriptArgument);
        if (script > 0 && script + 1 < args.Length) Environment.Exit(RunScript(args[script + 1]));
    }

    private static void WaitAndExit(string seconds)
    {
        Thread.Sleep(TimeSpan.FromSeconds(int.Parse(seconds, CultureInfo.InvariantCulture)));
        Environment.Exit(0);
    }

    [GeneratedRegex(@"\$\{(#?)([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex Variable();

    private static string Expand(string text) => Variable().Replace(text, m =>
    {
        var value = Environment.GetEnvironmentVariable(m.Groups[2].Value) ?? "";
        return m.Groups[1].Length > 0 ? value.Length.ToString(CultureInfo.InvariantCulture) : value;
    });

    private static int RunScript(string path)
    {
        try
        {
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = Expand(raw.Trim());
                if (line.Length == 0 || line[0] == '#') continue;
                var space = line.IndexOf(' ');
                var step = space < 0 ? line : line[..space];
                var rest = space < 0 ? "" : line[(space + 1)..];
                switch (step)
                {
                    case "print":
                        Console.Out.WriteLine(rest);
                        Console.Out.Flush();
                        break;
                    case "error":
                        Console.Error.WriteLine(rest);
                        Console.Error.Flush();
                        break;
                    case "lines":
                        for (var i = 1; i <= Number(rest); i++) Console.Out.WriteLine("line " + i.ToString(CultureInfo.InvariantCulture));
                        Console.Out.Flush();
                        break;
                    case "write":
                    {
                        var (file, text) = Split(rest);
                        File.WriteAllText(file, text);
                        break;
                    }
                    case "wait":
                        Thread.Sleep(TimeSpan.FromSeconds(Number(rest)));
                        break;
                    case "wait-for":
                    {
                        var (seconds, file) = SplitLast(rest);
                        var until = Stopwatch.StartNew();
                        while (!File.Exists(file) && until.Elapsed < TimeSpan.FromSeconds(Number(seconds))) Thread.Sleep(50);
                        break;
                    }
                    case "read-line":
                        if (Console.In.ReadLine() is { } answer) Console.Out.WriteLine("got " + answer);
                        else
                        {
                            Console.Out.WriteLine("no input");
                            return Number(rest);
                        }
                        break;
                    case "read-key":
                        if (Console.In.Read() >= 0) Console.Out.WriteLine("got a key");
                        else
                        {
                            Console.Out.WriteLine("no key");
                            return Number(rest);
                        }
                        break;
                    case "spawn":
                    {
                        var (seconds, file) = SplitLast(rest);
                        // Not disposed on purpose: the copy lives on while the script goes on to its next step.
                        var child = Process.Start(Self(WaitArgument, seconds))!;
                        File.WriteAllText(file, $"{Environment.ProcessId} {child.Id}");
                        break;
                    }
                    case "exit":
                        return Number(rest);
                    default:
                        Console.Error.WriteLine($"stand-in task: unknown step '{step}'");
                        return 97;
                }
            }
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException)
        {
            Console.Error.WriteLine("stand-in task: " + e.Message);
            return 98;
        }
    }

    private static int Number(string text) => int.Parse(text.Trim(), CultureInfo.InvariantCulture);

    /// <summary>"PATH TEXT": the first word and the rest.</summary>
    private static (string First, string Others) Split(string text)
    {
        var space = text.IndexOf(' ');
        return space < 0 ? (text, "") : (text[..space], text[(space + 1)..]);
    }

    /// <summary>"PATH SECONDS": the last word and what is before it, so the path may hold spaces.</summary>
    private static (string Last, string Before) SplitLast(string text)
    {
        var space = text.LastIndexOf(' ');
        return space < 0 ? (text, "") : (text[(space + 1)..], text[..space]);
    }

    /// <summary>
    /// This program with the given arguments. Run as the test exe, that exe is the host; run as
    /// <c>dotnet Yawble.Core.Tests.dll</c>, the host is dotnet and the test dll goes first.
    /// </summary>
    private static ProcessStartInfo Self(params string[] arguments)
    {
        var host = Environment.ProcessPath!;
        var start = new ProcessStartInfo(host) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(StandInTask).Assembly.Location);
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return start;
    }
}
