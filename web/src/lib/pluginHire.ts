import type { InstalledPlugin, PluginHire } from '../api/types';

/**
 * HIRING A PLUGIN FROM THE ADD MEMBER DIALOG: what its manifest's fields start as, whether a hire
 * is complete, and the body the hire route is sent. Kept out of the component so the rules are one
 * place and testable without a mount.
 *
 * THE SERVER IS THE CHECK. It validates config against the manifest and refuses an unset required
 * secret key, with the field named; this only keeps the button honest about what is obviously
 * missing.
 */

/** What a person has typed or chosen, per field. Every input holds a string except a toggle. */
export type PluginFieldValues = Record<string, string | boolean>;

/** Each config field's starting value: its manifest default, else empty (or off for a bool). */
export function initialConfig(plugin: InstalledPlugin): PluginFieldValues {
  const values: PluginFieldValues = {};

  for (const [name, field] of Object.entries(plugin.config)) {
    const fallback = field.default;
    if (field.type === 'bool') values[name] = fallback === true;
    else values[name] = fallback === null || fallback === undefined ? '' : String(fallback);
  }

  return values;
}

/** Each secret's LOGICAL KEY, empty until a person names one. Never a value. */
export function initialSecrets(plugin: InstalledPlugin): Record<string, string> {
  return Object.fromEntries(Object.keys(plugin.secrets).map((name) => [name, '']));
}

/** A required config field left empty, or a required secret with no key: the names, in order. */
export function missingRequired(
  plugin: InstalledPlugin,
  config: PluginFieldValues,
  secrets: Record<string, string>,
): string[] {
  const missing: string[] = [];

  for (const [name, field] of Object.entries(plugin.config)) {
    const value = config[name];
    if (field.required && field.type !== 'bool' && (typeof value !== 'string' || value.trim() === '')) {
      missing.push(name);
    }
  }

  for (const [name, secret] of Object.entries(plugin.secrets)) {
    if (secret.required && (secrets[name] ?? '').trim() === '') missing.push(name);
  }

  return missing;
}

/**
 * The hire's `config` and `secrets`. An empty string field is LEFT OUT, so the manifest's default
 * (or its absence) holds rather than a blank the person never meant; a number field is sent as a
 * number, and one that does not parse is sent as typed so the server's refusal names it.
 */
export function pluginHire(
  plugin: InstalledPlugin,
  config: PluginFieldValues,
  secrets: Record<string, string>,
): PluginHire {
  const hire: PluginHire = { config: {}, secrets: {} };

  for (const [name, field] of Object.entries(plugin.config)) {
    const value = config[name];

    if (field.type === 'bool') {
      hire.config[name] = value === true;
      continue;
    }

    if (typeof value !== 'string' || value.trim() === '') continue;

    const number = Number(value);
    hire.config[name] = field.type === 'number' && value.trim() !== '' && Number.isFinite(number) ? number : value;
  }

  for (const name of Object.keys(plugin.secrets)) {
    const key = (secrets[name] ?? '').trim();
    if (key !== '') hire.secrets[name] = key;
  }

  return hire;
}
