import { computed, ref } from 'vue'
import { listCatalog } from '../api/client'
import type { AgentInstallation } from '../api/types'
import { agentsBadge } from './agentInstall'

/**
 * Whether the CLIs this tenant's teams are on are actually on this machine - held ONCE, for every
 * surface that marks it.
 *
 * MODULE SCOPE, and that is the whole reason this is not three refs inside `useRibbon()`. The
 * desktop strip and the mobile drawer each call that composable, so per-call state would give them
 * a badge each, fetched separately and updated separately - and the failure that produces is the
 * one this repository has already paid for: the switch-team menu once carried a team mark while the
 * mobile menu beside it stayed bare, which teaches a person that the absence of a mark means
 * something. One store of the fact, several readers.
 *
 * It also lets the Agents screen refresh it on close, so putting a CLI back clears the badge
 * WITHOUT A RESTART - which is the recovery this whole feature exists to make visible.
 */
const installations = ref<AgentInstallation[]>([])

/**
 * Re-reads the probe.
 *
 * CALLED ONCE THERE IS A SESSION - `useRibbon` watches for one - because an anonymous caller gets
 * a 401 and would settle on an empty list, which the library reads as "not checked" rather than as
 * "everything is fine". Every signed-in person reads the full catalog, so there is no tier to test.
 *
 * A FAILURE EMPTIES THE LIST RATHER THAN THROWING. This runs on mount and from a dialog closing,
 * neither of which has anywhere to report to; a badge that is absent because the fetch failed is
 * the safe direction, since the mark is a CLAIM that something will not run.
 */
export async function refreshAgentInstallations(): Promise<void> {
  try {
    installations.value = (await listCatalog()).installations ?? []
  } catch {
    installations.value = []
  }
}

/** The shared list and the badge derived from it. The badge is NULL when there is nothing to say -
 *  on a machine with every CLI installed the mark must be absent, or it is noise from day one. */
export function useAgentInstallations() {
  return {
    installations,
    badge: computed(() => agentsBadge(installations.value)),
  }
}
