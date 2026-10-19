/**
 * THE ONE EVENT VOCABULARY: a log row's type, as the words a person reads, its tone and its icon.
 *
 * The member card's feed chip, the Kanban card panel's story, and the Monitor's activity list,
 * legend and effects all read this table, so one event is said one way on every view. The raw type
 * is still shown where someone may quote it - the feed row's tooltip and the opened message's
 * header - and nowhere else.
 *
 * A type this table does not know reads as it always did: the type with its `agentContainer.` prefix
 * removed, in the quiet tone. `event-words.spec.ts` fails if the Monitor's model or the feed carries
 * a table of its own.
 */

/** The colour family of an effect, a list row, a feed chip and a legend entry. */
export type Tone =
  | 'tell'
  | 'start'
  | 'progress'
  | 'done'
  | 'handback'
  | 'fail'
  | 'blocked'
  | 'workflow'
  | 'card'
  | 'push'
  | 'plugin'
  | 'file'
  | 'quiet'

export interface EventWords {
  /** A short verb phrase: "moved a card", "pushed", "finished". */
  words: string
  tone: Tone
  /** A Material Symbols Outlined name. */
  icon: string
}

export const InstructionPrefix = 'agentContainer.instruction.'
const KanbanCardPrefix = 'kanban.card.'
const PluginPrefix = 'plugin.'
/** The source of a member's own wake: nobody sent it. */
const WakeSource = 'wake:'

const said = (words: string, tone: Tone, icon: string): EventWords => ({ words, tone, icon })

/** Every type with words of its own. The three families below it are read by prefix. */
const Table: Readonly<Record<string, EventWords>> = {
  'agentContainer.started': said('started', 'start', 'play_arrow'),
  'agentContainer.progress': said('working', 'progress', 'more_horiz'),
  'agentContainer.completed': said('finished', 'done', 'check_circle'),
  'agentContainer.handback': said('handed back', 'handback', 'undo'),
  'agentContainer.failed': said('failed', 'fail', 'error'),
  'agentContainer.blocked': said('blocked', 'blocked', 'block'),
  'agentContainer.needsDecision': said('needs you', 'blocked', 'help'),
  'agentContainer.rejected': said('refused', 'fail', 'cancel'),
  'agentContainer.scheduleSkipped': said('skipped', 'quiet', 'schedule'),
  'wake.asked': said('asked to be woken', 'quiet', 'alarm'),
  'wake.fired': said('wake fired', 'tell', 'alarm'),
  'wake.cancelled': said('wake cancelled', 'quiet', 'alarm'),
  'wake.skipped': said('wake skipped', 'quiet', 'alarm'),
  'workflow.completed': said('workflow done', 'workflow', 'done'),
  'workflow.closed': said('closed', 'quiet', 'close'),
  'workflow.paused': said('paused', 'quiet', 'pause'),
  'workflow.resumed': said('resumed', 'quiet', 'play_circle'),
  'kanban.card.planned': said('planned a card', 'card', 'view_kanban'),
  'kanban.card.moved': said('moved a card', 'card', 'view_kanban'),
  'backlog.item.dispatched': said('dispatched', 'tell', 'flag'),
  'repo.pushed': said('pushed', 'push', 'upload'),
  'repo.pushFailed': said('push failed', 'fail', 'error'),
  'file.changed': said('files changed', 'file', 'description'),
  'site.action': said('site action', 'tell', 'language'),
  'solution.checked': said('package checked', 'workflow', 'inventory_2'),
}

/**
 * A row's words, tone and icon. `source` tells a member's own wake ("woke") from an instruction
 * somebody sent ("told"); it changes nothing else.
 */
export function eventWords(type: string, source = ''): EventWords {
  const known = Table[type]
  if (known) return known

  if (type.startsWith(InstructionPrefix)) {
    return source.startsWith(WakeSource) ? said('woke', 'tell', 'alarm') : said('told', 'tell', 'send')
  }
  if (type.startsWith(KanbanCardPrefix)) return said(`card ${type.slice(KanbanCardPrefix.length)}`, 'card', 'view_kanban')
  if (type.startsWith(PluginPrefix)) return said(type.slice(type.lastIndexOf('.') + 1), 'plugin', 'extension')

  return said(type.replace('agentContainer.', ''), 'quiet', 'event')
}

/**
 * The Monitor's legend: one entry per tone it colours, each said by the words of the row that
 * wears it most, so the legend and the list beside it use one vocabulary.
 */
export const ToneLegend: readonly { tone: Tone; words: string }[] = (
  [
    ['tell', `${InstructionPrefix}legend`],
    ['start', 'agentContainer.started'],
    ['done', 'agentContainer.completed'],
    ['handback', 'agentContainer.handback'],
    ['workflow', 'workflow.completed'],
    ['card', 'kanban.card.moved'],
    ['push', 'repo.pushed'],
    ['fail', 'agentContainer.failed'],
  ] as const
)
  .map(([tone, type]): { tone: Tone; words: string } => ({ tone, words: eventWords(type).words }))
  // A plugin's row is said by its own last word, so the legend names the kind instead.
  .concat({ tone: 'plugin', words: 'plugin event' })

/**
 * The ink a tone is drawn in on the page - the feed chip's edge and the opened message's rule -
 * through the state tokens wherever the tone is a state, the muted ink otherwise. Never the
 * Monitor's own canvas colours, which are drawn for its screen.
 */
export function toneInk(tone: Tone): string {
  switch (tone) {
    case 'start':
    case 'progress':
      return 'var(--os-state-running)'
    case 'done':
    case 'workflow':
      return 'var(--os-state-done)'
    case 'fail':
      return 'var(--os-state-failed)'
    case 'blocked':
      return 'var(--os-state-blocked)'
    case 'tell':
    case 'handback':
      return 'var(--os-primary)'
    default:
      return 'var(--os-ink-muted)'
  }
}
