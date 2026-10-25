using Harness.Contracts;

namespace Harness.SqlServer.Skills;

/// <summary>
/// The skill index on SQL Server, as SQLite's <c>SkillSchema</c> leaves it after <c>skill-004</c>, in
/// one baseline step. That class's remarks are the argument for the table, its kinds and its two name
/// scopes; this one says only where the dialect makes a difference.
///
/// <para>
/// Types follow the other baselines' rules: BIGINT for INTEGER, NVARCHAR(40) for an instant, NVARCHAR(MAX)
/// for text no key or index holds, and every column SQLite declares <c>COLLATE NOCASE</c> takes
/// <see cref="SqlServerSql.NoCase"/>. <c>id</c> is an IDENTITY: SQLite's <c>INTEGER PRIMARY KEY</c> is
/// assigned the same way, one past the highest, except that SQLite may hand a deleted highest id out
/// again and SQL Server never does; listings order by it and page below it, which both keep.
/// </para>
///
/// <para>KEYED TEXT IS AS WIDE AS WHAT BOUNDS IT, and no wider:</para>
/// <list type="bullet">
///   <item><c>name</c> NVARCHAR(100): every name a store writes is a legal skill name, 48 characters at
///     most (<c>SkillRules.RequireLegalName</c>, on every draft before any write, and the free name a
///     displaced skill moves to, which <c>SkillRules.FreeNames</c> keeps within 48).</item>
///   <item><c>team</c> NVARCHAR(200), the width of <c>teams.id</c>: a team skill is written for a team's
///     stored id (<c>TeamSkills.TeamOf</c>), a container name of at most 32 characters.</item>
///   <item><c>source</c> NVARCHAR(100): a plugin id, at most 48 characters
///     (<c>MemberRef.IsValidPluginId</c>, checked when the manifest is read), and in the store bounded by
///     the name its skills must carry (<c>plugin-&lt;source&gt;</c>, 48 at most).</item>
///   <item><c>kind</c> NVARCHAR(16): one of three words, which the CHECK holds it to.</item>
/// </list>
///
/// <para>
/// NO FULL-TEXT INDEX. SQLite searches an FTS5 table; full-text search on SQL Server is an optional
/// component a server may not have, so the SQL Server store searches the three columns the FTS table
/// held with <c>LIKE</c> (<c>SqlServerSkillStore</c> says how the two differ).
/// </para>
/// </summary>
public static class SqlServerSkillSchema
{
    private const string NoCase = SqlServerSql.NoCase;

    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "skill.sqlserver-baseline-1",
            $"""
             CREATE TABLE skills (
                 id           BIGINT IDENTITY(1,1) NOT NULL PRIMARY KEY,
                 name         NVARCHAR(100) COLLATE {NoCase} NOT NULL,
                 kind         NVARCHAR(16)  NOT NULL CHECK (kind IN (N'builtin', N'custom', N'plugin')),
                 description  NVARCHAR(MAX) NOT NULL,
                 roles        NVARCHAR(MAX) NOT NULL,
                 body         NVARCHAR(MAX) NOT NULL,
                 updated_at   NVARCHAR(40)  NOT NULL,
                 updated_by   NVARCHAR(MAX) NULL,
                 source       NVARCHAR(100) NULL,
                 team         NVARCHAR(200) COLLATE {NoCase} NULL,
                 CHECK ((kind = N'plugin' AND source IS NOT NULL) OR (kind <> N'plugin' AND source IS NULL)),
                 CHECK (team IS NULL OR kind = N'custom')
             );

             CREATE UNIQUE INDEX skills_instance_name ON skills(name) WHERE team IS NULL;
             CREATE UNIQUE INDEX skills_team_name ON skills(team, name) WHERE team IS NOT NULL;
             CREATE INDEX skills_source ON skills(source) WHERE source IS NOT NULL;
             """),
    ];
}
