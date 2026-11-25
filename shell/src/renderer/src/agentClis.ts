// The agent CLI presets the app can run (core: Policy/AgentClis.cs), in the order the UI lists them.
export const agentClis = [
  { id: 'claude', label: 'Claude Code' },
  { id: 'grok', label: 'Grok (xAI)' },
  { id: 'codex', label: 'Codex (OpenAI)' },
  { id: 'copilot', label: 'GitHub Copilot' },
  { id: 'antigravity', label: 'Antigravity (Google)' },
] as const;

export type AgentCliId = (typeof agentClis)[number]['id'];

/** The CLIs whose maker documents an install command the app can run for the person (core: Agents/AgentModels.cs InstallCommands). */
export const installable: readonly string[] = ['claude', 'grok', 'codex', 'copilot'];

export const cliLabel = (id: string): string => agentClis.find((c) => c.id === id)?.label ?? id;

/** The CLI's name without its maker ("Codex", "Claude Code"), as the core's DisplayName says it in "Your Codex settings". */
export const cliName = (id: string): string => cliLabel(id).replace(/\s*\([^)]*\)\s*$/, '');
