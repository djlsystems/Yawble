namespace Harness.Contracts;

/// <summary>
/// HOW MUCH OF THE DIAGNOSTICS LOG IS KEPT, AND WHY IT IS NOT A CONSTANT.
///
/// <para>
/// This is the highest-volume store this product has. An unbounded one fills a disk, and the disk
/// it fills first is the small one - so the bound has to be <b>DERIVED OR DECLARED, never a number
/// tuned to one machine</b>, and it has to <b>DEGRADE ON A SMALL HOST RATHER THAN REFUSE</b>. A
/// store that stopped recording because it had reached a limit somebody typed would go quiet at
/// exactly the moment it is worth reading.
/// </para>
///
/// <para>
/// TWO BOUNDS, AND THEY ANSWER DIFFERENT QUESTIONS. Whichever bites first wins; neither can be
/// switched off.
/// </para>
///
/// <list type="number">
///   <item>
///     <b><see cref="MaxAge"/> is DECLARED.</b> How far back this log is worth reading is a policy
///     rather than a property of the hardware - a fortnight is chosen because the question this
///     store exists to answer is "what happened on the night of", and the night of is asked about
///     days later, not months. It is a named default here, overridable per instance, and it is the
///     same number on every machine BECAUSE it is not about the machine.
///   </item>
///   <item>
///     <b><see cref="MaxRows"/> is DERIVED</b> from the size of the volume the data root sits on.
///     A dev box with a terabyte keeps roughly a million rows; a small appliance keeps tens of
///     thousands; a tiny volume falls to <see cref="FloorRows"/> and keeps recording. Nothing
///     refuses, and nobody has to guess a number that is right in two places at once.
///   </item>
/// </list>
///
/// <para>
/// BOTH CAN BE DECLARED OUTRIGHT, and that is the escape hatch rather than the mechanism: an
/// operator who knows their instance can set either. Absence means the derived or default value,
/// which is the shape every other setting in this product has - absence means the safe value.
/// </para>
/// </summary>
/// <param name="MaxAge">Rows older than this are removed.</param>
/// <param name="MaxRows">At most this many rows are kept, newest first.</param>
public sealed record DiagnosticRetention(TimeSpan MaxAge, long MaxRows)
{
    /// <summary>
    /// A fortnight. Long enough that "what happened on the night of" is still answerable when
    /// somebody gets round to asking, short enough that an instance left running does not carry a
    /// year of 409s.
    /// </summary>
    public static readonly TimeSpan DefaultMaxAge = TimeSpan.FromDays(14);

    /// <summary>
    /// What one row costs on disk, near enough. Rows are short strings and a nullable integer; the
    /// long field is <see cref="DiagnosticEvent.Detail"/>, itself bounded by
    /// <see cref="DiagnosticRedaction.MaximumLength"/>.
    ///
    /// IT IS AN ESTIMATE AND IS ONLY EVER USED TO TURN BYTES INTO ROWS. Being wrong by a factor of
    /// two moves the derived bound by a factor of two, which is the difference between half a
    /// percent and a percent of a volume - not the difference between working and not. A measured
    /// number would be more precise and no more correct, because the true figure varies per row.
    /// </summary>
    public const int EstimatedBytesPerRow = 512;

    /// <summary>
    /// The share of the volume this store may occupy: one part in a thousand.
    ///
    /// SMALL ON PURPOSE. This is an observability sink, not the product's data; it must not be the
    /// reason a volume holding somebody's teams, transcripts and repositories runs out. A tenth of
    /// a percent of a 500GB disk is 500MB, which at the row size above is most of a million rows -
    /// far more than any investigation has ever needed.
    /// </summary>
    public const long VolumeShareDivisor = 1_000;

    /// <summary>
    /// THE SMALLEST NUMBER OF ROWS THIS STORE WILL EVER KEEP, whatever the volume says.
    ///
    /// This is the "degrade, do not refuse" half of the rule made concrete: on a volume too small
    /// for the derived share to mean anything, the answer is a short log rather than no log. Five
    /// thousand rows is a few megabytes and is still enough to hold one bad night.
    /// </summary>
    public const long FloorRows = 5_000;

    /// <summary>
    /// The largest derived bound. A very large volume should not produce a table nobody can page
    /// through and every query has to scan - past a point, more rows stop being more evidence.
    /// </summary>
    public const long CeilingRows = 2_000_000;

    /// <summary>
    /// The bound for one instance.
    ///
    /// <paramref name="declaredMaxAgeDays"/> and <paramref name="declaredMaxRows"/> are what an
    /// operator set, or null. Null on either means derive it. A declared value that is zero or
    /// negative is treated as absent rather than as "keep nothing": a typo in a setting must not be
    /// how a log gets emptied, which is the same reasoning the durability setting's unparseable
    /// value gets.
    ///
    /// <paramref name="volumeTotalBytes"/> is resolved by <see cref="ForDataRoot"/> in production.
    /// It is a parameter so the derivation can be tested without a disk of a chosen size.
    /// </summary>
    public static DiagnosticRetention Derive(
        long volumeTotalBytes,
        int? declaredMaxAgeDays = null,
        long? declaredMaxRows = null)
    {
        var age = declaredMaxAgeDays is > 0
            ? TimeSpan.FromDays(declaredMaxAgeDays.Value)
            : DefaultMaxAge;

        if (declaredMaxRows is > 0) return new DiagnosticRetention(age, declaredMaxRows.Value);

        // A volume that could not be measured derives the FLOOR, not the ceiling and not zero.
        // "I do not know how big this disk is" is the case where keeping less is the safe answer,
        // and keeping nothing is never one.
        var budget = volumeTotalBytes <= 0 ? 0 : volumeTotalBytes / VolumeShareDivisor;
        var rows = budget / EstimatedBytesPerRow;

        return new DiagnosticRetention(age, Math.Clamp(rows, FloorRows, CeilingRows));
    }

    /// <summary>
    /// The bound for the volume <paramref name="dataRoot"/> sits on.
    ///
    /// NEVER THROWS. A data root on a path `DriveInfo` cannot read - a UNC share, a path that has
    /// gone away - answers the floor, for the reason above: this is a retention bound, and failing
    /// to compute one must not be how a host fails to start.
    /// </summary>
    public static DiagnosticRetention ForDataRoot(
        string dataRoot, int? declaredMaxAgeDays = null, long? declaredMaxRows = null)
    {
        long total;

        try
        {
            total = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dataRoot))!).TotalSize;
        }
        catch
        {
            total = 0;
        }

        return Derive(total, declaredMaxAgeDays, declaredMaxRows);
    }
}
