import type { ConnectionSlot, PluginConfigField, PluginHire, PluginSecretField, PluginSettingValue } from '../api/types';
import { bindingsBody } from './connections';
import { boundsHint, boundsProblem } from './numberBounds';

/**
 * A PLUGIN MEMBER'S SETTINGS, AS A FORM: what each manifest field starts as, whether it is still at
 * its default, whether the form is complete, and the body the Host is sent. Shared by Add member
 * (a hire) and Member settings (an edit after hire), so both save the same shape; kept out of the
 * components so the rules are one place and testable without a mount.
 *
 * THE MANIFEST SHAPES IT. There is no free-form JSON anywhere: one input per `config` field, typed,
 * so what is saved is always a shape the plugin accepts. THE SERVER IS STILL THE CHECK - it
 * validates config against the manifest and refuses an unset required secret key, with the field
 * named; this only keeps the button honest about what is obviously missing.
 */

/** The manifest's declarations the form is built from. `InstalledPlugin` is one. */
export interface PluginSettingsShape {
  config: Record<string, PluginConfigField>;
  secrets: Record<string, PluginSecretField>;
  /** Its connection slots. Absent or `{}` for a plugin that declares none. */
  connections?: Record<string, ConnectionSlot>;
  /**
   * What the settings read found stored, for the member being edited (absent on a hire): the values
   * as loaded, and the Host's `outOfRange` sentences for the stored numbers outside their bounds.
   */
  stored?: StoredSettings;
}

/** A member's settings as read: the form values as loaded, and the Host's out-of-range sentences. */
export interface StoredSettings {
  config: PluginFieldValues;
  outOfRange: Record<string, string>;
}

/** Whether the plugin declares any connection slot, so the body carries `connections` at all. */
export const hasSlots = (shape: PluginSettingsShape) => Object.keys(shape.connections ?? {}).length > 0;

/**
 * What a person has typed or chosen, per field: text for a string or number box, a boolean for a
 * toggle, the chips for a list.
 */
export type PluginFieldValue = string | boolean | string[];
export type PluginFieldValues = Record<string, PluginFieldValue>;

/** A field's default, in the form's own terms: `[]` for a list, off for a bool with none. */
export function defaultValue(field: PluginConfigField): PluginFieldValue {
  const fallback = field.default;

  if (field.type === 'list') return Array.isArray(fallback) ? fallback.map(String) : [];
  if (field.type === 'bool') return fallback === true;

  return fallback === null || fallback === undefined || Array.isArray(fallback) ? '' : String(fallback);
}

/** A stored value, in the form's own terms. */
function asFormValue(field: PluginConfigField, stored: PluginSettingValue): PluginFieldValue {
  if (field.type === 'list') return Array.isArray(stored) ? stored.map(String) : [String(stored)];
  if (field.type === 'bool') return stored === true || stored === 'true';

  return Array.isArray(stored) ? stored.join(',') : String(stored);
}

/**
 * Each config field's starting value: what is stored for it when there is something (a member
 * being edited), else its manifest default.
 */
export function initialConfig(
  shape: PluginSettingsShape,
  stored: Record<string, PluginSettingValue> = {},
): PluginFieldValues {
  const values: PluginFieldValues = {};

  for (const [name, field] of Object.entries(shape.config)) {
    const current = stored[name];
    values[name] = current === undefined || current === null ? defaultValue(field) : asFormValue(field, current);
  }

  return values;
}

/** Each secret's LOGICAL KEY: the one bound already, else empty until a person names one. Never a value. */
export function initialSecrets(
  shape: PluginSettingsShape,
  stored: Record<string, string> = {},
): Record<string, string> {
  return Object.fromEntries(Object.keys(shape.secrets).map((name) => [name, stored[name] ?? '']));
}

/** A number box's text as the number it names, or null when it names none. */
function asNumber(text: string): number | null {
  if (text.trim() === '') return null;

  const number = Number(text);
  return Number.isFinite(number) ? number : null;
}

/** Whether a field's value is its default - the case the body leaves out and Reset returns to. */
export function isDefault(field: PluginConfigField, value: PluginFieldValue | undefined): boolean {
  const fallback = defaultValue(field);

  if (field.type === 'list') {
    const list = Array.isArray(value) ? value : [];
    const defaults = fallback as string[];
    return list.length === defaults.length && list.every((item, index) => item === defaults[index]);
  }

  if (field.type === 'bool') return (value === true) === fallback;

  const text = typeof value === 'string' ? value : '';

  if (field.type === 'number' && text.trim() !== '' && fallback !== '') {
    const typed = asNumber(text);
    return typed !== null && typed === asNumber(fallback as string);
  }

  return text.trim() === (fallback as string).trim();
}

/** "Default: upper", or null when the manifest gives the field none. */
export function defaultLabel(field: PluginConfigField): string | null {
  const fallback = field.default;

  if (field.type === 'list') {
    const list = Array.isArray(fallback) ? fallback : [];
    return `Default: ${list.length > 0 ? list.join(', ') : 'none'}`;
  }
  if (field.type === 'bool') return `Default: ${fallback === true ? 'on' : 'off'}`;
  if (fallback === null || fallback === undefined || fallback === '') return null;

  return `Default: ${String(fallback)}`;
}

