-- 0059 roles and who a Manager may hire.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
--
-- roles: the role catalog (Settings > Teams > Roles). name is one word and is also the name word a hire gets; job is the
-- short job description that becomes a hire's standing instructions; agent and model are the suggested agent (a built-in
-- id or a custom agent's name) and model (NULL: the agent's default). Rows are listed in id order.
CREATE TABLE roles (
    id         INTEGER PRIMARY KEY AUTOINCREMENT,
    name       TEXT    NOT NULL UNIQUE COLLATE NOCASE,
    job        TEXT    NOT NULL,
    agent      TEXT    NOT NULL,
    model      TEXT    NULL,
    updated_at TEXT    NOT NULL
) STRICT;

INSERT INTO roles (name, job, agent, model, updated_at) VALUES
    ('Developer', 'Writes the code and its unit tests on its own branch.', 'claude', NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    ('Tester', 'Builds the developers'' branches and checks them end to end; reports exactly what passed and failed.', 'claude', NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    ('Reviewer', 'Reviews a branch''s diff for correctness, security and the repository''s rules; reports findings, does not rewrite.', 'claude', NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    ('Researcher', 'Investigates a question (code, docs, the web where allowed) and writes findings to the team''s documents.', 'claude', NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    ('Architect', 'Designs the approach before building: the plan, the interfaces and the risks, written to the team''s documents.', 'claude', NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    ('Writer', 'Writes and edits the docs, release notes and user-facing text.', 'claude', NULL, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));

-- team_hire_list: the roles a team's Manager may hire, each with the agent and model (NULL: the agent's default) a hire
-- of it runs with. A team with no rows hires as before: any agent, free job text. role names a roles row (kept in step
-- by the app when a role is renamed; a role a list uses cannot be removed).
CREATE TABLE team_hire_list (
    team_id  INTEGER NOT NULL REFERENCES teams(id) ON DELETE CASCADE,
    role     TEXT    NOT NULL COLLATE NOCASE,
    agent    TEXT    NOT NULL,
    model    TEXT    NULL,
    position INTEGER NOT NULL,
    PRIMARY KEY (team_id, role)
) STRICT;

-- members.hire_role: the role a member was hired as (NULL: hired without one, and every Manager).
ALTER TABLE members ADD COLUMN hire_role TEXT NULL;
