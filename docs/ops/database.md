# The database: SQLite or SQL Server

Yawble keeps its state (teams, accounts, the message log, the board, the ledgers, settings, the backlog and skills) in one database. It runs on either of two engines:

| | SQLite (the default) | SQL Server |
|---|---|---|
| Where the data is | `messages.db` in the data root (`/data/messages.db` in the container) | A database on a SQL Server you run, named by a connection string |
| What to set | Nothing | `HARNESS_DATABASE=sqlserver` and `HARNESS_DATABASE_CONNECTION` |
| Versions | The SQLite bundled with the Host | SQL Server 2022 or later, or Azure SQL Database |
| Backups | The Host's own daily copy, `--backup` and `--restore` | The server's own backups and point-in-time restore |
| Schema | Moved forward by the Host at start, after a copy | Moved forward by the Host at start, with no copy |

SQLite is the reference. Every behaviour is defined there first, and the SQL Server stores are held to it by contract tests that run the same cases on both engines.

Everything else still lives in the data root on either engine: keys, team folders and git clones, local repositories, documents, sites, logs and agent logins. Only the database moves.

## The two settings

| Environment variable | Or the setting | Value |
|---|---|---|
| `HARNESS_DATABASE` | `Database:Provider` | `sqlite` (the default) or `sqlserver`. Any other value stops the Host with a sentence naming it and the key it was read from. |
| `HARNESS_DATABASE_CONNECTION` | `Database:Connection` | A SqlClient connection string. Required with `sqlserver`; without one the Host does not start. |

Set them where the control Host reads its settings: its environment, or `appsettings.json` (a `Database` section with `Provider` and `Connection`, or `Database__Provider` and `Database__Connection` in the environment). The environment variable wins when both are set, and a blank value counts as unset. Workers open no database and need neither.

A bare `Database` or `DatabaseConnection` key is not read. Every environment variable is a setting and names compare without case, so a bare key would pick up an unrelated `DATABASE=...` that another tool set and stop a SQLite Host that was never asked to change. A value set under one of those keys is ignored, and the Host starts on SQLite: move it to `HARNESS_DATABASE` or `Database:Provider`, and to `HARNESS_DATABASE_CONNECTION` or `Database:Connection`.

For example:

```
HARNESS_DATABASE=sqlserver
HARNESS_DATABASE_CONNECTION=Server=tcp:sql.example.net,1433;Database=yawble;User ID=yawble;Password=...;Encrypt=True
```

Managed identity is the connection string's own setting: `Authentication=Active Directory Managed Identity` (or `Active Directory Default`) and no password. Yawble reads no credential of its own.

