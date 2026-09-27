/**
 * WHAT A FAILURE CLASS SAYS TO A PERSON — the browser's one store for those words.
 *
 * The C# side has its own in `FailureClasses.Sentence`, and the two are deliberately separate for
 * the reason `PayloadFields`' own doc comment gives about `summarise.ts`: there is no server in the
 * web test run, so no test could compare them, and making a renderer fetch `/api/events` to learn a
 * sentence buys a runtime dependency for a wording problem. Two stores, and both of them named.
 *
 * ONE STORE WITHIN THE BROWSER, though. The activity feed and the member card both say this, and
 * two copies of it is how a row and the card above it come to describe the same failure
 * differently — the same argument `containerMark` and `teamStatus` already share about ranking.
 */

/**
 * The classes this build knows, in the spelling the server writes.
 *
 * A VALUE OUTSIDE THIS SET IS NOT AN ERROR. The log is append-only and a newer server may write a
 * class this bundle has never heard of; everything below answers `null` for one, so the card and
 * the feed fall back to exactly what they rendered before classes existed.
 */
export const FailureClasses = {
  Quota: 'quota',
  Rate: 'rate',
  Transport: 'transport',
  AgentFault: 'agent-fault',
  Timeout: 'timeout',
  Interrupted: 'interrupted',
  Unknown: 'unknown',
} as const

/**
 * Whether the platform resumes a failure of this class BY ITSELF.
 *
 * The product rule, and the browser's copy of it: quota and rate, and
 * nothing else, ever. It is here so the card does not promise a resume for a class that never gets
 * one — a `retryAfter` on a transport row is what the provider said, not what this platform intends
 * to do about it.
 */
export function resumesAutomatically(failureClass: string | null | undefined): boolean {
  return failureClass === FailureClasses.Quota || failureClass === FailureClasses.Rate
}

/**
 * The class in words, or `null` for a row with no class and for a spelling this build does not
 * know.
 *
 * NULL IS THE LOAD-BEARING ANSWER: every caller renders nothing at all for it, which is how rows
 * without a class keep their meaning.
 */
export function failureClassWords(
  failureClass: string | null | undefined,
  kind?: 'agent' | 'plugin',
): string | null {
  if (kind === 'plugin') return pluginFailureWords(failureClass)

  switch (failureClass) {
    case FailureClasses.Quota:
      return 'the provider says a budget is spent, so nothing is wrong with this run'
    case FailureClasses.Rate:
      return 'the provider refused for sending too much too fast'
    case FailureClasses.Transport:
      return 'the network failed, so how far this run got is not known'
    case FailureClasses.AgentFault:
      return 'the agent itself failed, so re-running it would spend again to fail the same way'
    case FailureClasses.Timeout:
      return 'the idle clock fired'
    case FailureClasses.Interrupted:
      return 'the run was cut off before it finished'
    case FailureClasses.Unknown:
      return 'nothing could say why, so this is treated as an agent fault and is not resumed'
    default:
      return null
  }
}

/**
 * THE SAME CLASSES, SAID OF A PLUGIN. A plugin runs no model and reports no usage, so it has no
 * provider to blame and no spend to repeat: the provider and spend words above would send a person
 * looking for a bill that does not exist. A class this build does not know is `null`, as above.
 */
function pluginFailureWords(failureClass: string | null | undefined): string | null {
  switch (failureClass) {
    case FailureClasses.Quota:
      return 'a limit the plugin depends on is used up'
    case FailureClasses.Rate:
      return 'the plugin was refused for sending too much too fast'
    case FailureClasses.Transport:
      return 'the network failed, so how far this run got is not known'
    case FailureClasses.AgentFault:
      return 'the plugin itself failed, so re-running it would likely fail the same way'
    case FailureClasses.Timeout:
      return 'the idle clock fired'
    case FailureClasses.Interrupted:
      return 'the run was cut off before it finished'
    case FailureClasses.Unknown:
      return 'nothing could say why, so this is not resumed'
    default:
      return null
  }
}

/**
 * A short badge label for the class — `quota`, `rate`, and so on — or `null` when there is nothing
 * to badge. The bare value, because the server's spelling already reads as a label and inventing a
 * prettier one is a second store of the same word.
 */
export function failureClassLabel(
  failureClass: string | null | undefined,
  kind?: 'agent' | 'plugin',
): string | null {
  if (failureClassWords(failureClass, kind) === null) return null

  // A plugin is not an agent: its own fault is badged as the plugin's.
  return kind === 'plugin' && failureClass === FailureClasses.AgentFault ? 'plugin-fault' : (failureClass as string)
}

/**
 * When a pending automatic resume fires, as local wall-clock, or `null` when there is none.
 *
 * LOCAL AND ABSOLUTE, matching `timeOf` in `summarise.ts` and for its reason: the point of a time
 * on this board is lining it up against what the console printed, and "in 4 hours" cannot be
 * compared to anything. An unparseable stamp answers `null` rather than `Invalid Date` — a card
 * that promises a resume at a moment it cannot name is worse than one that promises nothing.
 */
export function resumeTimeText(resumeAt: string | null | undefined): string | null {
  if (typeof resumeAt !== 'string' || resumeAt.trim() === '') return null

  const at = new Date(resumeAt)

  return Number.isNaN(at.getTime()) ? null : at.toLocaleString()
}
