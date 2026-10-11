namespace Harness.Contracts;

/// <summary>
/// WHAT THIS HOST PROCESS IS. <see cref="All"/> is today's single process: control, with the run
/// worker in the same process. <see cref="Control"/> is control alone - admission, the stores, the
/// API - with every run on a worker that connects to it. <see cref="Worker"/> runs what control
/// places on it, and opens no store.
/// </summary>
public enum HostRole
{
    All,
    Control,
    Worker,
}

/// <summary>Reads the role from <c>--Role</c> (or <c>--Role=</c>) on the command line, else <c>HARNESS_ROLE</c>; absent is <see cref="HostRole.All"/>.</summary>
public static class HostRoles
{
    public const string Variable = "HARNESS_ROLE";

    /// <summary>The role, or the sentence an unknown one is refused with.</summary>
    public static (HostRole Role, string? Refusal) Resolve(IReadOnlyList<string> args, string? variable)
    {
        var configured = Argument(args, "Role") ?? variable;
        if (string.IsNullOrWhiteSpace(configured)) return (HostRole.All, null);

        return configured.Trim().ToLowerInvariant() switch
        {
            "all" => (HostRole.All, null),
            "control" => (HostRole.Control, null),
            "worker" => (HostRole.Worker, null),
            _ => (HostRole.All, $"--Role must be control, worker or all ({Variable}); '{configured.Trim()}' is none of them."),
        };
    }

    /// <summary>The value of <c>--name value</c>, <c>--name=value</c> or <c>/name value</c> on the command line, the last one given.</summary>
    public static string? Argument(IReadOnlyList<string> args, string name)
    {
        string? found = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            foreach (var prefix in new[] { "--", "/" })
            {
                if (!arg.StartsWith(prefix + name, StringComparison.OrdinalIgnoreCase)) continue;

                var rest = arg[(prefix.Length + name.Length)..];
                if (rest.StartsWith('=')) found = rest[1..];
                else if (rest.Length == 0 && i + 1 < args.Count) found = args[i + 1];
            }
        }

        return found;
    }
}
