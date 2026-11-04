using Harness.Contracts;

namespace Harness.Host.Link;

/// <summary>
/// The stored link to a management service: <c>management_link</c>, at most one row. The <c>id</c> is
/// always 1 (the CHECK refuses any other), so a second link cannot be stored beside the first; a new
/// attach replaces the row. <c>credential_protected</c> is Data Protection ciphertext
/// (<see cref="LinkCredential"/>), never the credential itself. An instance never attached has the
/// empty table and nothing else.
///
/// <para>
/// A MODULE OF ITS OWN, registered in <c>SchemaModules</c>, with its SQL Server twin of the same id in
/// <c>SqlServerLinkSchema</c>. A shipped step is never edited; a change is a NEW step after the last one.
/// </para>
/// </summary>
public static class LinkSchema
{
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "link-001",
            """
            CREATE TABLE management_link (
                id                   INTEGER PRIMARY KEY CHECK (id = 1),
                service_url          TEXT NOT NULL,
                service_name         TEXT NOT NULL,
                instance_name        TEXT NOT NULL,
                credential_protected TEXT NOT NULL,
                attached_at          TEXT NOT NULL,
                state                TEXT NOT NULL,
                state_since          TEXT NOT NULL,
                reason               TEXT NULL,
                last_report_at       TEXT NULL
            );
            """),
    ];
}
