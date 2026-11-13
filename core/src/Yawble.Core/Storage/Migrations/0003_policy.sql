-- 0003 policy. The global policy and the per-member override and loosen blocks.
-- member_id is the members.id of 0002_bus. No foreign key: this migration does not depend on 0002's
-- table existing, so the policy store and the bus can land in either order.
-- policy_global holds exactly one row (id = 1); the core seeds it with the default policy on first use.
CREATE TABLE policy_global (
    id          INTEGER PRIMARY KEY CHECK (id = 1),
    policy_json TEXT    NOT NULL,
    updated_at  TEXT    NOT NULL
) STRICT;

-- override_json: tighten only, saved by the person in the UI. loosen_json: the separate loosen block, saved
-- only by the person after a confirmation that lists exactly what is loosened (loosen_summary is that list,
-- loosen_approved_at when it was confirmed). No agent and no MCP tool can write this table.
CREATE TABLE member_policy (
    member_id          INTEGER PRIMARY KEY,
    override_json      TEXT,
    loosen_json        TEXT,
    loosen_summary     TEXT,
    loosen_approved_at TEXT,
    updated_at         TEXT    NOT NULL
) STRICT;
