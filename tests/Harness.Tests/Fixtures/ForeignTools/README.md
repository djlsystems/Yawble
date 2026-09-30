# Foreign tools fixtures

Real agent transcripts from this Host, redacted, for the per-run foreign tools check
(`TranscriptTools`, `ForeignToolsCheck`). Nothing here was written by hand.

Redaction keeps only what names a tool: attachment and event types, tool and server names,
call records and their ids and times. Prompts, message text, tool inputs and results, tool
descriptions and schemas, account ids and working directories are `REDACTED` or dropped;
lines that name no tool are dropped.

| File | Source | What it shows |
| --- | --- | --- |
| `claude-offers-connectors.jsonl` | Claude Code 2.1.285, member DeveloperTobias, team b001h-solution-packages, session `0b51ae98…` (2026-09-29) | A member offered the signed-in account's `claude_ai_*` connectors (Gmail, Claude Docs; Calendar and Drive awaiting authorisation) beside `harness`; it called only `harness` and its own tools. |
| `claude-calls-connectors.jsonl` | Claude Code, the Concierge's own session `bbb7fa7e…` (2026-09-26) | Connectors offered and CALLED (`claude_ai_Claude_Docs` batch, guide, update, create). The only real transcript on this Host with such a call; no member has made one. |
| `claude-isolated.jsonl` | Claude Code 2.1.285, an isolated launch probe (session `cda854d1…`, 2026-09-30) | `harness` and Claude's own tools only. |
| `codex-calls-stub-server.jsonl` | codex-cli 0.157.0, `codex exec` with a local stdio MCP server `stubfs` added by `-c mcp_servers.stubfs.command=` (2026-09-30) | A call to a server that is not `harness` (`stubfs/echo`). The server is a local stub with one echo tool, never an account's connector. |
| `grok-calls-stub-server.jsonl` | grok 1.0.44, the same stub as a project-scoped server, `--trust` (2026-09-30) | `search_tool` finding `stubfs__echo`, then `use_tool` calling it. |
| `copilot-no-builtin-servers.jsonl` | Copilot CLI 1.0.88 with `--disable-builtin-mcps` (2026-09-30) | No server section in the system message, one call to its own `view`: nothing foreign seen, but Copilot does not list its whole offer. |

The LiveView fixtures are read too: `copilot-events.jsonl` (the built-in
`github-mcp-server` offered), `grok-updates.jsonl` and `codex-rollout.jsonl` (only `harness`
called).
