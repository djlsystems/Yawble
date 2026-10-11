import { computed, ref, watch } from 'vue'
import { endedOutcomeLabel, findOutcomeRef, type OutcomeRef } from '../api/outcomes'

/**
 * THE CURRENT VALUE OF AN OUTCOME SELECT THAT IS NOT ON OFFER: a retired or merged outcome. It is
 * shown by name with its status ("Old goal (retired)") and never offered as a new choice - the one
 * option it yields is disabled, and it is gone once the value changes to anything else.
 *
 * `known` names the outcome when the caller already holds it (a card carries its outcome);
 * otherwise it is read by id, and only once `ready` says the live list is in, so a live value is
 * never read. A slower read for an earlier value never lands on a newer one.
 */
const Ended = new Set<OutcomeRef['status']>(['retired', 'merged'])

export function useEndedOutcomeOption(
  current: () => string | null | undefined,
  live: () => OutcomeRef[],
  options: { known?: () => OutcomeRef | null | undefined; ready?: () => boolean } = {},
) {
  const { known = () => null, ready = () => true } = options
  const ended = ref<OutcomeRef | null>(null)
  const isLive = (id: string) => live().some((outcome) => outcome.id === id)

  watch(
    () => [current(), ready(), live().map((outcome) => outcome.id).join()] as const,
    async ([id]) => {
      ended.value = null
      if (!id || id === 'none' || !ready() || isLive(id)) return

      const held = known()
      const found = held?.id === id ? held : await findOutcomeRef(id)
      if (current() === id) ended.value = found
    },
    { immediate: true },
  )

  return computed(() =>
    ended.value && ended.value.id === current() && Ended.has(ended.value.status) && !isLive(ended.value.id)
      ? [{ label: endedOutcomeLabel(ended.value), value: ended.value.id, disable: true }]
      : [],
  )
}
