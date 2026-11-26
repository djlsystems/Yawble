import { reactive, ref } from 'vue';
import type { AgentModelList, AgentProfileRow, YawbleApi } from '../../shared/api';
import { modelOptions, type ModelOption } from './modelOptions';

/**
 * The models each agent offers to a model picker: the agent profiles (each with its base CLI and default model) and,
 * per base CLI, its agents.models list, asked once. Without an answer a picker offers just the default and Custom….
 */
export function useAgentModels(api: YawbleApi) {
  const profiles = ref<AgentProfileRow[]>([]);
  const lists = reactive<Record<string, ModelOption[]>>({});
  const profileOf = (cli: string) => profiles.value.find((r) => r.name === cli);
  const baseOf = (cli: string) => profileOf(cli)?.baseCli ?? cli;
  const defaultLabel = (cli: string) => { const d = profileOf(cli)?.defaultModel; return d ? `default (${d})` : 'default'; };
  const modelsOf = (cli: string) => lists[baseOf(cli)] ?? [];
  async function loadModels(cli: string) {
    if (!cli) return;
    const base = baseOf(cli);
    if (lists[base]) return;
    lists[base] = [];
    try { lists[base] = modelOptions(await api.call<AgentModelList>('agents.models', { cli: base })); } catch { /* the default only */ }
  }
  async function loadProfiles() {
    try { profiles.value = await api.call<AgentProfileRow[]>('agentProfiles.list'); } catch { /* built-ins by name */ }
  }
  return { profiles, defaultLabel, modelsOf, loadModels, loadProfiles };
}

/** The role a member was hired as: member.hireRole from teams.agents; null when none. member.role stays
 *  'manager' | 'member' and is never read as a hire role. */
export function hireRoleOf(m: { hireRole?: string | null }): string | null {
  return m.hireRole && m.hireRole.trim() ? m.hireRole.trim() : null;
}
