# Foreign tools fixtures

Real agent transcripts from this Host, redacted, for the per-run foreign tools check
(`TranscriptTools`, `ForeignToolsCheck`). Nothing here was written by hand.

Redaction keeps only what names a tool: attachment and event types, tool and server names,
call records and their ids and times. Prompts, message text, tool inputs and results, tool
descriptions and schemas, account ids and working directories are `REDACTED` or dropped;
lines that name no tool are dropped. Copilot's usage checkpoint keeps only each offered tool's
name, the model, `tool_count` and `tools_truncated`. Grok's `tool_definitions.json` keeps only
each tool's name, and its `events.jsonl` only the MCP start-up rows (servers, their tool names,
counts). A Grok server-side call keeps its `rawInput.variant` (`WebSearch`), nothing else.

A Grok fixture is a folder, as Grok writes a session: `updates.jsonl` with its two side files.

| File | Source | What it shows |
| --- | --- | --- |
| `claude-offers-connectors.jsonl` | Claude Code 2.1.285, member DeveloperTobias, team b001h-solution-packages, session `0b51ae98…` (2026-09-29) | A member offered the signed-in account's `claude_ai_*` connectors (Gmail, Claude Docs; Calendar and Drive awaiting authorisation) beside `harness`; it called only `harness` and its own tools. |
| `claude-calls-connectors.jsonl` | Claude Code, the Concierge's own session `bbb7fa7e…` (2026-09-26) | Connectors offered and CALLED (`claude_ai_Claude_Docs` batch, guide, update, create). The only real transcript on this Host with such a call; no member has made one. |
| `claude-isolated.jsonl` | Claude Code 2.1.285, a probe launched with `--strict-mcp-config` only (session `cda854d1…`, 2026-09-30) | `harness` and Claude's own tools, among them tools the claude preset does not list (`RemoteTrigger`, `SendMessage`). |
| `claude-isolated-member.jsonl` | Claude Code 2.1.285, member ProbeClaude on a test Host launched with the claude preset's full isolation (session `7d0da4bb…`, 2026-09-30) | Only `harness` and the tools the preset lists: clean. |
| `codex-calls-stub-server.jsonl` | codex-cli 0.157.0, `codex exec` with a local stdio MCP server `stubfs` added by `-c mcp_servers.stubfs.command=` (2026-09-30) | A call to a server that is not `harness` (`stubfs/echo`). The server is a local stub with one echo tool, never an account's connector. |
| `grok-calls-stub-server/` | grok 1.0.44, the same stub as a project-scoped server, `--trust` (session `90dc1cb0…`, 2026-09-30) | `stubfs` configured and connected beside `harness`; `search_tool` finding `stubfs__echo`, then `use_tool` calling it. |
| `grok-member/` | grok 1.0.44, member ResearcherImani, team research-draft-license (session `7e2d3594…`, 2026-09-26) | Grok's own tools, `harness` only, server-side web searches: clean under the grok preset. |
| `copilot-isolated-member.jsonl` | Copilot CLI 1.0.88, member ProbeCopilot on a test Host launched with `--disable-builtin-mcps` (session `38cb555c…`, 2026-09-30) | A usage checkpoint listing `harness` and Copilot's own tools (model gpt-6-luna): clean. |
| `copilot-no-builtin-servers.jsonl` | Copilot CLI 1.0.88 with `--disable-builtin-mcps`, model mai-code-1.1-flash (session `9ad689d1…`, 2026-09-30) | No MCP server at all; on this model Copilot offers `create`, `edit`, `grep` where the preset lists `apply_patch`. |
| `copilot-stopped-before-checkpoint.jsonl` | The first four lines of `copilot-no-builtin-servers.jsonl`, unchanged | What a run stopped before Copilot writes its usage checkpoint (it writes it at the end) leaves: no offer list, so not measured. |

The LiveView fixtures are read too: `copilot-events.jsonl` (the built-in `github-mcp-server`
offered, in its usage checkpoint and its system message), `grok-updates.jsonl` (only `harness`
called, kept without Grok's side files, so its offer is not measured) and `codex-rollout.jsonl`
(only `harness` called; Codex lists no offer).
