-- 0054 the team setting "Allow localhost (macOS)" (B119): whether a team's macOS runs may open local ports and connect to
-- any service on the Mac's loopback (classic VSTest `dotnet test` needs it). Off by default; the person turns it on per
-- team (teams.localhost, audited, a full-access change). Windows and Linux runs do not read it.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
ALTER TABLE teams ADD COLUMN allow_localhost INTEGER NOT NULL DEFAULT 0;
