using System.Diagnostics;
using System.Text;
using Harness.Contracts;
using Harness.Pty;

namespace Harness.Host;

/// <summary>
/// RUNS ONE AGENT CLI COMMAND ON THIS WORKER and says what it printed: a tool listing, a version, a
/// platform update. As the agent (the launch prefix), with this worker's environment plus the
/// command's overlay and the run's credential, every provider key that is not the CLI's own taken
/// out, as at a member's spawn. Never runs an agent-installed program as the worker: when the agent
/// user exists and cannot be reached, it does not run at all.
/// </summary>
public sealed class AgentCommands(AgentLaunchUser? runAs, RunHomes homes)
{
    /// <summary>One command, never throwing but for cancellation.</summary>
    public async Task<AgentCliRunResult> RunAsync(AgentCliRun command, RunCredential? credential, CancellationToken ct)
    {
        if (PathSearch.Find(command.FileName) is not { } resolved)
        {
            return new AgentCliRunResult(false, null, false, "", "", false, $"'{command.FileName}' is not on PATH.");
        }

        if (runAs is { Refuses: true })
        {
            return new AgentCliRunResult(true, null, false, "", "", false, runAs.Refusal($"'{command.FileName}'"));
        }

        string? scratch = null;
        string? home = null;

        try
        {
            if (command.Scratch)
            {
                scratch = Directory.CreateTempSubdirectory("harness-tool-listing-").FullName;
                runAs?.Share(scratch);
            }

            // A home of the command's own, as an issued member run gets, with its cache beside it.
            if (command.HomesIn is { } parent && (home = await homes.CreateAsync(parent, null, ct, memberFolder: false)) is null)
            {
                return new AgentCliRunResult(true, null, false, "", "", false,
                    "This command runs in a home of its own, which could not be made.");
            }

            var prefix = runAs?.Prefix ?? [];
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = prefix.Count > 0 ? prefix[0] : resolved,
                    WorkingDirectory = scratch ?? Path.GetTempPath(),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                },
            };

            if (prefix.Count > 0)
            {
                foreach (var part in prefix.Skip(1)) process.StartInfo.ArgumentList.Add(part);
                process.StartInfo.ArgumentList.Add(resolved);
            }

            foreach (var argument in command.Arguments) process.StartInfo.ArgumentList.Add(argument);

            var environment = process.StartInfo.Environment;
            foreach (var (name, value) in command.Environment) environment[name] = value;
            if (home is not null)
            {
                environment["HOME"] = home;
                environment["XDG_CACHE_HOME"] = RunHomes.CacheBeside(home);
            }

            // The credential as a member run gets it, then every provider key but this CLI's own
            // taken out, as at a member's spawn.
            credential?.ApplyTo(environment, command.Environment);
            foreach (var name in command.RemovedEnvironment) environment.Remove(name);

            if (!process.Start()) return new AgentCliRunResult(true, null, false, "", "", false, "It could not be started.");
            process.StandardInput.Close();

            var stdout = ReadAsync(process.StandardOutput, ct);
            var stderr = ReadAsync(process.StandardError, ct);

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(command.TimeoutSeconds), ct);
            }
            catch (TimeoutException)
            {
                timedOut = true;
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            }

            var (output, outputCut) = await stdout;
            var (error, errorCut) = await stderr;

            return new AgentCliRunResult(
                true, timedOut ? null : process.ExitCode, timedOut, output, error, outputCut || errorCut,
                timedOut ? $"It did not finish in {command.TimeoutSeconds} seconds." : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new AgentCliRunResult(true, null, false, "", "", false, exception.Message);
        }
        finally
        {
            if (home is not null) await homes.RemoveAsync(home);

            // The worker made the scratch folder owner-only and AgentLaunchUser.Share gives the agent's
            // group read and traverse, never write; the CLI runs in it but cannot create anything in it.
            if (scratch is not null)
            {
                // RECURSIVE DELETE REVIEWED: agents cannot write here (above).
                try { Directory.Delete(scratch, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>A stream read to its end, keeping its first <see cref="AgentCliRuns.MaxOutputBytes"/> bytes.</summary>
    private static async Task<(string Text, bool Cut)> ReadAsync(StreamReader reader, CancellationToken ct)
    {
        var kept = new StringBuilder();
        var bytes = 0;
        var cut = false;
        var buffer = new char[8192];

        int read;
        while ((read = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (cut) continue;

            for (var i = 0; i < read; i++)
            {
                var size = Encoding.UTF8.GetByteCount(buffer, i, char.IsHighSurrogate(buffer[i]) && i + 1 < read ? 2 : 1);
                if (bytes + size > AgentCliRuns.MaxOutputBytes)
                {
                    cut = true;
                    break;
                }

                kept.Append(buffer[i]);
                if (size == 4)
                {
                    kept.Append(buffer[++i]);
                }

                bytes += size;
            }
        }

        return (kept.ToString(), cut);
    }
}
