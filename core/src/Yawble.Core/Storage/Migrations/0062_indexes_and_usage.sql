-- 0062 indexes for the lookups that grow with history, and usage_events without the provider's raw usage text.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.

-- messages: a question's replies are found by what they answer (causation_seq), and questions, reports and
-- instructions by type and sender. The deliverable index carries type, source and time, so reading a deliverable's
-- first and last message, its count and its sender needs the index alone.
CREATE INDEX ix_messages_causation   ON messages(causation_seq);
CREATE INDEX ix_messages_type_source ON messages(type, source, seq);
CREATE INDEX ix_messages_head        ON messages(correlation_id, seq, type, source, occurred_at);
DROP INDEX ix_messages_correlation;

-- runs, model usage, cards and backlog dispatches of one deliverable.
CREATE INDEX ix_runs_correlation               ON runs(correlation_id, id);
CREATE INDEX ix_usage_events_run_number        ON usage_events(run_number);
CREATE INDEX ix_board_cards_deliverable        ON board_cards(deliverable);
CREATE INDEX ix_backlog_dispatches_deliverable ON backlog_dispatches(deliverable);

-- Cards are looked up by team and member ignoring case, so the index has that collation.
CREATE INDEX ix_board_cards_team_member ON board_cards(team COLLATE NOCASE, member COLLATE NOCASE, deliverable);
DROP INDEX board_cards_member;

-- Indexes no statement uses: seq is the row id of messages; nothing filters on a message's origin, on guardrail
-- events alone, on a run record's bus run, or on a card's team and lane.
DROP INDEX ix_messages_seq_type;
DROP INDEX ix_messages_origin;
DROP INDEX ix_gateway_events_guardrail;
DROP INDEX ix_run_records_bus_run;
DROP INDEX board_cards_team;

-- The provider's raw usage text is not read by anything; the token columns hold what is used.
ALTER TABLE usage_events DROP COLUMN usage;
