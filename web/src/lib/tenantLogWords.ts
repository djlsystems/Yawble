import type { TenantEvent } from '../api/types';
import { localInstants } from './localTime';

/**
 * THE ADMIN LOG IN WORDS. A row's detail is JSON the Host writes; for the common acts it is said
 * as a sentence here - a trigger turned off or on, its daily cap set, a setting changed - and the
 * JSON stays one toggle away. A row these words do not know reads as before: its action and its
 * JSON. Every instant in a sentence is in the reader's own zone and format.
 */

/** One entry of a `schedule.changed` row's `changed` list: a field, and its value before and after. */
interface FieldChange {
  field: string;
  from?: unknown;
  to?: unknown;
}

function parse(detail: string | null | undefined): Record<string, unknown> | null {
  if (!detail) return null;
  try {
    const value: unknown = JSON.parse(detail);
    return value && typeof value === 'object' && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
  } catch {
    return null;
  }
}

function changesOf(detail: Record<string, unknown> | null): FieldChange[] | null {
  const changed = detail?.changed;
  if (!Array.isArray(changed)) return null;
  return changed.filter((entry): entry is FieldChange => !!entry && typeof (entry as FieldChange).field === 'string');
}

const tokens = (value: unknown) => (typeof value === 'number' ? `${value.toLocaleString()} tokens` : 'no cap');

const shown = (value: unknown) =>
  value === null || value === undefined || value === '' ? 'nothing' : localInstants(typeof value === 'string' ? value : JSON.stringify(value));

/** One field of a trigger change, in words. */
function fieldWords(change: FieldChange): string {
  switch (change.field) {
    case 'enabled':
      return change.to === false ? 'turned off' : 'turned on';
    case 'dailyTokenCap':
      return change.to === null || change.to === undefined
        ? `daily cap cleared (was ${tokens(change.from)})`
        : `daily cap set to ${tokens(change.to)} (was ${tokens(change.from)})`;
    case 'instruction':
      return 'instruction changed';
    case 'filter':
      return 'filter changed';
    case 'name':
      return `renamed from ${shown(change.from)} to ${shown(change.to)}`;
    case 'member':
      return `moved from ${shown(change.from)} to ${shown(change.to)}`;
    case 'idleOnly':
      return change.to ? 'now skipped while its member is busy' : 'now queued while its member is busy';
    case 'expression':
    case 'timezone':
    case 'intervalSeconds':
    case 'fireAt':
    case 'kind':
      return `schedule changed: ${change.field} from ${shown(change.from)} to ${shown(change.to)}`;
    default:
      return `${change.field} changed from ${shown(change.from)} to ${shown(change.to)}`;
  }
}

const sentence = (parts: string[]) => {
  const text = parts.join('; ');
  return text ? `${text.charAt(0).toUpperCase()}${text.slice(1)}.` : '';
};

/**
 * What a row's Did column says: a trigger change names what it did when that was one thing - turned
 * off, turned on, its cap set - so the two read apart at a glance. Null for every other row.
 */
export function didWords(row: Pick<TenantEvent, 'action' | 'detail'>): string | null {
  if (row.action !== 'schedule.changed') return null;
  const changes = changesOf(parse(row.detail));
  if (!changes || changes.length !== 1) return 'changed trigger';
  const only = changes[0]!;
  if (only.field === 'enabled') return only.to === false ? 'turned trigger off' : 'turned trigger on';
  if (only.field === 'dailyTokenCap') return "set trigger's daily cap";
  return 'changed trigger';
}

/** The Detail column's sentence for a row, or null when these words do not know the row. */
export function detailWords(row: Pick<TenantEvent, 'action' | 'detail'>): string | null {
  const detail = parse(row.detail);
  if (!detail) return null;

  switch (row.action) {
    case 'schedule.changed': {
      const changes = changesOf(detail);
      if (changes && changes.length > 0) return sentence(changes.map(fieldWords));
      if (changes) return 'Saved with nothing changed.';
      return typeof detail.reason === 'string' ? `Trigger changed: ${detail.reason}.` : null;
    }
    case 'tenant.settingChanged':
      return `The setting ${shown(detail.setting)} changed from ${shown(detail.old)} to ${shown(detail.new)}.`;
    case 'team.budgetChanged':
      return detail.budgetTokens === null || detail.budgetTokens === undefined
        ? 'Workflow budget cleared.'
        : `Workflow budget set to ${tokens(detail.budgetTokens)}.`;
    case 'team.paused':
      return 'Paused: no new work is delivered.';
    case 'team.resumed':
      return 'Resumed.';
    default:
      return null;
  }
}
