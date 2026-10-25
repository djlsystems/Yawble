using System.Data.Common;
using System.Globalization;
using System.Text;
using Harness.Contracts;

namespace Harness.Identity;

/// <summary>
/// WHAT A SITE STORE READS OUT OF <c>sites</c>, <c>site_versions</c> AND <c>site_documents</c>, AND
/// WHAT A DATA WRITE IS REFUSED FOR, whichever database holds the rows: the column lists, the row
/// readers and the limit checks. Both providers' site stores call these, so a row either writes reads
/// the same on the other and a write past a bound is refused with the same sentence; only the SQL
/// differs.
///
/// Readers take <see cref="DbDataReader"/> and read every integer with <c>GetInt64</c>, narrowing
/// where the record holds an <c>int</c>: SQLite's INTEGER and SQL Server's BIGINT both answer it.
/// </summary>
public static class SiteRows
{
    /// <summary>The columns <see cref="ReadSite"/> reads, in its order.</summary>
    public const string SiteColumns = "team, name, live_version, next_version, created_at, created_by";

    /// <summary>The columns <see cref="ReadVersion"/> reads, in its order.</summary>
    public const string VersionColumns = "version, published_at, published_by, source, files, bytes";

    /// <summary>The columns <see cref="ReadDocument"/> reads, in its order.</summary>
    public const string DocumentColumns = "collection, id, doc, updated_at, updated_by";

    public static SiteRow ReadSite(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : checked((int)reader.GetInt64(2)),
            checked((int)reader.GetInt64(3)),
            DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
            reader.GetString(5));

    public static SiteVersionRow ReadVersion(DbDataReader reader) =>
        new(
            checked((int)reader.GetInt64(0)),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            reader.GetString(2),
            reader.GetString(3),
            checked((int)reader.GetInt64(4)),
            reader.GetInt64(5));

    public static SiteDocument ReadDocument(DbDataReader reader) =>
        new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture),
            reader.GetString(4));

    /// <summary>A site's usage from its two aggregates: the document count, then the byte total.</summary>
    public static SiteDataUsage ReadUsage(DbDataReader reader) =>
        new(checked((int)reader.GetInt64(0)), reader.GetInt64(1));

    /// <summary>A document's size as the limits count it: its JSON text in UTF-8 bytes.</summary>
    public static int Bytes(string json) => Encoding.UTF8.GetByteCount(json);

    /// <summary>The refusal for one document over <see cref="SiteRules.MaxDocumentBytes"/>, or null.
    /// Checked before any transaction opens: it needs nothing stored.</summary>
    public static SiteWriteOutcome? TooLarge(int bytes) =>
        bytes > SiteRules.MaxDocumentBytes ? SiteWriteOutcome.Refused(SiteRules.DocumentTooLarge(bytes), 413) : null;

    /// <summary>The refusal for a NEW document in a collection already holding
    /// <paramref name="documents"/>, or null.</summary>
    public static SiteWriteOutcome? CollectionFull(string collection, long documents) =>
        documents >= SiteRules.MaxDocumentsPerCollection
            ? SiteWriteOutcome.Refused(
                $"The collection \"{collection}\" already holds {SiteRules.MaxDocumentsPerCollection} documents, "
                + "the most one collection may hold. Delete some, or use another collection.", 413)
            : null;

    /// <summary>
    /// The refusal for a write of <paramref name="bytes"/> to a site holding <paramref name="used"/>
    /// bytes, of which <paramref name="existing"/> are the document being replaced, or null.
    /// </summary>
    public static SiteWriteOutcome? SiteFull(string site, long used, long? existing, int bytes) =>
        used - (existing ?? 0) + bytes > SiteRules.MaxSiteDataBytes
            ? SiteWriteOutcome.Refused(
                $"This write would take the site \"{site}\" past its limit of {SiteRules.MaxSiteDataBytes} bytes "
                + $"(50 MB) of data; it holds {used} bytes now. Delete documents it no longer needs.", 413)
            : null;
}
