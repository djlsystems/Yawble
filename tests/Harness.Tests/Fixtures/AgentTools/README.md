# Agent tool listings

Real output of each agent CLI's own listing, recorded on the product image (claude 2.1.285,
codex-cli 0.157.0, grok 1.0.44, copilot 1.0.89) as the agent user, and read by
`AgentToolPreflightTests`. Nothing here was written by hand.

- `*.base.*`: the listing with no isolation, as the Concierge's launch runs it.
- `*.isolated.*`: the same listing with the member preset's isolation (environment, and the
  launch flags the listing command accepts).
- `*.canary.*`: the listing from a scratch config folder (`CLAUDE_CONFIG_DIR`, `CODEX_HOME`,
  `COPILOT_HOME`) with a stand-in `canary` MCP server added. The shared agent home had no such
  server, and was not edited.

Redacted: the claude.ai account id in grok's synced-skill paths is zeroed, the scratch working
folder is renamed, and grok's skill descriptions are cut to 80 characters.
