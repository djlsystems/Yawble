using System.Diagnostics;
using Harness.Contracts;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// WHETHER ONE CLI IS SIGNED IN ON THIS WORKER, before a run spends tokens to find out. Asked of the
/// environment this worker's children inherit and the agent's home here, never control's.
/// </summary>
/// <remarks>
/// <c>Authenticated == null</c> means not measured: a probe that cannot see must not convict. A
/// credential variable the probe file names is enough, and so is a saved login (a file the CLI
/// writes when a person signs in, named relative to HOME). A status command is only run when neither
/// is present, as the agent and with the CLI's update-off; a non-zero exit means signed out and a
/// timeout stays null.
/// </remarks>
public static class SignInProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    /// <summary>One command's answer.</summary>
    public static async Task<SignInProbeResult> ProbeAsync(SignInProbeSpec spec, AgentLaunchUser? runAs, CancellationToken ct)
    {
        var (installed, authenticated, detail) = await AnswerAsync(spec, runAs, ct);
        return new SignInProbeResult(spec.Command, installed, authenticated, detail);
    }

    private static async Task<(bool Installed, bool? Authenticated, string Detail)> AnswerAsync(
        SignInProbeSpec spec, AgentLaunchUser? runAs, CancellationToken ct)
    {
        var command = spec.Command;
        var resolved = PathSearch.Find(command);
        if (resolved is null)
        {
            return (false, null, $"'{command}' is not on PATH.");
        }

        if (spec.CredentialVariable is { Length: > 0 } variable
            && !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(variable)))
        {
            return (true, true, $"Authenticated via {variable}.");
        }

        if (spec.CredentialFiles is { Count: > 0 } files
            && Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home)
        {
            var saved = files.FirstOrDefault(file => File.Exists(Path.Combine(home, file)));
            if (saved is not null)
            {
                return (true, true, $"Authenticated via a saved login ({saved}).");
            }
        }

        if (spec.StatusArguments is not { Count: > 0 } args)
        {
            return (true, null, "Installed. No auth probe is configured for this command, and no credential variable is set.");
        }

        // The CLI was found on PATH, whose first folders the agent owns. Where an `agent` user exists
        // but this process cannot start anything as it, running that file here would run
        // agent-written code as this user. A root-owned system program is still asked.
        if (runAs is { Switches: false, AgentUserUnreachable: true } && !SystemCommand.IsTrusted(resolved))
        {
            return (true, null,
                $"Not probed: this process cannot start '{command}' as '{runAs.Name}' ({runAs.Reason}), and it does not run an agent-installed program as itself.");
        }

        var prefix = runAs?.Prefix ?? [];

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    // Asked as the user the agents run as, whose login it is, and with no
                    // capability: `setpriv` (from a system directory) first whenever there is a prefix.
                    FileName = prefix.Count > 0 ? prefix[0] : resolved,
                    WorkingDirectory = Path.GetTempPath(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                },
            };

            if (prefix.Count > 0)
            {
                foreach (var part in prefix.Skip(1)) process.StartInfo.ArgumentList.Add(part);
                process.StartInfo.ArgumentList.Add(resolved);
            }

            // WITH THE CLI'S OWN UPDATER OFF, as every launch of it: a probe that started an update
            // would replace the install members launch from.
            foreach (var arg in spec.UpdateArguments ?? []) process.StartInfo.ArgumentList.Add(arg);
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            foreach (var (key, value) in spec.UpdateEnvironment ?? new Dictionary<string, string>())
            {
                process.StartInfo.Environment[key] = value;
            }

            if (!process.Start()) return (true, null, "The status command could not be started.");

            try
            {
                await process.WaitForExitAsync(ct).WaitAsync(Timeout, ct);
            }
            catch (TimeoutException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return (true, null, "The status command did not finish in time.");
            }

            return process.ExitCode == 0
                ? (true, true, "The status command exited 0.")
                : (true, false, $"The status command exited {process.ExitCode}.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (true, null, "The status command could not be run.");
        }
    }
}
