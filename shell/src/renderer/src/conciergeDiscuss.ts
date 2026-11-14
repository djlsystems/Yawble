import { ref } from 'vue';
import type { BoardCardDetail, NetworkAsk, ToolAsk } from '../../shared/api';
import { displayName } from './faces/faces';

/**
 * "Discuss with the Concierge" from anywhere in the window (Needs You, the Kanban card panel): a new Concierge chat about
 * that team with the question already in the box, for the person to send or change. The Concierge only advises: the
 * person still answers the ask (Allow, Install, Move...) themselves. App.vue opens the chat when a request comes in.
 */
export interface DiscussRequest { teamId: number | null; text: string; seq: number }

export const discussRequest = ref<DiscussRequest | null>(null);
let seq = 0;

export function discussWithConcierge(teamId: number | null, text: string): void {
  discussRequest.value = { teamId, text, seq: ++seq };
}

/** Agent-written text as a Markdown quote, clipped: the Concierge reads it as the agent's words, not the person's. */
export function quoted(text: string, max = 1200): string {
  const t = text.trim();
  const clipped = t.length > max ? `${t.slice(0, max - 1)}…` : t;
  return clipped.split('\n').map((l) => `> ${l}`).join('\n');
}

const decideYourself = 'Don\'t answer it for me: I\'ll choose in the window.';

export function networkAskPrompt(a: NetworkAsk): string {
  const who = `${a.team}'s ${displayName(a.memberName)}`;
  const what = a.reason
    ? `Its reason, in its own words:\n\n${quoted(a.reason)}`
    : `It gave no reason; what was refused: ${[a.method, a.pathSample].filter(Boolean).join(' ') || 'a request'}.`;
  return [
    `${who} asks to reach the host \`${a.host}\` (refused ${a.count === 1 ? 'once' : `${a.count} times`}).`,
    what,
    ...(a.hint ? [`The app's note on this host: ${a.hint}`] : []),
    `Help me decide: what is this host, does the team really need it for its current work, is it safe, and which answer fits best: allow for this agent, for this team, everywhere, or deny? ${decideYourself}`,
  ].join('\n\n');
}

export function toolAskPrompt(a: ToolAsk): string {
  const who = `${a.team}'s ${displayName(a.memberName)}`;
  const tool = `${a.displayName ?? a.tool}${a.publisher ? ` (${a.publisher})` : ''}`;
  return [
    `${who} asks me to install ${tool} on this computer${a.count > 1 ? ` (asked ${a.count} times)` : ''}.`,
    ...(a.reason ? [`Its reason, in its own words:\n\n${quoted(a.reason)}`] : []),
    ...(a.command ? [`The install command would be: \`${a.command}\``] : []),
    `Help me decide: does the team need it for its current work, is there a way to do without it, and is installing it safe? ${decideYourself}`,
  ].join('\n\n');
}

export function cardPrompt(c: BoardCardDetail, laneLabel: string, statusLabel: string): string {
  const who = c.member ? displayName(c.member) : 'nobody yet';
  const recent = c.trail.slice(-5).map((t) => `- ${t.source === 'you' ? 'You' : displayName(t.source)} (${t.kind}): ${t.text.replace(/\s+/g, ' ').slice(0, 300)}`);
  return [
    `About card ${c.id} on ${c.team}'s Kanban board: "${c.title}", assigned to ${who}, ${statusLabel}, in ${laneLabel}${c.item ? `, from backlog item ${c.item}` : ''}${c.workflow ? `, workflow #${c.workflow}` : ''}.`,
    ...(c.body.trim() ? [`The card says:\n\n${quoted(c.body)}`] : []),
    ...(recent.length ? [`Its latest updates:\n${recent.join('\n')}`] : []),
    'Help me understand where this stands, whether it needs anything from me, and what I should do next.',
  ].join('\n\n');
}

/** The team page's branch sync notice: a member's work that could not be merged with the base, and its files. */
export function syncNotePrompt(team: string, note: string): string {
  return [
    `The app says ${team}'s branches could not be brought up to date automatically:`,
    quoted(note),
    'Help me understand what this conflict means, who has to resolve it (the member, the Manager or me), and whether I need to do anything.',
  ].join('\n\n');
}

/** The top line when git is missing, too old or not working. */
export function gitPrompt(text: string): string {
  return [
    `The app says: ${text}`,
    'Help me fix this: what should I install or change on this computer, and how do I check it worked?',
  ].join('\n\n');
}

/** The startup box when this computer lacks what agents need (Linux: AppArmor, bubblewrap, slirp4netns). */
export function hostChecksPrompt(checks: { message: string; fix?: string | null }[]): string {
  return [
    'The app says this computer is not ready to run agents:',
    checks.map((c) => `- ${c.message}${c.fix ? ` Fix: \`${c.fix}\`` : ''}`).join('\n'),
    'Help me understand what each of these means and fix them, one at a time.',
  ].join('\n\n');
}
