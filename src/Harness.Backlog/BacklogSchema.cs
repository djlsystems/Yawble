using Harness.Contracts;

namespace Harness.Backlog;

/// <summary>
/// The backlog's own schema module.
///
/// <para>
/// A MODULE OF ITS OWN, like <c>Harness.Skills</c>. The rule it satisfies: a step belongs to the
/// module that owns the table it touches, and its id prefix says which - a step in the wrong
/// module's list fails loudly on a fresh database and QUIETLY on an existing one. Not
/// <c>Harness.Identity</c>: it owns <c>teams</c>, but <c>backlog_items.team</c> is a visibility
/// link with deliberately no foreign key to it, so that module would own a table it has no
/// relationship with.
/// </para>
///
/// <para>
/// REGISTERED IN <c>SchemaModules</c>, WHICH IS THE ONE PLACE THE ORDER IS DECIDED. That file's own
/// comment records what happens to a module left out of it. <c>SchemaModuleCoverageTests</c> fails
/// the build on one that is, which is what makes adding a module the safe option rather than the
/// brave one.
/// </para>
///
/// <para>
/// A change is a NEW step after the last one; a shipped id is permanent.
/// </para>
///
/// <para>
/// THE STORED KEY IS THE CITATION. <c>backlog_items.id</c> holds <c>B001F</c>, exactly what
/// <see cref="PlatformBacklogId.Format"/> renders, so a row read out of a dump is the id a person
/// says. It is minted from <c>backlog_id_sequence</c>, a one-row counter bumped in the insert's own
/// transaction, because a TEXT key cannot be AUTOINCREMENT and an id must never be reused: MAX(id)+1
/// would hand a deleted item's citation to the next one. Four characters of Crockford base 32 sort
/// lexically in numeric order, so ORDER BY id is still creation order.
/// </para>
/// </summary>
public static class BacklogSchema
{
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "backlog-001",
            """
            CREATE TABLE backlog_id_sequence (
                id   INTEGER PRIMARY KEY CHECK (id = 1),
                last INTEGER NOT NULL
            );

            INSERT INTO backlog_id_sequence (id, last) VALUES (1, 0);

            CREATE TABLE backlog_items (
                id           TEXT    PRIMARY KEY,
                team         TEXT    NULL COLLATE NOCASE,
                title        TEXT    NOT NULL,
                body         TEXT    NOT NULL,
                state        TEXT    NOT NULL,
                position     REAL    NOT NULL,
                archived_at  TEXT    NULL,
                created_at   TEXT    NOT NULL,
                updated_at   TEXT    NOT NULL,
                created_by   TEXT    NOT NULL
            );

            CREATE INDEX backlog_items_position ON backlog_items(position);

            -- No foreign key to backlog_items, deliberately: a cascade would take the execution
            -- record with the item. DeleteAsync removes the dispatch rows itself.
            CREATE TABLE backlog_dispatches (
                id             INTEGER PRIMARY KEY AUTOINCREMENT,
                item           TEXT    NOT NULL,
                team_id        TEXT    NOT NULL COLLATE NOCASE,
                team_name      TEXT    NOT NULL,
                correlation    INTEGER NOT NULL,
                dispatched_at  TEXT    NOT NULL,
                dispatched_by  TEXT    NOT NULL,
                frozen_at      TEXT    NULL,
                frozen_stats   TEXT    NULL
            );

            CREATE INDEX backlog_dispatches_item ON backlog_dispatches(item, id);
            """),

        // LANDED SURVIVES CLEANUP (B0025). The work's tip per dispatch and repository, recorded
        // when the team's publish pushes its branch, and landed stored on the dispatch once proven
        // so it outlives the branch, the clone and the team. Nullable columns: every dispatch
        // before this step simply has not been proven yet.
        new MigrationStep(
            "backlog-002",
            """
            ALTER TABLE backlog_dispatches ADD COLUMN landed_at TEXT NULL;
            ALTER TABLE backlog_dispatches ADD COLUMN landed_sha TEXT NULL;
            ALTER TABLE backlog_dispatches ADD COLUMN landed_branch TEXT NULL;

            -- No foreign key, for the reason backlog_dispatches has none. DeleteAsync removes these.
            CREATE TABLE backlog_dispatch_tips (
                dispatch     INTEGER NOT NULL,
                repo         TEXT    NOT NULL COLLATE NOCASE,
                sha          TEXT    NOT NULL,
                recorded_at  TEXT    NOT NULL,
                PRIMARY KEY (dispatch, repo)
            );
            """),
    ];
}