The Host adds `ConnectRetryCount=3` and `Pooling=true` when the string does not set them, so a pooled connection that died in a failover is reopened. A value the string sets is kept. Which faults the Host itself runs again is under [How SQL Server differs from SQLite](#how-sql-server-differs-from-sqlite-and-where-it-does-not).

The Host never prints or records the connection string. A sentence about the database names the server and nothing else of it, and `--doctor` reports the server's and the database's names only.

## Creating the database

The Host refuses a database it cannot run on correctly, with one sentence saying what to change. It checks two settings before it writes anything, so a refused database is left exactly as it was.

**The collation must be `Latin1_General_100_BIN2_UTF8`.** SQLite compares text by code point, so `Alpha` and `alpha` are two team names there. SQL Server's usual default collation is case-insensitive, which would make them one, and would change which names are unique and which lookups match. Create the database with the binary collation:

```sql
CREATE DATABASE yawble COLLATE Latin1_General_100_BIN2_UTF8;
```

Under this collation table and column names are case-sensitive too.

**`READ_COMMITTED_SNAPSHOT` must be on.** The stores are written for readers that never wait on a writer. Azure SQL Database has it on. A SQL Server you install has it off, so turn it on:

```sql
ALTER DATABASE yawble SET READ_COMMITTED_SNAPSHOT ON;
```

The statement waits for other connections to the database to close.

The login needs to create and change tables in that database, because the Host moves the schema forward itself at every start (`db_owner` on the database is enough). It needs no rights on any other database.

## Starting

On a sqlserver start the Host:

1. reads the two settings, and stops with a sentence if `sqlserver` has no connection string or the string cannot be read;
2. takes the database's control lock (see [One control process per database](#one-control-process-per-database)), and stops with a sentence if another Host holds it;
3. checks the collation and `READ_COMMITTED_SNAPSHOT`, then applies every schema step the database has not taken. When a step ran it says once: `Schema updated. No backup was taken first: point-in-time restore is the backup on SQL Server.` One apply runs at a time: a start and an operator command that both find steps to apply take turns on the `schema.migrate` application lock, and the second finds the steps applied;
4. serves.

It never creates, opens or migrates `messages.db`. A server it cannot reach, a database from a newer build, or a step that failed is one sentence and a non-zero exit, and nothing is served.

## Backups

On SQLite the Host copies the database itself: a daily copy, a copy before each schema step, `--backup` and `--restore`. See [backups-logs-and-versions.md](backups-logs-and-versions.md).

On SQL Server the database is not a file the Host can copy, so the backups are the server's own. Azure SQL Database takes them automatically and restores to a point in time. A SQL Server you run is backed up with its own full and log backups. Therefore:

- The daily copy takes none, and logs that point-in-time restore is the backup.
- No copy is taken before a schema step. Restore to a point in time before the start if a step has to be undone.
- `--backup` and `--restore` change nothing and exit non-zero: `No copy was written: point-in-time restore is the backup on SQL Server.` and `Nothing was restored: point-in-time restore is the backup on SQL Server.`
- `yawble backup` still archives the data root (keys, team folders, documents and the rest), but the database is not in it, and it says so in one line: `the database is on SQL Server and is not in this backup: its backups are the SQL Server's own (point-in-time restore)`. It reads the provider from the running instance's doctor, or from the `HARNESS_DATABASE` it hands a stopped one. Back up both: the server's backups for the database, `yawble backup` for the volume. Restore them to the same point.

## Operator commands

| Switch | SQLite | SQL Server |
|---|---|---|
| `--backup [path]` | Writes a consistent copy of `messages.db` | Refuses, naming point-in-time restore |
| `--restore <path>` | Replaces `messages.db` with a backup | Refuses, naming point-in-time restore |
| `--reset-password <email>` | Moves the schema forward, sets a new password | The same, on the SQL Server database |
| `--list-users` | Moves the schema forward, lists the accounts | The same, on the SQL Server database |
| `--doctor` | Reports the file, its schema and the backups folder | Reports `provider: sqlserver`, the server and database names, the applied steps, and `backups: null` |
| `--solution-check <folder>` | Reads no database | Reads no database |

The Host reads `HARNESS_DATABASE` for these switches as it does for a start, so run them with the same settings the instance uses. On SQL Server no switch creates `messages.db`.

`yawble doctor` reads the `--doctor` report. On SQL Server its backups line is ok and says the backups are the SQL Server's own (point-in-time restore), since the Host takes no copy. A database it cannot read points at the server, `HARNESS_DATABASE_CONNECTION` and the server's point-in-time restore, not at `--restore`.

On SQL Server a refusal is printed to stderr, as a refused start is, and the exit code is non-zero.

## How SQL Server differs from SQLite, and where it does not

The SQL Server stores keep SQLite's meaning column by column.

- **Text compares by code point**, through the database collation, as SQLite's default `BINARY` does.
- **A column SQLite declares `COLLATE NOCASE`** (team ids, member names, repository names) takes `Latin1_General_100_CI_AS_SC_UTF8`, so it folds case in its key, its index and its comparisons. SQLite folds ASCII letters only, and SQL Server folds every letter and treats `ß` as `ss`. The two agree on every team and member name, which are ASCII letters, digits, `-` and `_`. They can differ only on free text that nothing compares this way. Ordering such a column sorts by its lower-cased code points, as SQLite's `NOCASE` does.
- **`LIKE` folds ASCII case** on SQLite, so every SQL Server `LIKE` names the case-insensitive collation.
- **Unbounded text that is a key** is keyed by its hash. An index key on SQL Server holds at most 900 bytes, and a repository name or a removal's path has no length limit. Such a column is `NVARCHAR(MAX)`, and its key is a persisted SHA-256 of it (lower-cased where SQLite's key folds case). Every match names the hash and the text, so the hash finds the row and the text decides it.
- **Timestamps** stay round-trip text, so they sort and parse identically. `INTEGER` is `BIGINT`, booleans stay integers and JSON stays text.
- **Skill search** is SQLite's full-text search (FTS5 with `porter unicode61`) ported to code. SQL Server's full-text search is an optional component a server may not have, so the SQL Server store matches a query the way the FTS5 index does: the same tokenizer tables, the same stemmer, the same phrases. `SkillSearchPortTests` holds the port to the bundled SQLite over a corpus.
- **Two writers at once** are serialised by SQLite. On SQL Server every read-then-write runs in one transaction that holds what it read, and a deadlock victim is run again, up to three attempts in all. One that still fails, or a lock or command that waited out its timeout, is recorded in diagnostics as `db.busy` or `db.busy-timeout-expired`, as SQLite's busy file is.
- **A transient fault** (a failover or reconfiguration, throttling, a dropped connection: Azure SQL's documented transient errors 4060, 4221, 10928, 10929, 40197, 40501, 40613, 49918, 49919 and 49920, and the network errors 64, 233, 10053, 10054 and 10060) is run again, up to three attempts in all, only where nothing can have committed: while the connection opens, or inside a transaction whose commit has not been sent. A fault on the commit itself, or after a statement that commits on its own, reaches the request: the write may have landed, and running it again would store it twice. A command or lock timeout is not retried.

## One control process per database

Two instances must not share a SQL Server database: nothing in the schema separates one instance's rows from another's, and two control Hosts would each run the delivery pump, the sweeps, the wakes and retention over the same rows. On SQLite the data root's lock refuses a second Host. On SQL Server the data roots can differ, so the database itself holds the lock:

- A serving Host holds an exclusive application lock, `host.control`, owned by a session on a connection of its own for as long as it runs.
- A second Host on the same database is refused before it changes anything, with one sentence naming the server and database: `Another control Host is already running on the SQL Server database '<server>/<database>': ...`. The first goes on serving.
- The lock belongs to the session, so a Host that is killed releases it with its session, and the next start is not refused by a lock nobody holds.
- Every 10 seconds the Host checks that its session still holds the lock. When the connection has dropped (a failover, a network break), it says so and takes the lock again on a new connection, and keeps trying while the server cannot be reached. If another Host took the lock in the meantime, this Host says so and stops with a non-zero exit, so two Hosts never go on serving one database.
- Operator commands (`--reset-password`, `--list-users`, `--doctor` and the rest) never take the lock, just as they never take the data root's lock on SQLite. They run beside a live Host.

## Limits

- **One control process per database.** Enforced as described above.
- **No move from SQLite to SQL Server.** An instance that has run on SQLite cannot carry its data over. A SQL Server instance starts from an empty database.
- **PostgreSQL is not supported.** The code shared by the two engines holds no T-SQL, so a third engine stays possible, but none exists.

## Writing a schema step for both engines

A schema change is a new step, never an edit to one that has shipped (see `AGENTS.md`). It is written for both engines, with the same id.

SQL Server code has two homes. A store whose interface lives in Contracts, Messaging, Identity, Backlog or Skills has its SQL Server class in `src/Harness.SqlServer/`. A store whose interface the Host owns has its class in `src/Harness.Host/SqlServer/`, since `Harness.SqlServer` cannot reference the Host without a cycle.

SQL Server does not replay SQLite's history. Each module has a SQL Server baseline step (`<module>.sqlserver-baseline-1`, and for auth `-2` and `-3`) that creates its tables as they stand after the SQLite steps at the time the baselines were cut. From there the two lists grow together:

1. Add the SQLite step to its module's `Steps` (for example `backlog-005` in `BacklogSchema`), and its SHA-256 to `ShippedSchemaStepsTests`.
2. Add a SQL Server step with **the same id** to the module's SQL Server schema (`SqlServerBacklogSchema`). Translate as the baselines do: `BIGINT` for `INTEGER`, `BIGINT IDENTITY(1,1)` for an autoincrement key, `NVARCHAR(40)` for an instant, `NVARCHAR(450)` or narrower for keyed or indexed text and `NVARCHAR(MAX)` otherwise, `COLLATE` `SqlServerSql.NoCase` where SQLite says `COLLATE NOCASE`, and names in exactly SQLite's case. A step is one batch with no `GO`, and runs in a transaction.
3. Change both stores, and add the behaviour to the store's contract test so it runs on both engines.

`SchemaParityTests` pins it:

- `Every_step_after_the_baseline_exists_in_both` fails on a step id written for one engine only. Its baseline lists are literals, so a new step is never exempt.
- `Tables_match` compares every table of the two fresh databases: the same tables, the same column names in the same case, the same nullability, and the same collation intent (a `NOCASE` column folds on SQL Server, a binary one does not).

The SQL Server tests run when `HARNESS_TEST_SQLSERVER` holds a connection string to a server (database `master`) on which the tests may create and drop databases. `scripts/test-in-container.sh` starts one. Without the variable they are skipped.
