-- 0038 Logs: the audit keeps changes only. Rows the screens' polls and lookups wrote (reads) are removed; teams.push
-- and teams.syncToBase rows are removed too, since their target cannot say whether a call set the setting or only
-- read it. Indexes on each log's time and member serve the Logs dialog's sorting, paging and member filter.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
DELETE FROM audit_log WHERE method IN (
    'integrations.asks', 'monitor.steps', 'bus.workflows', 'backlog.item', 'integrations.connectionAttention',
    'integrations.connections', 'agents.versionStatus', 'agents.models', 'teams.repos', 'integrations.mcpList',
    'integrations.apiList', 'integrations.checklist', 'integrations.discoverStatus', 'integrations.watches',
    'teams.approvals', 'teams.env', 'teams.quiet', 'tasks.runs', 'agents.runRecords', 'agentProfiles.history',
    'agentProfiles.check', 'agents.commandLine', 'agents.testSessions', 'companion.actionStatus', 'ops.backups',
    'ops.doctor', 'ops.keepDaily', 'ops.restoreStatus', 'agents.defaultModels', 'teams.push', 'teams.syncToBase');

CREATE INDEX IF NOT EXISTS ix_gateway_events_at ON gateway_events(at);
CREATE INDEX IF NOT EXISTS ix_gateway_events_member ON gateway_events(member);
CREATE INDEX IF NOT EXISTS ix_run_records_started ON run_records(started_at);
CREATE INDEX IF NOT EXISTS ix_integration_calls_at ON integration_calls(at);
CREATE INDEX IF NOT EXISTS ix_integration_calls_member ON integration_calls(member);
CREATE INDEX IF NOT EXISTS ix_usage_events_member ON usage_events(member);
CREATE INDEX IF NOT EXISTS ix_agent_changes_at ON agent_changes(at);
CREATE INDEX IF NOT EXISTS ix_transcripts_started ON transcripts(started_at);
CREATE INDEX IF NOT EXISTS ix_messages_occurred ON messages(occurred_at);
