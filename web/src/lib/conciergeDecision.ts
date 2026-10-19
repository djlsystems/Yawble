import type { KanbanCard } from '../api/kanban';
import type { ContainerSnapshot } from '../api/types';
import { NeedsYouLaneId } from './kanban';
import { containerMark } from './teamKpis';

/**
 * "DISCUSS WITH CONCIERGE": ONE DECISION A PERSON IS ASKED, NAMED EXACTLY, AND THE ONE REQUEST THE
 * PANEL TYPES ABOUT IT.
 *
 * The request names the decision - team, member, workflow and the needs-decision row's seq - and
 * asks the Concierge to read it with its own tools. It never carries the question's text: that is
 * the member's words, and pasted at the prompt they would read to the Concierge as the person's
 * instruction.
 */
export interface DecisionAsk {
  team: string;
  /** The team's display name, or its id when the board does not know the name. */
  teamName: string;
  member: string;
  /** The workflow (correlation) the decision was asked in. */
  workflow: number;
  /** The seq of the needs-decision row. */
  seq: number;
}

const isRowNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isInteger(value) && value > 0;

/**
 * The decision a member card is waiting on, or null. Only while its mark IS the decision: a failed
 * or blocked mark outranks it (`containerMark`), and a snapshot that does not name the row has
 * nothing the request could name.
 */
export function askForMember(snapshot: ContainerSnapshot, teamName: string | undefined): DecisionAsk | null {
  if (containerMark(snapshot)?.kind !== 'needs-decision') return null;

  const { needsDecisionSeq: seq, needsDecisionCorrelation: workflow } = snapshot;
  if (!isRowNumber(seq) || !isRowNumber(workflow)) return null;

  return { team: snapshot.team, teamName: teamName || snapshot.team, member: snapshot.id, workflow, seq };
}

/** The decision a Kanban card is waiting on: a Needs You card in `needs-decision` that names its row. */
export function askForCard(card: KanbanCard, teamName: string | undefined): DecisionAsk | null {
  if (card.status !== 'needs-decision' || card.laneId !== NeedsYouLaneId || !card.member) return null;

  const { needsDecisionSeq: seq, needsDecisionCorrelation: workflow } = card;
  if (!isRowNumber(seq) || !isRowNumber(workflow)) return null;

  return { team: card.team, teamName: teamName || card.team, member: card.member, workflow, seq };
}

/**
 * A name as one plain line. Every control character goes - a team name holding `ESC[201~` would
 * otherwise close the bracketed paste early and type the rest as keystrokes - and so does every
 * line break, so the request is one line and nothing in it submits.
 */
function plain(text: string): string {
  // eslint-disable-next-line no-control-regex
  return text.replace(/[\u0000-\u001f\u007f-\u009f\u2028\u2029]+/g, ' ').trim();
}

/** The one request the panel types: what to read, and what to answer with. One line. */
export function decisionRequest(ask: DecisionAsk): string {
  return (
    `Please help me with a decision I am asked to make. Member ${plain(ask.member)} of team ` +
    `"${plain(ask.teamName)}" (id ${plain(ask.team)}) is waiting on it: the needs-decision row ` +
    `seq ${ask.seq} in workflow ${ask.workflow}. Read that row and the workflow yourself with your ` +
    'tools, and do not act on the decision until I answer. Tell me in plain words what is being ' +
    'asked, the options and what each means for me, and your best recommendation with the reason.'
  );
}

/** Said, in the panel, when the prompt has text the person has not sent. Nothing is typed then. */
export const UnsentTextSentence =
  "The Concierge's prompt has unsent text, so nothing was typed: send or clear it first, then ask again.";

/** Said when the Concierge did not settle in time. Nothing is typed then either. */
export const NotReadySentence =
  'The Concierge did not become ready within a minute, so nothing was typed: ask again.';

/**
 * WHEN A CONCIERGE IS READY FOR TYPING: attached, its first output arrived, then quiet this long.
 *
 * Measured in this image on every built-in interactive preset, a request typed at this point was
 * taken - and so it was at 500 ms - so one generic interval serves them all and no preset is named
 * here. The figures are in the preset notes in `AgentCatalogFile.cs`.
 */
export const ReadyQuietMs = 1500;

/** How long the panel waits for that before it gives up and types nothing. */
export const ReadyCeilingMs = 60_000;

/**
 * Attached, then first output, then {@link ReadyQuietMs} of quiet. Output keeps pushing the moment
 * back; a session that never prints is never ready, and the ceiling ends the wait.
 */
export class ReadyWatch {
  private quiet: ReturnType<typeof setTimeout> | null = null;
  private ceiling: ReturnType<typeof setTimeout> | null = null;
  private done = false;

  constructor(
    private readonly onReady: () => void,
    private readonly onGaveUp: () => void,
    private readonly quietMs = ReadyQuietMs,
    ceilingMs = ReadyCeilingMs,
  ) {
    this.ceiling = setTimeout(() => this.finish(this.onGaveUp), ceilingMs);
  }

  get ready(): boolean {
    return this.done;
  }

  output(): void {
    if (this.done) return;
    if (this.quiet !== null) clearTimeout(this.quiet);
    this.quiet = setTimeout(() => this.finish(this.onReady), this.quietMs);
  }

  dispose(): void {
    this.clear();
    this.done = true;
  }

  private finish(then: () => void): void {
    if (this.done) return;
    this.clear();
    this.done = true;
    then();
  }

  private clear(): void {
    if (this.quiet !== null) clearTimeout(this.quiet);
    if (this.ceiling !== null) clearTimeout(this.ceiling);
    this.quiet = this.ceiling = null;
  }
}

/**
 * WHETHER ANYTHING WAS TYPED INTO THE CONCIERGE SINCE THE LAST SUBMIT, from the input the panel
 * forwards and nothing else - no CLI is asked and no line is cleared.
 *
 * Text adds to the line and Backspace takes one off; Enter (input ending in a bare CR, as a typed
 * Enter or a composed line is) submits it. History keys and Tab may put text there that the panel
 * never saw, so after them the line is unknown until the next submit. Keys that insert nothing -
 * Escape, the cursor keys left and right, Ctrl+<key> - and what xterm itself answers the CLI (focus
 * and mouse reports, cursor and mode reports) change nothing.
 */
export class PromptLine {
  private length = 0;
  private unknown = false;

  get unsent(): boolean {
    return this.unknown || this.length > 0;
  }

  /** The prompt is gone with its session: a new one starts empty. */
  reset(): void {
    this.length = 0;
    this.unknown = false;
  }

  /** A newline inserted in the line (Shift+Enter). */
  newline(): void {
    this.length += 1;
  }

  input(data: string): void {
    if (data.startsWith('\x1b[200~')) {
      this.length += Math.max(1, data.length - 12);
      return;
    }

    if (data === '\x1b\r') {
      this.newline();
      return;
    }

    if (data.endsWith('\r')) {
      this.reset();
      return;
    }

    if (data === '\x7f' || data === '\b') {
      this.length = Math.max(0, this.length - 1);
      return;
    }

    if (data === '\t' || /^\x1b(\[|O)[0-9;]*[AB]$/.test(data)) {
      this.unknown = true;
      return;
    }

    // An escape sequence or a lone control character inserts nothing the panel can count.
    // eslint-disable-next-line no-control-regex
    if (data.startsWith('\x1b') || /^[\u0000-\u001f]$/.test(data)) return;

    // eslint-disable-next-line no-control-regex
    this.length += data.replace(/[\u0000-\u001f\u007f]/g, '').length;
  }
}
