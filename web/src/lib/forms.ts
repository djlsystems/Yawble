import type { Rule } from './rules'

/**
 * The glue every validated dialog needs around `rules.ts`, kept apart from it so the rules module
 * stays a list of rules.
 *
 * WHY `passes` AND NOT QForm's OWN STATE: QForm exposes `validate()`, which is asynchronous and
 * paints every field red as a side effect. A submit button's `:disable` has to be a plain computed
 * that can be read on every render without touching the fields, so each dialog asks the same rules
 * its fields carry, directly.
 */

/** True when every rule accepts the value. */
export function passes(value: unknown, rules: readonly Rule[]): boolean {
  return rules.every((rule) => rule(value) === true)
}

/** The HTTP status a refusal from `api/client` carried, when it carried one. */
export function refusalStatus(cause: unknown): number | undefined {
  if (typeof cause !== 'object' || cause === null) return undefined
  const status = (cause as { status?: unknown }).status
  return typeof status === 'number' ? status : undefined
}

interface Submittable {
  submit: (event?: Event) => void
}

/**
 * Enter in a single-line field submits the form, through QForm so its validation runs first.
 *
 * A browser already does this for a form with a submit button, but only when that button is
 * enabled, and not at all in the test DOM. Doing it here makes the behaviour the same everywhere and
 * testable. A textarea keeps Enter for a new line; a select keeps it for choosing an option; a
 * composition (an IME) keeps it for committing the character.
 */
export function submitOnEnter(form: () => Submittable | null, enabled: () => boolean) {
  return (event: KeyboardEvent) => {
    if (event.key !== 'Enter' || event.isComposing || event.shiftKey) return

    const target = event.target
    if (!(target instanceof HTMLInputElement)) return
    if (target.closest('.q-select')) return

    event.preventDefault()
    if (!enabled()) return

    form()?.submit(event)
  }
}
