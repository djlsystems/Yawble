// The agent CLI presets the app can run (core: Policy/AgentClis.cs), in the order the UI lists them.
export const agentClis = [
  { id: 'claude', label: 'Claude Code' },
  { id: 'grok', label: 'Grok (xAI)' },
  { id: 'codex', label: 'Codex (OpenAI)' },
  { id: 'copilot', label: 'GitHub Copilot' },
  { id: 'antigravity', label: 'Antigravity (Google)' },
] as const;

export type AgentCliId = (typeof agentClis)[number]['id'];

export const cliLabel = (id: string): string => agentClis.find((c) => c.id === id)?.label ?? id;
