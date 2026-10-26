import type {
  ConnectionSlot,
  PluginConfigField,
  PluginHire,
  PluginNamedSecrets,
  PluginSecretField,
  PluginSecretState,
  PluginSettingValue,
} from '../api/types';
import { bindingsBody } from './connections';
import { namedSecretValuesBody, type NamedSecretsDraft } from './namedSecrets';
import { boundsHint, boundsProblem } from './numberBounds';
import { localTime, wireInstant } from './localTime';

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
  /** Its `namedSecrets` block (`NamedSecretsTable`). Absent or null for a plugin that declares none. */
  namedSecrets?: PluginNamedSecrets | null;
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

/**
 * Whether the field is a box whose EMPTY state stands for its default: a string or number box with
 * no `enum`. Its default is its placeholder, so a person sees it without it being typed in for them,
 * and an empty box is saved as nothing - the default, including a later change to it.
 */
const emptyIsDefault = (field: PluginConfigField) =>
  (field.type === 'string' || field.type === 'number') && !(field.enum && field.enum.length > 0);

/** A field's default, in the form's own terms: `[]` for a list, off for a bool with none, an empty box for text. */
export function defaultValue(field: PluginConfigField): PluginFieldValue {
  const fallback = field.default;

  if (field.type === 'list') return Array.isArray(fallback) ? fallback.map(String) : [];
  if (field.type === 'bool') return fallback === true;
  if (emptyIsDefault(field)) return '';

  return fallback === null || fallback === undefined || Array.isArray(fallback) ? '' : String(fallback);
}

/** A text or number box's placeholder: the manifest's default, or nothing when it gives none. */
export function placeholderOf(field: PluginConfigField): string | undefined {
  const fallback = field.default;
  if (!emptyIsDefault(field) || fallback === null || fallback === undefined || Array.isArray(fallback)) return undefined;

  const text = String(fallback);
  return text === '' ? undefined : text;
}

/** Words a person knows for a whole name that is a run of capitals or a known short form. */
const ShortForms: Record<string, string> = {
  api: 'API', id: 'ID', ip: 'IP', url: 'URL', uri: 'URI', dns: 'DNS', smtp: 'SMTP', imap: 'IMAP', http: 'HTTP',
  https: 'HTTPS', sms: 'SMS', ttl: 'TTL', json: 'JSON', ssl: 'SSL', tls: 'TLS', oauth: 'OAuth', sql: 'SQL',
};

/**
 * A field's name in words, for a manifest that gives it no `label`: `apiKey` -> "API key",
 * `clientIp` -> "Client IP", `daily_send_cap` -> "Daily send cap". Camel case, underscores and
 * dashes split it; a known short form is said in capitals, any other word in lower case, the first
 * word capitalised.
 */
export function fieldWords(name: string): string {
  const words = name
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/([A-Z]+)([A-Z][a-z])/g, '$1 $2')
    .split(/[\s_\-.]+/)
    .filter((word) => word !== '');

  const said = words.map((word) => ShortForms[word.toLowerCase()] ?? (/^[A-Z0-9]+$/.test(word) && word.length > 1 ? word : word.toLowerCase()));
  if (said.length === 0) return name;

  const first = said[0]!;
  said[0] = first.charAt(0).toUpperCase() + first.slice(1);
  return said.join(' ');
}

