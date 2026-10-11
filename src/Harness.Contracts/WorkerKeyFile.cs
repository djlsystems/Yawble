namespace Harness.Contracts;

/// <summary>
/// THE WORKER KEY, FROM ITS VARIABLE OR FROM A FILE. In an image the entrypoint moves the key out of
/// the environment before anything runs, into a file only the container's Host user can read, and
/// names that file in <see cref="FileVariable"/>: a variable would be in the Host's
/// <c>/proc/&lt;pid&gt;/environ</c>, and every child it starts would inherit it. Control and a worker
/// read the key the same way: the key itself when one is configured, else the file's first line.
/// </summary>
public static class WorkerKeyFile
{
    public const string FileVariable = "HARNESS_WORKER_KEY_FILE";

    /// <summary>
    /// The key: <paramref name="key"/> when it is set, else the first line of <paramref name="file"/>,
    /// trimmed. Null with no refusal when neither is set; a refusal when a file is named and cannot be
    /// read, or holds no key.
    /// </summary>
    public static (string? Key, string? Refusal) Read(string? key, string? file)
    {
        if (!string.IsNullOrWhiteSpace(key)) return (key, null);
        if (string.IsNullOrWhiteSpace(file)) return (null, null);

        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return (null, $"The worker key file {file} could not be read: {exception.Message}");
        }

        var first = text.Split('\n', 2)[0].Trim();
        return first.Length > 0 ? (first, null) : (null, $"The worker key file {file} could not be read: it holds no key.");
    }
}
