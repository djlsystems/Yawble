using Harness.Contracts;

namespace Harness.Skills;

/// <summary>
/// The skill index, as one step: see <c>MessageSchema</c> for what that means for an existing
/// volume. A change is a NEW step after this one; this id is permanent.
/// </summary>
public static class SkillSchema
{
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "skill-001",
            """
            CREATE TABLE skills (
                id           INTEGER PRIMARY KEY,
                team_id      TEXT,
                name         TEXT NOT NULL,
                description  TEXT NOT NULL,
                body         TEXT NOT NULL,
                path         TEXT NOT NULL,
                gated        INTEGER NOT NULL DEFAULT 0,
                status       TEXT NOT NULL,
                version      TEXT,
                indexed_at   TEXT NOT NULL
            );

            CREATE UNIQUE INDEX skills_scope_name
              ON skills(IFNULL(team_id, ''), status, name);

            CREATE VIRTUAL TABLE skills_fts USING fts5(
              name, description, body,
              content='skills', content_rowid='id',
              tokenize='porter unicode61'
            );

            CREATE TRIGGER skills_ai AFTER INSERT ON skills BEGIN
              INSERT INTO skills_fts(rowid, name, description, body)
              VALUES (new.id, new.name, new.description, new.body);
            END;

            CREATE TRIGGER skills_ad AFTER DELETE ON skills BEGIN
              INSERT INTO skills_fts(skills_fts, rowid, name, description, body)
              VALUES('delete', old.id, old.name, old.description, old.body);
            END;

            CREATE TRIGGER skills_au AFTER UPDATE ON skills BEGIN
              INSERT INTO skills_fts(skills_fts, rowid, name, description, body)
              VALUES('delete', old.id, old.name, old.description, old.body);
              INSERT INTO skills_fts(rowid, name, description, body)
              VALUES (new.id, new.name, new.description, new.body);
            END;
            """),

        // Built-in skills come from the build and custom skills live here, in the row, with
        // no file behind them. The table this drops is a projection of files on disk, so dropping it
        // loses nothing: the first start moves those files to backups/skills-before-builtins and
        // imports the custom ones. No skill is team-scoped or gated, and every skill declares its
        // roles, stored as the space-separated role names.
        new MigrationStep(
            "skill-002",
            """
            DROP TRIGGER IF EXISTS skills_ai;
            DROP TRIGGER IF EXISTS skills_ad;
            DROP TRIGGER IF EXISTS skills_au;
            DROP TABLE IF EXISTS skills_fts;
            DROP TABLE IF EXISTS skills;

            CREATE TABLE skills (
                id           INTEGER PRIMARY KEY,
                name         TEXT NOT NULL COLLATE NOCASE UNIQUE,
                kind         TEXT NOT NULL CHECK (kind IN ('builtin', 'custom')),
                description  TEXT NOT NULL,
                roles        TEXT NOT NULL,
                body         TEXT NOT NULL,
                updated_at   TEXT NOT NULL,
                updated_by   TEXT
            );

            CREATE VIRTUAL TABLE skills_fts USING fts5(
              name, description, body,
              content='skills', content_rowid='id',
              tokenize='porter unicode61'
            );

            CREATE TRIGGER skills_ai AFTER INSERT ON skills BEGIN
              INSERT INTO skills_fts(rowid, name, description, body)
              VALUES (new.id, new.name, new.description, new.body);
            END;

            CREATE TRIGGER skills_ad AFTER DELETE ON skills BEGIN
              INSERT INTO skills_fts(skills_fts, rowid, name, description, body)
              VALUES('delete', old.id, old.name, old.description, old.body);
            END;

            CREATE TRIGGER skills_au AFTER UPDATE ON skills BEGIN
              INSERT INTO skills_fts(skills_fts, rowid, name, description, body)
              VALUES('delete', old.id, old.name, old.description, old.body);
              INSERT INTO skills_fts(rowid, name, description, body)
              VALUES (new.id, new.name, new.description, new.body);
            END;
            """),

        // PLUGIN SKILLS: a third kind, `plugin`, and the id of the plugin that ships the row in
        // `source` (null for every other kind). A CHECK cannot be altered in SQLite, so the table
        // is rebuilt here, every row and id kept, and the full-text index rebuilt from it.
        new MigrationStep(
            "skill-003",
            """
            DROP TRIGGER IF EXISTS skills_ai;
            DROP TRIGGER IF EXISTS skills_ad;
            DROP TRIGGER IF EXISTS skills_au;

            CREATE TABLE skills_rebuilt (
                id           INTEGER PRIMARY KEY,
                name         TEXT NOT NULL COLLATE NOCASE UNIQUE,
                kind         TEXT NOT NULL CHECK (kind IN ('builtin', 'custom', 'plugin')),
                description  TEXT NOT NULL,
                roles        TEXT NOT NULL,
                body         TEXT NOT NULL,
                updated_at   TEXT NOT NULL,
                updated_by   TEXT,
                source       TEXT NULL,
                CHECK ((kind = 'plugin') = (source IS NOT NULL))
            );

            INSERT INTO skills_rebuilt (id, name, kind, description, roles, body, updated_at, updated_by, source)
            SELECT id, name, kind, description, roles, body, updated_at, updated_by, NULL FROM skills;

            DROP TABLE skills;
            ALTER TABLE skills_rebuilt RENAME TO skills;

            CREATE INDEX skills_source ON skills(source) WHERE source IS NOT NULL;

            CREATE TRIGGER skills_ai AFTER INSERT ON skills BEGIN
              INSERT INTO skills_fts(rowid, name, description, body)
              VALUES (new.id, new.name, new.description, new.body);
            END;

            CREATE TRIGGER skills_ad AFTER DELETE ON skills BEGIN
              INSERT INTO skills_fts(skills_fts, rowid, name, description, body)
              VALUES('delete', old.id, old.name, old.description, old.body);
            END;

            CREATE TRIGGER skills_au AFTER UPDATE ON skills BEGIN
              INSERT INTO skills_fts(skills_fts, rowid, name, description, body)
              VALUES('delete', old.id, old.name, old.description, old.body);
              INSERT INTO skills_fts(rowid, name, description, body)
              VALUES (new.id, new.name, new.description, new.body);
            END;

            INSERT INTO skills_fts(skills_fts) VALUES('rebuild');
            """),

        // TEAM SKILLS: a custom skill may belong to one team (`team`, the team's stored id; NULL for
        // every instance-wide skill, which is every row before this step). A name is unique among
        // the instance-wide skills and within one team, so two teams installed from one package may
        // each hold the same skill name; the store refuses a team skill an instance-wide name and
        // the other way round, so nothing is shadowed. The column-level UNIQUE on name cannot be
        // dropped in SQLite, so the table is rebuilt, every row and id kept, and the index with it.
        new MigrationStep(
            "skill-004",
            """
            DROP TRIGGER IF EXISTS skills_ai;
            DROP TRIGGER IF EXISTS skills_ad;
            DROP TRIGGER IF EXISTS skills_au;
            DROP INDEX IF EXISTS skills_source;

            CREATE TABLE skills_rebuilt (
                id           INTEGER PRIMARY KEY,
                name         TEXT NOT NULL COLLATE NOCASE,
                kind         TEXT NOT NULL CHECK (kind IN ('builtin', 'custom', 'plugin')),
                description  TEXT NOT NULL,
                roles        TEXT NOT NULL,
                body         TEXT NOT NULL,
                updated_at   TEXT NOT NULL,
                updated_by   TEXT,
                source       TEXT NULL,
                team         TEXT NULL COLLATE NOCASE,
                CHECK ((kind = 'plugin') = (source IS NOT NULL)),
                CHECK (team IS NULL OR kind = 'custom')
            );

            INSERT INTO skills_rebuilt (id, name, kind, description, roles, body, updated_at, updated_by, source, team)
            SELECT id, name, kind, description, roles, body, updated_at, updated_by, source, NULL FROM skills;

            DROP TABLE skills;
            ALTER TABLE skills_rebuilt RENAME TO skills;

            CREATE UNIQUE INDEX skills_instance_name ON skills(name) WHERE team IS NULL;
            CREATE UNIQUE INDEX skills_team_name ON skills(team, name) WHERE team IS NOT NULL;
            CREATE INDEX skills_source ON skills(source) WHERE source IS NOT NULL;

            CREATE TRIGGER skills_ai AFTER INSERT ON skills BEGIN
              INSERT INTO skills_fts(rowid, name, description, body)
              VALUES (new.id, new.name, new.description, new.body);
            END;

            CREATE TRIGGER skills_ad AFTER DELETE ON skills BEGIN
              INSERT INTO skills_fts(skills_fts, rowid, name, description, body)
              VALUES('delete', old.id, old.name, old.description, old.body);
            END;

            CREATE TRIGGER skills_au AFTER UPDATE ON skills BEGIN
              INSERT INTO skills_fts(skills_fts, rowid, name, description, body)
              VALUES('delete', old.id, old.name, old.description, old.body);
              INSERT INTO skills_fts(rowid, name, description, body)
              VALUES (new.id, new.name, new.description, new.body);
            END;

            INSERT INTO skills_fts(skills_fts) VALUES('rebuild');
            """),
    ];
}
