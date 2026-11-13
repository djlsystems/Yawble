-- 0001 baseline. The app's one SQLite file (WAL mode), owned by the core alone.
-- Migrations are append-only: never edit a shipped file, add 0002_*.sql and so on.
-- This step creates only the bookkeeping; the numbered steps after it add the tables.
CREATE TABLE app_meta (
    key   TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
) STRICT;

INSERT INTO app_meta (key, value) VALUES ('created_by_schema', '0001_baseline');
