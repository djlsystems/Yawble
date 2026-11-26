import { ref } from 'vue';
import type { ConciergeDefaults, YawbleApi } from '../../shared/api';

/**
 * The saved Concierge default agent (Settings > Concierge, concierge.defaults): the agent a new chat in the window starts
 * on when it is ready (App.vue defaultCli). Shared so a change saved on the Concierge tab applies to the next new chat at
 * once. null: none saved, or an older core without concierge.defaults (then the first ready agent).
 */
export const savedConciergeAgent = ref<string | null>(null);

export async function loadConciergeDefault(api: YawbleApi): Promise<void> {
  try {
    const d = await api.call<ConciergeDefaults>('concierge.defaults');
    savedConciergeAgent.value = d?.defaultAgent ?? null;
  } catch { savedConciergeAgent.value = null; /* an older core: the first ready agent */ }
}

/**
 * The agent a new chat starts on: the saved default when it is ready (a built-in that is ready, or a custom agent whose
 * base CLI is ready), else the first ready built-in.
 */
export function newChatAgent(agents: { id: string; ready: boolean }[], saved: string | null,
  customs: { name: string; baseCli: string }[] = []): string {
  const ready = agents.filter((a) => a.ready);
  if (saved) {
    if (ready.some((a) => a.id === saved)) return saved;
    const custom = customs.find((c) => c.name === saved);
    if (custom && ready.some((a) => a.id === custom.baseCli)) return saved;
  }
  return ready[0]?.id || 'claude';
}
