using System.Runtime.CompilerServices;
using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// THE ROLE IS DECIDED BEFORE THE HOST'S OWN CODE IS COMPILED. A Host started with
/// <c>--Role worker</c> (or <c>HARNESS_ROLE=worker</c>) runs <see cref="WorkerProcess"/> and exits, from
/// here, when this assembly loads - before <c>Program</c>'s top-level statements run or are even
/// compiled. Those statements are one method; compiling it loads every assembly it names, the
/// database's and the key ring's among them, so a branch inside it would come too late for a worker
/// that must never load them. An unknown role is refused here too, with a sentence, for every role.
/// </summary>
internal static class WorkerRole
{
#pragma warning disable CA2255 // An application's own entry: the role must be known before Program is compiled.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void RunAWorkerInstead()
    {
        var args = Environment.GetCommandLineArgs();

        // Only the Host's own process: a test host or a tool that loads this assembly is not started with a role.
        if (!IsHostProcess(args)) return;

        var (role, refusal) = HostRoles.Resolve(args[1..], Environment.GetEnvironmentVariable(HostRoles.Variable));
        if (refusal is not null)
        {
            Console.Error.WriteLine(refusal);
            Environment.Exit(WorkerProcess.Misconfigured);
        }

        if (role != HostRole.Worker) return;

        Environment.Exit(WorkerProcess.RunAsync(args[1..], BuildVersion.Current.Version).GetAwaiter().GetResult());
    }

    /// <summary>Whether this process was started as the Host: its entry assembly is this one.</summary>
    private static bool IsHostProcess(string[] args) =>
        args.Length > 0 && System.Reflection.Assembly.GetEntryAssembly() == typeof(WorkerRole).Assembly;
}