/** Whether only a person may set the field. */
export const setByPerson = (field: PluginConfigField) => field.setBy === 'person';

/**
 * A required field left empty, or a required secret with no key: the names, in order. A required
 * connection slot left unbound is NOT here: the Host hires and saves a member without it and blocks
 * its runs instead, so a person may hire first and connect the account later - the slot's picker
 * says what the runs will be blocked with. A bool is
 * never empty - it is on or off - and a list is never missing: the Host defaults one to `[]`, so a
 * required list left empty saves, exactly as the Host takes it.
 */
export function missingRequired(
  shape: PluginSettingsShape,
  config: PluginFieldValues,
  secrets: Record<string, string>,
): string[] {
  const missing: string[] = [];

  for (const [name, field] of Object.entries(shape.config)) {
    if (!field.required || field.type === 'bool' || field.type === 'list') continue;

    const value = config[name];
    if (typeof value !== 'string' || value.trim() === '') missing.push(name);
  }

  for (const [name, secret] of Object.entries(shape.secrets)) {
    if (secret.required && (secrets[name] ?? '').trim() === '') missing.push(name);
  }

  return missing;
}

/**
 * Each number field whose value is outside its manifest bounds (or not whole when it must be), with
 * the sentence saying so. A value STORED out of range (bounds added after it was saved) shows here
 * too, as it is, with the Host's own sentence when the read carried one: the form never changes it
 * for the person.
 */
export function outOfRange(shape: PluginSettingsShape, config: PluginFieldValues): Record<string, string> {
  const problems: Record<string, string> = {};

  for (const [name, field] of Object.entries(shape.config)) {
    if (field.type !== 'number') continue;

    const kept = keptStored(shape, config, name);
    const problem = (kept ? shape.stored?.outOfRange[name] : undefined) ?? boundsProblem(name, field, config[name]);
    if (problem) problems[name] = problem;
  }

  return problems;
}

/**
 * The out-of-range fields that HOLD a save - as a missing required field does. A stored value sent
 * back unchanged is not one: the Host keeps it and accepts the rest of the form, so it is shown as
 * out of range but never blocks. Any other out-of-range value is refused, as the Host refuses it.
 */
export function refusedOutOfRange(shape: PluginSettingsShape, config: PluginFieldValues): string[] {
  return Object.keys(outOfRange(shape, config)).filter((name) => !keptStored(shape, config, name));
}

/** Whether `name` still holds the value it was read with (the same number, however it is typed). */
function keptStored(shape: PluginSettingsShape, config: PluginFieldValues, name: string): boolean {
  const stored = shape.stored?.config[name];
  const current = config[name];
  if (stored === undefined || current === undefined) return false;
  if (stored === current) return true;

  const [a, b] = [stored, current].map((value) => (typeof value === 'string' && value.trim() !== '' ? Number(value) : NaN));
  return Number.isFinite(a) && a === b;
}

/** A number field's bounds in words for its hint, or null for any other field or one without bounds. */
export const fieldBoundsHint = (field: PluginConfigField) => (field.type === 'number' ? boundsHint(field) : null);

/**
 * The `config` and `secrets` to store, holding ONLY what differs from the manifest's defaults - so
 * a later change to a default reaches every member that never chose otherwise. An empty text box is left out rather than sent
 * as a blank the person never meant; a number box is sent as a number, and one that does not parse
 * is sent as typed so the server's refusal names it.
 */
export function settingsBody(
  shape: PluginSettingsShape,
  config: PluginFieldValues,
  secrets: Record<string, string>,
  connections: Record<string, string> = {},
): PluginHire {
  const body: PluginHire = { config: {}, secrets: {} };

  for (const [name, field] of Object.entries(shape.config)) {
    const value = config[name];

    if (isDefault(field, value)) continue;

    if (field.type === 'list') {
      body.config[name] = Array.isArray(value) ? [...value] : [];
      continue;
    }

    if (field.type === 'bool') {
      body.config[name] = value === true;
      continue;
    }

    if (typeof value !== 'string' || value.trim() === '') continue;

    const number = field.type === 'number' ? asNumber(value) : null;
    body.config[name] = number ?? value;
  }

  for (const name of Object.keys(shape.secrets)) {
    const key = (secrets[name] ?? '').trim();
    if (key !== '') body.secrets[name] = key;
  }

  // Connection ids per slot, never a token. Sent whole for a plugin with slots - the route replaces
  // the bindings, so `{}` unbinds every slot - and not at all for one without, as before.
  if (hasSlots(shape)) body.connections = bindingsBody(shape.connections, connections);

  return body;
}

/**
 * What a list field would hold after `entry` is added, or the sentence saying why it is not: blank,
 * already there, or - for a list with an `enum` - not one of its values.
 */
export function addToList(
  field: PluginConfigField,
  current: PluginFieldValue | undefined,
  entry: string,
): { list: string[] } | { problem: string } {
  const list = Array.isArray(current) ? current : [];
  const value = entry.trim();

  if (value === '') return { problem: 'Type a value first.' };
  if (list.includes(value)) return { problem: `${value} is already in the list.` };
  if (field.enum && field.enum.length > 0 && !field.enum.includes(value)) {
    return { problem: `${value} is not allowed. Choose from: ${field.enum.join(', ')}.` };
  }

  return { list: [...list, value] };
}
