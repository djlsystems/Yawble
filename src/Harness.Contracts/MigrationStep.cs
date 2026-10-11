namespace Harness.Contracts;

/// <summary>
/// One forward step in the database's schema, as DATA.
///
/// It lives here, carrying no SQLite type, because two modules own tables in one file and neither
/// can see the other: `Harness.Messaging` owns the message log, `Harness.Identity` owns
/// everything else, and they are siblings. Each publishes an ordered list of these and the Host
/// composes them - which is the same rule that keeps a Microsoft.Data.Sqlite reference off the Host.
/// </summary>
/// <param name="Id">Stable, module-prefixed, and NEVER renumbered - it is the primary key of the
/// applied record, so renaming one re-runs it against every existing database.</param>
/// <param name="Sql">Runs inside a transaction that also writes <paramref name="Id"/>.</param>
/// <param name="SkipWhen">A scalar query; non-zero means the step is recorded as applied WITHOUT
/// running. It exists for exactly one reason: SQLite has no ALTER TABLE ADD COLUMN IF NOT EXISTS,
/// so the column adds that reached the live database through AddColumnIfMissing cannot be written
/// as idempotent SQL. It is for ADOPTION only - a step written after adoption runs exactly once and
/// needs no probe, and reaching for this on a new step means writing a migration that does not know
/// whether it has run.</param>
public sealed record MigrationStep(string Id, string Sql, string? SkipWhen = null);
