import { getPluginSettings, savePluginSettings } from '../api/client';
import type { PluginMemberSettings } from '../api/types';
import { initialConfig, initialSecrets, settingsBody, type PluginSettingsShape } from './pluginSettings';

/**
 * BINDS ONE SLOT OF A PLUGIN MEMBER through the member's settings route - the one a person's binding
 * uses, so the Host runs the same scope check and writes the same tenant row with it. The route
 * replaces the whole body, so the member's STORED settings are read and sent back as they are, with
 * only this slot changed: an edit not yet saved in an open form is not saved by it.
 *
 * A refusal is thrown as the Host said it - its sentence, and `reconnect` on the body when only
 * scopes are missing. Answers the settings as now stored.
 */
export async function bindSlot(
  team: string,
  member: string,
  slot: string,
  connectionId: string,
): Promise<PluginMemberSettings | undefined> {
  const stored = await getPluginSettings(team, member);
  const shape: PluginSettingsShape = {
    config: stored.fields,
    secrets: stored.secretFields,
    connections: stored.connectionFields ?? {},
  };

  return savePluginSettings(
    team,
    member,
    settingsBody(shape, initialConfig(shape, stored.config), initialSecrets(shape, stored.secrets), {
      ...(stored.connections ?? {}),
      [slot]: connectionId,
    }),
  );
}

/** What a refused binding offers: reconnecting that connection with the scopes it lacks. */
export function refusedReconnect(cause: unknown): { connectionId: string; scopes: string[] } | null {
  const body = (cause as { body?: { reconnect?: { connectionId?: unknown; scopes?: unknown } } } | null)?.body;
  const reconnect = body?.reconnect;
  if (!reconnect || typeof reconnect.connectionId !== 'string' || !Array.isArray(reconnect.scopes)) return null;
  return { connectionId: reconnect.connectionId, scopes: reconnect.scopes.map(String) };
}
