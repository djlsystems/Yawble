using Harness.Contracts;

namespace Harness.Host;

/// <summary>
/// Transcripts as plain files inside the TEAM'S OWN ROOT - `&lt;teamRoot&gt;/transcripts/&lt;Member&gt;/
/// &lt;seq&gt;.txt` - which is why this takes <see cref="TeamPaths"/> rather than a data root: a team
/// can be placed on another volume, so there is no data root to be "beside".
///
/// Files rather than rows because the consumer is a human with a text editor, and because an
/// unbounded blob is exactly what the database should not be asked to hold — the log stays cheap to
/// read precisely by not carrying this.
/// </summary>
public sealed class FileTranscriptStore(TeamPaths paths) : ITranscriptStore
{
    public async Task<TranscriptRef> WriteAsync(
        ContainerId container, long causeSeq, string content, CancellationToken ct = default)
    {
        var path = PathFor(container, causeSeq);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await File.WriteAllTextAsync(path, content, ct);

        return new TranscriptRef(container, causeSeq);
    }

    public async Task<string?> ReadAsync(TranscriptRef reference, CancellationToken ct = default)
    {
        var path = PathFor(reference.Container, reference.CauseSeq);

        return File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : null;
    }

    /// <summary>
    /// One directory per team, one per container inside it - NOT one segment named for the
    /// qualified id.
    ///
    /// Names arrive over the API and are therefore untrusted input on a path, and they are still
    /// REJECTED rather than sanitised - the rejection has simply moved to where the identity is
    /// built. Silently rewriting a name would put a container's transcripts somewhere neither it nor
    /// a human would think to look, and the reference in the payload would point at a file that is
    /// not there.
    /// </summary>
    private string PathFor(ContainerId container, long causeSeq)
    {
        ArgumentNullException.ThrowIfNull(container);

        // Both parts are validated by ContainerId's constructor - neither can contain a separator,
        // `..`, or a character invalid in a file name - so this composes two safe segments rather
        // than re-checking one unsafe string. The qualified form CANNOT be a single segment: `/` is
        // the qualifier, and a guard on the whole string would reject it as an invalid file-name
        // character while MemberRuntime swallowed the exception, so a qualified container would
        // write nothing at all.
        return Path.Combine(paths.TranscriptsFor(container), $"{causeSeq}.txt");
    }
}
