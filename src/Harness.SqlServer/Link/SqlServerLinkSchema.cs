using Harness.Contracts;

namespace Harness.SqlServer.Link;

/// <summary>
/// SQLite's <c>LinkSchema</c> on SQL Server, step for step and with the same ids. That class's remarks
/// are the argument for the table. Text compares by code point, as SQLite's BINARY columns do; an
/// instant is NVARCHAR(40), as every module's is.
/// </summary>
public static class SqlServerLinkSchema
{
    public static IReadOnlyList<MigrationStep> Steps { get; } =
    [
        new MigrationStep(
            "link-001",
            """
            CREATE TABLE management_link (
                id                   INT           NOT NULL CONSTRAINT pk_management_link PRIMARY KEY
                                                   CONSTRAINT ck_management_link_one CHECK (id = 1),
                service_url          NVARCHAR(MAX) NOT NULL,
                service_name         NVARCHAR(MAX) NOT NULL,
                instance_name        NVARCHAR(MAX) NOT NULL,
                credential_protected NVARCHAR(MAX) NOT NULL,
                attached_at          NVARCHAR(40)  NOT NULL,
                state                NVARCHAR(40)  NOT NULL,
                state_since          NVARCHAR(40)  NOT NULL,
                reason               NVARCHAR(MAX) NULL,
                last_report_at       NVARCHAR(40)  NULL
            );
            """),
    ];
}
