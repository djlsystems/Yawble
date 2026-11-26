using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace Yawble.Core.Tests.Tasks;

/// <summary>
/// A stand-in host task for the tests that need a real process tree to kill: this test program itself, started with
/// an argument that makes it wait instead of running tests. No system program is involved, and every stand-in ends
/// by itself after the seconds it is given, so one that is never killed does not stay behind.
/// </summary>
internal static class StandInTask
{
    /// <summary>Followed by a file path and a number of seconds: start a waiting copy, write its process id to the file, wait.</summary>
    public const string TreeArgument = "--stand-in-task-tree";

    /// <summary>Followed by a number of seconds: wait that long and exit.</summary>
    public const string WaitArgument = "--stand-in-task-wait";

    /// <summary>The program and arguments of a task that starts one child process, writes the child's id to <paramref name="pidFile"/> and waits.</summary>
    public static (string Program, string[] Args) Tree(string pidFile, int seconds)
    {
        var self = Self(TreeArgument, pidFile, seconds.ToString(CultureInfo.InvariantCulture));
        return (self.FileName, [.. self.ArgumentList]);
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
    }

    private static void WaitAndExit(string seconds)
    {
        Thread.Sleep(TimeSpan.FromSeconds(int.Parse(seconds, CultureInfo.InvariantCulture)));
        Environment.Exit(0);
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
