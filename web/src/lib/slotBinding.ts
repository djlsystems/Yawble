import { getPluginSettings, savePluginSettings } from '../api/client';
import type { PluginMemberSettings } from '../api/types';
import { initialConfig, initialSecrets, settingsBody, type PluginSettingsShape } from './pluginSettings';

/**
 * BINDS ONE SLOT OF A PLUGIN MEMBER through the member's settings route - the one a person's binding
 * uses, so the Host runs the same scope check and writes the same tenant row with it. The route
 * replaces the whole body, so the member's STORED settings are read and sent back as they are, with
 * only this slot changed: an edit not yet saved in an open form is not saved by it. A `null`
 * connection unbinds the slot.
 *
 * A refusal is thrown as the Host said it - its sentence, and `reconnect` on the body when only
 * scopes are missing. Answers the settings as now stored.
 */
export async function bindSlot(
  team: string,
  member: string,
  slot: string,
  connectionId: string | null,
): Promise<PluginMemberSettings | undefined> {
  const stored = await getPluginSettings(team, member);
  const { [slot]: _, ...others } = stored.connections ?? {};
  const shape: PluginSettingsShape = {
    config: stored.fields,
    secrets: stored.secretFields,
    connections: stored.connectionFields ?? {},
  };

  return savePluginSettings(
    team,
    member,
    settingsBody(shape, initialConfig(shape, stored.config), initialSecrets(shape, stored.secrets), connectionId ? { ...others, [slot]: connectionId } : others),
  );
}

/** What a refused binding offers: reconnecting that connection with the scopes it lacks. */
export function refusedReconnect(cause: unknown): { connectionId: string; scopes: string[] } | null {
  const body = (cause as { body?: { reconnect?: { connectionId?: unknown; scopes?: unknown } } } | null)?.body;
  const reconnect = body?.reconnect;
  if (!reconnect || typeof reconnect.connectionId !== 'string' || !Array.isArray(reconnect.scopes)) return null;
  return { connectionId: reconnect.connectionId, scopes: reconnect.scopes.map(String) };
}

/**
 * SAVES SOME SETTINGS OF A PLUGIN MEMBER through the member's settings route, as `bindSlot` binds a
 * slot: the STORED settings are read and sent back as they are, with only `changes` (setting name ->
 * value) changed. The guided wizard's Change setup saves each answer this way. A setting the plugin
 * does not declare is left out. Answers the settings as now stored.
 */
export async function saveSettingValues(
  team: string,
  member: string,
  changes: Record<string, unknown>,
): Promise<PluginMemberSettings | undefined> {
  const stored = await getPluginSettings(team, member);
  const shape: PluginSettingsShape = {
    config: stored.fields,
    secrets: stored.secretFields,
    connections: stored.connectionFields ?? {},
  };
  const config = initialConfig(shape, stored.config);
  for (const [name, value] of Object.entries(changes)) {
    const field = shape.config[name];
    if (!field) continue;
    if (field.type === 'list') config[name] = Array.isArray(value) ? value.map(String) : [];
    else if (field.type === 'bool') config[name] = value === true;
    else config[name] = value === null || value === undefined ? '' : String(value);
  }

  return savePluginSettings(
    team,
    member,
    settingsBody(shape, config, initialSecrets(shape, stored.secrets), { ...(stored.connections ?? {}) }),
  );
}
