using System.Globalization;

namespace Harness.Host;

/// <summary>An image saved into a person's Concierge folder: what the upload route answers.</summary>
/// <param name="Path">The absolute path, which the page pastes into the Concierge's prompt.</param>
/// <param name="Size">Bytes written.</param>
/// <param name="Type">The detected media type, never the one the browser claimed.</param>
public sealed record ConciergeAttachment(string Path, long Size, string Type);

/// <summary>Why an attachment was refused, with the status the route answers and the sentence a
/// person reads in the panel.</summary>
public sealed class ConciergeAttachmentRefused(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// IMAGES A PERSON GIVES THEIR CONCIERGE, as files in its own working folder - the one way an
/// image reaches an agent CLI whose clipboard is a container's, which has none.
///
/// <list type="number">
/// <item><b>The type is the content's.</b> PNG, JPEG, GIF and WebP, told by their signature bytes;
/// the uploaded name and Content-Type are never read. Nothing past the signature is decoded: the
/// Host does not parse an image, and the app never renders one.</item>
/// <item><b>The name is generated</b>, <c>attachments/&lt;utc-timestamp&gt;-&lt;n&gt;.&lt;ext&gt;</c>, the
/// extension from the detected type. Created with <see cref="FileMode.CreateNew"/>, so an existing
/// file - or a link an agent planted at that name - is never written through.</item>
/// <item><b>The agent can read it</b>: owner read-write, group read, and handed to the agent's group
/// by <see cref="AgentLaunchUser.Share"/>, as every other file the Host writes for an agent.</item>
/// <item><b>No row, no file.</b> The <c>concierge.attachment-added</c> row is written after the
/// file, and a row that cannot land removes the file before the refusal is answered.</item>
/// <item><b>They do not outlive their session</b>: removed when the Concierge session ends, and
/// any older than the retention removed at start. Through <see cref="FolderRemoval"/>, because the
/// agent writes in this folder and may have left something the Host cannot remove itself.</item>
/// </list>
///
/// THE FOLDER IS THE AGENT'S TOO. The Concierge runs with its working folder writable, so
/// <c>attachments</c> is refused when it is a symbolic link or reached through one; a link there
/// would point the Host's write wherever the agent liked.
/// </summary>
public sealed class ConciergeAttachments(
    TeamPaths paths, AgentLaunchUser runAs, FolderRemoval removal, long maxBytes, TimeSpan retention)
{
    public const string FolderName = "attachments";

    /// <summary>The size cap when <c>ConciergeAttachmentMaxBytes</c> is not configured.</summary>
    public const long DefaultMaxBytes = 10 * 1024 * 1024;

    /// <summary>How long an attachment is kept when <c>ConciergeAttachmentRetention</c> is not configured.</summary>
    public static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(7);

    /// <summary>Owner read-write, group read: the agent's group reads it, nobody else does.</summary>
    public const UnixFileMode FileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead;

    /// <summary>Bytes needed to tell every accepted type apart.</summary>
    private const int SignatureLength = 12;

    public long MaxBytes => maxBytes;

    public TimeSpan Retention => retention;

    /// <summary>
    /// The media type and extension <paramref name="head"/> begins with, or null when it is none of
    /// the four. Signatures only: PNG's eight bytes, JPEG's SOI marker, GIF87a/GIF89a, and a RIFF
    /// container whose form type is WEBP.
    /// </summary>
    public static (string Type, string Extension)? Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])) return ("image/png", "png");
        if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])) return ("image/jpeg", "jpg");
        if (head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)) return ("image/gif", "gif");
        if (head.Length >= SignatureLength && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return ("image/webp", "webp");

        return null;
    }

    /// <summary>
    /// Saves <paramref name="content"/> into this person's Concierge folder and has
    /// <paramref name="record"/> write its row; removes the file again when the row fails, and
    /// throws what the row threw.
    /// </summary>
    /// <exception cref="ConciergeAttachmentRefused">No Concierge folder yet (409), over the cap
    /// (413), not one of the four types (415), or an <c>attachments</c> folder that is a link (409).</exception>
    public async Task<ConciergeAttachment> SaveAsync(
        string userId, Stream content, long length,
        Func<ConciergeAttachment, CancellationToken, Task> record, DateTimeOffset now, CancellationToken ct)
    {
        if (length > maxBytes) throw TooLarge();

        // NEVER CREATED HERE. Resolving a workspace makes the next launch a relaunch, so a person
        // with no Concierge yet is told to open one rather than handed a folder nothing launched.
        var workspace = ConciergeWorkspaces.TryExisting(paths, userId)
            ?? throw new ConciergeAttachmentRefused(409, "Open the Concierge first: it has no working folder to put the image in yet.");

        var head = new byte[SignatureLength];
        var read = await content.ReadAtLeastAsync(head, SignatureLength, throwOnEndOfStream: false, ct);

        if (Detect(head.AsSpan(0, read)) is not var (type, extension))
        {
            throw new ConciergeAttachmentRefused(415, "That is not a PNG, JPEG, GIF or WebP image, so it was not attached.");
        }

        var folder = Path.Combine(workspace, FolderName);

        if (IsLink(folder) || !FolderRemoval.Confined(workspace, folder))
        {
            throw new ConciergeAttachmentRefused(409,
                $"The image was not attached: {folder} is a symbolic link, and the Host does not write through one.");
        }

        Directory.CreateDirectory(folder);

        var (path, stream) = Create(folder, extension, now);
        long size;

        try
        {
            await using (stream)
            {
                await stream.WriteAsync(head.AsMemory(0, read), ct);
                await content.CopyToAsync(stream, ct);
                size = stream.Length;
            }

            // The declared length is the browser's word; the bytes are the fact.
            if (size > maxBytes) throw TooLarge();

            runAs.Share(path);

            var saved = new ConciergeAttachment(path, size, type);
            await record(saved, ct);
            return saved;
        }
        catch
        {
            // NO ROW, NO FILE. The Host made this file a moment ago, in a folder it can write.
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            throw;
        }
    }

    /// <summary>
    /// Removes this person's attachments - the whole folder, the agent's leftovers included.
    /// Called when their Concierge session ends. Never throws: a session must end whatever is left.
    /// </summary>
    public async Task<FolderRemovalReport> RemoveForAsync(string userId, CancellationToken ct = default)
    {
        if (ConciergeWorkspaces.TryExisting(paths, userId) is not { } workspace) return FolderRemovalReport.Done;

        try
        {
            return await removal.RemoveInsideAsync(workspace, Path.Combine(workspace, FolderName), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FolderRemovalReport([], ex.Message);
        }
    }

    /// <summary>
    /// <paramref name="revoke"/>, with this person's attachments removed first. What
    /// <see cref="ConciergeSessionStore"/> is given, so every way a session ends - a person's end,
    /// the reaper, the CLI exiting, shutdown - removes them. The revoke runs whatever the removal did.
    /// </summary>
    public ConciergeRevoke RemovingOnEnd(ConciergeRevoke revoke) => async (key, ct) =>
    {
        try
        {
            await RemoveForAsync(key.User, ct);
        }
        finally
        {
            await revoke(key, ct);
        }
    };

    /// <summary>
    /// Removes every attachment, in every person's Concierge folder, last written more than the
    /// retention before <paramref name="now"/>. Run at start, so a Host that was stopped rather
    /// than its sessions ended does not keep images forever. Returns what it could not remove.
    /// </summary>
    public async Task<IReadOnlyList<string>> RemoveExpiredAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        List<string> left = [];
        var root = paths.ConciergeWorkspacesRoot;

        if (!Directory.Exists(root)) return left;

        var cutoff = (now - retention).UtcDateTime;

        foreach (var workspace in Quietly(() => Directory.EnumerateDirectories(root)))
        {
            // Only a folder a person's marker claims is a Concierge workspace.
            if (TeamPaths.ConciergeOwnerOf(workspace) is null) continue;

            var folder = Path.Combine(workspace, FolderName);

            if (!Directory.Exists(folder) || IsLink(folder)) continue;

            foreach (var entry in Quietly(() => Directory.EnumerateFileSystemEntries(folder)))
            {
                DateTime written;

                try
                {
                    written = new FileInfo(entry).LastWriteTimeUtc;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                if (written >= cutoff) continue;

                var report = await removal.RemoveInsideAsync(folder, entry, ct);
                left.AddRange(report.Remaining);
                if (report.Refused is { } refused) left.Add(refused);
            }
        }

        return left;
    }

    /// <summary>The next free generated name, opened create-new. <c>n</c> counts up from 1 within
    /// one timestamp, so two images pasted in one millisecond both land.</summary>
    private static (string Path, FileStream Stream) Create(string folder, string extension, DateTimeOffset now)
    {
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture);

        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, $"{stamp}-{n}.{extension}");

            try
            {
                var options = new FileStreamOptions { Mode = System.IO.FileMode.CreateNew, Access = FileAccess.Write };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = FileMode;

                return (path, new FileStream(path, options));
            }
            catch (IOException) when (n < 1000 && (File.Exists(path) || IsLink(path)))
            {
                // Taken: by an image from the same instant, or by whatever an agent left there.
            }
        }
    }

    private ConciergeAttachmentRefused TooLarge() =>
        new(413, $"That image is larger than {maxBytes / (1024 * 1024)} MB, so it was not attached.");

    private static bool IsLink(string path)
    {
        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static List<string> Quietly(Func<IEnumerable<string>> list)
    {
        try
        {
            return [.. list()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
