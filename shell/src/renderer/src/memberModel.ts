import type { AgentModelShown } from '../../shared/api';
import { ago } from './board/cards';

/** The member pane's agent pill: "<agent>/<model>" (core teams.agents' agentModel); just the agent when the core sent none. */
export function agentPillText(agent: string, m: AgentModelShown | null | undefined): string {
  if (!m) return agent;
  return `${agent}/${m.name || 'default'}`;
}

/** The pill's tooltip: the whole text and where the model's name comes from. */
export function agentPillTitle(agent: string, m: AgentModelShown | null | undefined): string {
  const text = agentPillText(agent, m);
  if (!m) return text;
  switch (m.source) {
    case 'member': return `${text}\nModel set on this member`;
    case 'agent': return `${text}\nThe agent's default model`;
    case 'settings': return `${text}\nSet in the CLI's own settings`;
    case 'observed': return `${text}\nThe CLI's default; its last run used ${m.name}${m.at ? ` (${ago(m.at)})` : ''}`;
    default: return `${text}\nThe CLI's default model (not run yet)`;
  }
}
