namespace Harness.Pty;

/// <summary>
/// Finds an executable on PATH so a caller can resolve a command once and report a miss as a
/// launch failure, rather than letting the spawn fail with the OS's own less specific error.
/// </summary>
public static class PathSearch
{
    /// <summary>
    /// The full path of <paramref name="command"/>, or null when it is not an executable file.
    /// A command carrying a '/' is a location and is checked as given; a bare name is looked up
    /// in each PATH entry in order.
    /// </summary>
    public static string? Find(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var name = command.Trim();

        if (name.Contains('/')) return IsExecutable(name) ? name : null;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (IsExecutable(candidate)) return candidate;
        }

        return null;
    }

    private static bool IsExecutable(string path) =>
        File.Exists(path) &&
        (File.GetUnixFileMode(path) &
         (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
