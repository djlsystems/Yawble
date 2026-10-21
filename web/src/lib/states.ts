/**
 * THE SEVEN STATES A PERSON READS, and the one colour each is drawn in.
 *
 * Running, waiting (always said with what for: a slot, a worker, memory, a lease, an update), needs
 * you, blocked, failed, idle and done. Each has one `--os-state-*` token in every theme block of
 * `app.scss`, holding 3:1 on `--os-chrome` and `--os-surface` in every built-in preset, and the Teams
 * table's and console tab's chips, the KPI tile's tone, the member card's pill, the Kanban colour
 * classes, the activity lanes and the Monitor's legend and rings all draw a state through it.
 *
 * A wire word is not changed here: `needs-decision` stays the server's, and reads "needs you"
 * wherever a person reads it.
 */
export const States = ['running', 'waiting', 'needs-you', 'blocked', 'failed', 'idle', 'done'] as const

export type State = (typeof States)[number]

export const StateWords: Readonly<Record<State, string>> = {
  running: 'running',
  waiting: 'waiting',
  'needs-you': 'needs you',
  blocked: 'blocked',
  failed: 'failed',
  idle: 'idle',
  done: 'done',
}

/**
 * THREE FACTS THAT END IN ONE WORD, AND ARE NOT MERGED. A run ending (`completed` on a run), a
 * workflow's declaration (`COMPLETED` on a workflow) and a card in the Done lane (`Done`) are three
 * things the agents and the docs quote, so each place keeps its own word and says, on hover, which
 * of the three it is.
 */
export const EndingFact = {
  run: 'A run ended: this member finished one run. It says nothing about the workflow or its card.',
  workflow: 'A workflow was declared finished by the member it was given to, or by its Manager.',
  card: 'A card in the Done lane: a person or the team moved the card there.',
} as const
