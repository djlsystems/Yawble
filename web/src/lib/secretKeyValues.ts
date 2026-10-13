import { ActionRefused, clearSecretKey, setSecretKey } from '../api/client';
import type { SecretKeyState } from '../api/types';

/**
 * KEY VALUES TYPED ON A FORM, WAITING FOR THE FORM'S OWN BUTTON. A key's value field has no button
 * of its own: what a person types there is written when they press the form's Save (Add member,
 * Install), as every other field on it is. Each entry is a value to store, or a clear of a key set
 * from the screen. By key name, so two fields bound to one key are one write.
 */
export type PendingKeyValue = { value: string } | { clear: true };
export type PendingKeyValues = Record<string, PendingKeyValue>;

export const isClear = (entry: PendingKeyValue | undefined): entry is { clear: true } =>
  entry !== undefined && 'clear' in entry;

/** Whether anything is waiting to be written: a value typed, or a clear asked for. */
export function hasPendingKeys(pending: PendingKeyValues): boolean {
  return Object.values(pending).some((entry) => isClear(entry) || entry.value !== '');
}

function refusal(cause: unknown): string {
  if (cause instanceof ActionRefused && typeof cause.body.refusal === 'string') return cause.body.refusal;
  return cause instanceof Error ? cause.message : String(cause);
}

/**
 * Writes each waiting value, in order, handing the Host's answer for each to `written` (whose
 * caller drops the entry and shows the key's state now). The first refusal stops it with a sentence
 * naming the key and never the value; what was written before it stays written.
 */
export async function writeKeyValues(
  pending: PendingKeyValues,
  written: (state: SecretKeyState) => void,
): Promise<void> {
  for (const [key, entry] of Object.entries(pending)) {
    if (!isClear(entry) && entry.value === '') continue;
    try {
      written(isClear(entry) ? await clearSecretKey(key) : await setSecretKey(key, entry.value));
    } catch (cause) {
      throw new Error(`${key} was not ${isClear(entry) ? 'cleared' : 'saved'}: ${refusal(cause)}`);
    }
  }
}
