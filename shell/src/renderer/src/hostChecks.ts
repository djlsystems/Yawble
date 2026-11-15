import { ref, watch, type Ref } from 'vue';
import type { HostCheck, YawbleApi } from '../../shared/api';

/**
 * Allowed-host entries as the core's floor rule judges them (policy.checkHosts: FloorRules.HostProblem). An entry is an
 * exact host or one leading "*." over a registrable domain ("*.digicert.com"). The window never judges a host itself:
 * every problem shown is the core's own words, and a save is checked by the core again.
 */
export function checkHosts(api: YawbleApi, hosts: string[]): Promise<HostCheck[]> {
  return api.call<HostCheck[]>('policy.checkHosts', { hosts });
}

/**
 * The entries of a host list the core refuses, kept current as the list changes (a later answer wins over an earlier
 * one). An older core without policy.checkHosts, or a failed call, shows none: the save still says what is wrong.
 */
export function useHostProblems(api: YawbleApi, hosts: () => string[]): Ref<HostCheck[]> {
  const problems = ref<HostCheck[]>([]);
  let asked = 0;
  watch(() => hosts().map((h) => h.trim()).filter(Boolean), async (list) => {
    const mine = ++asked;
    if (!list.length) { problems.value = []; return; }
    try {
      const checked = await checkHosts(api, list);
      if (mine === asked) problems.value = (Array.isArray(checked) ? checked : []).filter((c) => c.problem);
    } catch {
      if (mine === asked) problems.value = [];
    }
  }, { immediate: true, deep: true });
  return problems;
}

/** A domain allow's meaning, shown with it: "every subdomain of digicert.com"; null for an exact host. */
export function domainAllowNote(entry: string): string | null {
  return entry.startsWith('*.') ? `every subdomain of ${entry.slice(2)}` : null;
}
