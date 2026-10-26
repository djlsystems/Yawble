import type { PluginNamedSecrets, PluginNamedSecretState } from '../api/types';
import { localTime, wireInstant } from './localTime';

/**
 * A PLUGIN MEMBER'S NAMED SECRETS, AS A TABLE: a name a person chooses beside a value typed masked.
 * The manifest's `namedSecrets` block (`{ label, description, max }`) asks for them; a plugin that
 * declares none shows nothing. Kept out of `NamedSecretsTable` so the rules are one place and
 * testable without a mount, as `pluginSettings` is for the other fields.
 *
 * NOTHING IS SENT UNTIL THE DIALOG'S BUTTON. What a person typed, added or removed waits in a
 * `NamedSecretsDraft`; the button sends `namedSecretValuesBody` as `namedSecretValues` - name to
 * value, null removes - with the hire or the settings save. A saved value is never read back: the
 * Host answers each name with `setBy` and `setAt` only.
 */

/** A name the Host takes: a letter, then letters, digits or underscores, 64 at most. */
export const NamedSecretName = /^[A-Za-z][A-Za-z0-9_]{0,63}$/;

/** The most a manifest's block may allow, and what it allows when it says nothing. */
export const NamedSecretsDefaultMax = 20;

/** One row added on the form and not yet saved: the name and value typed into it. */
export interface NamedSecretRow {
  key: number;
  name: string;
  value: string;
}

/**
 * What waits for the dialog's button: rows added, a new value typed for a saved name, and the saved
 * names Remove holds (Keep takes one back).
 */
export interface NamedSecretsDraft {
  added: NamedSecretRow[];
  replaced: Record<string, string>;
  removed: string[];
}

export const emptyNamedSecretsDraft = (): NamedSecretsDraft => ({ added: [], replaced: {}, removed: [] });

/** The block's `max`, as the Host reads it: 20 when it gives none. */
export const namedSecretsMax = (block: PluginNamedSecrets) =>
  typeof block.max === 'number' && block.max > 0 ? block.max : NamedSecretsDefaultMax;

/** An added row nobody has typed into: left out of everything, as an empty box is. */
const blank = (row: NamedSecretRow) => row.name.trim() === '' && row.value === '';

/** How many names the member will hold once the draft is saved. */
export function namedSecretsCount(states: PluginNamedSecretState[], draft: NamedSecretsDraft): number {
  const kept = states.filter((state) => !draft.removed.includes(state.name)).length;
  return kept + draft.added.filter((row) => !blank(row)).length;
}

/**
 * Why an added row's name cannot be saved, as one sentence naming it, or null: its form, a name the
 * member holds or another row has (any case), or one of the plugin's own secret fields. The Host is
 * still the check; this only keeps the button honest.
 */
export function namedSecretNameRefusal(
  row: NamedSecretRow,
  states: PluginNamedSecretState[],
  draft: NamedSecretsDraft,
  fields: string[],
): string | null {
  const name = row.name.trim();
  if (blank(row)) return null;
  if (name === '') return 'Give this secret a name.';
  if (!NamedSecretName.test(name)) {
    return `"${name}" is not a name a secret can have: start with a letter, then use letters, digits or underscores, 64 at most.`;
  }

  const lower = name.toLowerCase();
  if (fields.some((field) => field.toLowerCase() === lower)) return `"${name}" is the name of one of this plugin's own secrets.`;
  if (states.some((state) => state.name.toLowerCase() === lower && !draft.removed.includes(state.name))) {
    return `"${name}" is already a secret of this member.`;
  }
  if (draft.added.some((other) => other.key !== row.key && other.name.trim().toLowerCase() === lower)) {
    return `"${name}" is named twice.`;
  }
  return null;
}

/** Whether an added row with a name still waits for its value. */
export const namedSecretValueMissing = (row: NamedSecretRow) => !blank(row) && row.value === '';

/**
 * Whether the draft can be sent: every added row named well and given a value, and no more names
 * than the block allows.
 */
export function namedSecretsHeld(
  block: PluginNamedSecrets | null | undefined,
  states: PluginNamedSecretState[],
  draft: NamedSecretsDraft,
  fields: string[],
): boolean {
  if (!block) return false;
  if (namedSecretsCount(states, draft) > namedSecretsMax(block)) return true;
  return draft.added.some((row) => namedSecretNameRefusal(row, states, draft, fields) !== null || namedSecretValueMissing(row));
}

/**
 * `namedSecretValues` to send: each added row and each replaced value by name, null for each name
 * Remove holds. Undefined when nothing waits, so a body without a change carries none.
 */
export function namedSecretValuesBody(draft: NamedSecretsDraft): Record<string, string | null> | undefined {
  const body: Record<string, string | null> = {};

  for (const [name, value] of Object.entries(draft.replaced)) if (value !== '' && !draft.removed.includes(name)) body[name] = value;
  for (const name of draft.removed) body[name] = null;
  for (const row of draft.added) if (row.name.trim() !== '' && row.value !== '') body[row.name.trim()] = row.value;

  return Object.keys(body).length > 0 ? body : undefined;
}

/** Where a saved name's value stands, in words: saved by whom and when. Never the value. */
export function namedSecretSetWords(state: PluginNamedSecretState): string {
  if (!state.setBy) return 'Saved.';
  const at = state.setAt ? wireInstant(state.setAt) : NaN;
  return Number.isNaN(at) ? `Saved by ${state.setBy}.` : `Saved by ${state.setBy} on ${localTime(at, { date: true })}.`;
}
