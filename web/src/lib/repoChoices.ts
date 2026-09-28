import type { RepoCheckFailure, RepoCheckRefusal, RepoChoice } from '../api/types'

/**
 * A REFUSED REPOSITORY CHECK AND ITS ANSWER (B001F), shared by New Team and Team settings.
 *
 * The Host reads every new URL with `git ls-remote` before a create or an attach, and a URL it cannot
 * read is refused with 422 `repo-check-failed`: the sentence, and per URL the choices THIS caller may
 * send. The web offers exactly those, never a list of its own - which choices apply (a github.com
 * URL, a token that can create, a network failure) is the Host's judgement, and a button the Host
 * would refuse is a worse answer than no button.
 */

/** What each choice is called on its button. A code the Host adds later and this build does not know is not shown. */
export const RepoChoiceLabels: Record<RepoChoice, string> = {
  'create-on-github': 'Create it on GitHub (private)',
  'use-local': 'Use a local repository instead',
  'attach-anyway': 'Attach anyway',
}

/** The choices a failing URL offers that this build can render, in the Host's order. */
export function offeredChoices(failure: RepoCheckFailure): RepoChoice[] {
  return failure.choices.filter((code): code is RepoChoice => code in RepoChoiceLabels)
}

/**
 * The choices after a person picks one for a URL, and whether the request can be sent again: only
 * once EVERY URL the refusal names has a choice, so one resend answers the whole refusal. Choices
 * already made for URLs the refusal does not name (answered in an earlier round) are kept, because
 * the resend is the whole request again.
 */
export function withChoice(
  choices: Record<string, RepoChoice>,
  refusal: RepoCheckRefusal,
  url: string,
  choice: RepoChoice,
): { choices: Record<string, RepoChoice>; ready: boolean } {
  const next = { ...choices, [url]: choice }
  return { choices: next, ready: refusal.repos.every((entry) => next[entry.url] !== undefined) }
}

/**
 * The choices to carry into the next round once a new refusal arrives: a URL it names again needs
 * a fresh answer (GitHub refused the create, say), so its old choice is dropped.
 */
export function afterRefusal(
  choices: Record<string, RepoChoice>,
  refusal: RepoCheckRefusal,
): Record<string, RepoChoice> {
  const named = new Set(refusal.repos.map((entry) => entry.url))
  return Object.fromEntries(Object.entries(choices).filter(([url]) => !named.has(url)))
}