/** What a field is called on the form: the manifest's `label`, else its name in words. */
export function fieldLabel(name: string, field: { label?: string | null | undefined }): string {
  const label = field.label?.trim();
  return label ? label : fieldWords(name);
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

/**
 * Each secret's KEY BINDING as stored (the old way a member named a logical key), empty when none:
 * kept so "As JSON" can show it. The form never edits one, and Save sends none, which keeps them.
 */
export function initialSecrets(
  shape: PluginSettingsShape,
  stored: Record<string, string> = {},
): Record<string, string> {
  return Object.fromEntries(Object.keys(shape.secrets).map((name) => [name, stored[name] ?? '']));
}

/**
 * THE SECRET VALUES A PERSON HAS TYPED OR CLEARED ON THE FORM, by field, waiting for the dialog's
 * own button: a string sets or replaces the member's own value, null clears it. A field not here is
 * left as it is. Sent as `secretValues` with the hire and with Save.
 */
export type SecretValues = Record<string, string | null>;

/** Whether the manifest declares the field's value not secret (`masked: false`): a text box showing it. */
export const isUnmasked = (field: PluginSecretField | undefined) => field?.masked === false;

/** `secretValues` to send: what was typed or cleared, never an empty box. Undefined when nothing was. */
export function secretValuesBody(values: SecretValues): Record<string, string | null> | undefined {
  const body = Object.fromEntries(Object.entries(values).filter(([, value]) => value === null || value !== ''));
  return Object.keys(body).length > 0 ? body : undefined;
}

/**
 * Whether each secret field will have a value once the form is sent: typed here, or set now and not
 * cleared (a cleared value falls back to a bound key, when there is one). A field its settings leave
 * off for this member (`needed: false`) counts as having one: nothing waits on it. At hire there is
 * no state: only what is typed counts.
 */
export function secretsSet(
  shape: PluginSettingsShape,
  states: PluginSecretState[] | undefined,
  values: SecretValues,
): Record<string, boolean> {
  const set: Record<string, boolean> = {};

  for (const name of Object.keys(shape.secrets)) {
    const state = states?.find((entry) => entry.field === name);
    const typed = values[name];

    if (state && !state.needed) set[name] = true;
    else if (typeof typed === 'string' && typed.trim() !== '') set[name] = true;
    else if (typed === null) set[name] = state?.key != null;
    else set[name] = state?.set ?? false;
  }

  return set;
}

/**
 * Where a set secret field's value stands, in words: saved by whom and when, or from the Host's
 * environment. Never the value.
 */
export function secretSetWords(state: PluginSecretState): string {
  if (state.source === 'environment') return "Set in this Host's environment.";
  if (state.setBy) {
    const at = state.setAt ? wireInstant(state.setAt) : NaN;
    return Number.isNaN(at) ? `Saved by ${state.setBy}.` : `Saved by ${state.setBy} on ${localTime(at, { date: true })}.`;
  }
  return 'Saved.';
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
  // An empty box is the default; so is the default typed out in full.
  const declared = emptyIsDefault(field) ? placeholderOf(field) ?? '' : (fallback as string);

  if (emptyIsDefault(field) && text.trim() === '') return true;

  if (field.type === 'number' && text.trim() !== '' && declared !== '') {
    const typed = asNumber(text);
    return typed !== null && typed === asNumber(declared);
  }

  return text.trim() === declared.trim();
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
 * A required field left empty with no default, or a required secret that will have no value
 * (`secretsSet`): the names, in order. A required connection slot left unbound is NOT here: the Host
 * hires and saves a member without it and blocks its runs instead, so a person may hire first and
 * connect the account later - the slot's picker says what the runs will be blocked with. A bool is
 * never empty - it is on or off - and a list is never missing: the Host defaults one to `[]`, so a
 * required list left empty saves, exactly as the Host takes it.
 */
export function missingRequired(
  shape: PluginSettingsShape,
  config: PluginFieldValues,
  secrets: Record<string, boolean>,
): string[] {
  const missing: string[] = [];

  for (const [name, field] of Object.entries(shape.config)) {
    if (!field.required || field.type === 'bool' || field.type === 'list') continue;

    const value = config[name];
    if ((typeof value !== 'string' || value.trim() === '') && placeholderOf(field) === undefined) missing.push(name);
  }

  for (const [name, secret] of Object.entries(shape.secrets)) {
    if (secret.required && secrets[name] !== true) missing.push(name);
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
 * The `config` and `secrets` (key bindings) to store, holding ONLY what differs from the manifest's defaults - so
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
  const bindings: Record<string, string> = {};
  const body: PluginHire = { config: {}, secrets: bindings };

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
    if (key !== '') bindings[name] = key;
  }

  // Connection ids per slot, never a token. Sent whole for a plugin with slots - the route replaces
  // the bindings, so `{}` unbinds every slot - and not at all for one without, as before.
  if (hasSlots(shape)) body.connections = bindingsBody(shape.connections, connections);

  return body;
}

/** A description's first paragraph: up to its first blank line, trimmed. */
export function firstParagraph(description: string): string {
  return description.trim().split(/\n\s*\n/)[0]!.trim();
}

/**
 * THE BODY A SAVE SENDS: the settings as `settingsBody` has them, with NO `secrets` - the Host then
 * keeps the key bindings, which this form never edits - and the secret values typed or cleared.
 */
export function saveBody(
  shape: PluginSettingsShape,
  config: PluginFieldValues,
  values: SecretValues,
  connections: Record<string, string> = {},
  named?: NamedSecretsDraft,
): PluginHire {
  const { secrets: _bindings, ...settings } = settingsBody(shape, config, {}, connections);
  const typed = secretValuesBody(values);
  const namedTyped = shape.namedSecrets && named ? namedSecretValuesBody(named) : undefined;
  return {
    ...settings,
    ...(typed ? { secretValues: typed } : {}),
    ...(namedTyped ? { namedSecretValues: namedTyped } : {}),
  };
}
