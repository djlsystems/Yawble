import { onMounted, ref } from 'vue';
import type { AgentProfileRow, YawbleApi } from '../../shared/api';
import { agentClis } from './agentClis';

/**
 * The agents a picker offers (adding a member, a team's Manager, team_create): the built-ins, then the person's own
 * custom agents (agentProfiles.list). Until the core answers, or when it cannot (a cloud viewer), just the built-ins.
 */
export function useAgentOptions(api: YawbleApi) {
  const options = ref<{ id: string; label: string }[]>(agentClis.map((c) => ({ id: c.id, label: c.label })));
  onMounted(async () => {
    try {
      const rows = await api.call<AgentProfileRow[]>('agentProfiles.list');
      const customs = rows.filter((r) => r.custom).map((r) => ({ id: r.name, label: `${r.name} (my agent)` }));
      options.value = [...agentClis.map((c) => ({ id: c.id as string, label: c.label as string })), ...customs];
    } catch { /* built-ins only */ }
  });
  return options;
}
