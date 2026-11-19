-- 0049 The audit keeps changes only (as 0038 did): rows that calls which only read wrote are removed. The Monitor's
-- code map polled monitor.codeEvents about once a second, which made almost every row of the audit; the rest are the
-- Logs dialog's live tail, the integrations' read-outs and the cloud's org summary watch.
-- Append-only: never edit this file once shipped; change the schema in a later numbered step.
DELETE FROM audit_log WHERE method IN (
    'monitor.codeEvents', 'logs.tail', 'logs.untail', 'integrations.calls', 'integrations.taskRuns',
    'integrations.mcpParse', 'integrations.mcpImportScan', 'integrations.mcpCheck', 'org.summary.watch',
    'org.summary.unwatch');
