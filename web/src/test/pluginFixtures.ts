import type {
  InstalledPlugin,
  PluginConfigField,
  PluginList,
  PluginMemberSettings,
  PluginSecretField,
} from '../api/types';

/**
 * PLUGIN FIXTURES IN THE HOST'S SHAPE, key for key, as `PluginEndpoints` writes them: `Fields` for a
 * config field, `SecretFields` for a secret, `Listing` for `GET /api/plugins` and `Settings` for the
 * member's settings route. Shared so a mount spec cannot drift into a shape the Host never sends -
 * a mock in the wrong shape is a confident test.
 */

/** A config field as `PluginEndpoints.Fields` sends it: every key present. */
export function hostField(field: Partial<PluginConfigField> & Pick<PluginConfigField, 'type'>): PluginConfigField {
  return {
    description: '',
    required: false,
    // A list with no default is sent as `[]` (`PluginConfigField.Parse`).
    default: field.type === 'list' ? [] : null,
    enum: null,
    setBy: 'anyone',
    ...field,
  };
}

/** A secret as `PluginEndpoints.SecretFields` sends it. */
export function hostSecret(secret: Partial<PluginSecretField> = {}): PluginSecretField {
  return { description: '', required: false, ...secret };
}

/** One entry of `plugins` in `GET /api/plugins`. */
export function hostPlugin(plugin: Partial<InstalledPlugin> & Pick<InstalledPlugin, 'id'>): InstalledPlugin {
  return {
    reference: `plugin:${plugin.id}`,
    name: plugin.id,
    description: '',
    version: '1.0.0',
    active: true,
    verdict: 'installed',
    protocol: 'harness.member/1',
    timeoutSeconds: 300,
    config: {},
    secrets: {},
    publishes: [],
    skills: [],
    skill: null,
    requires: [],
    members: [],
    reserved: [],
    ignored: [],
    ...plugin,
  };
}

/** `GET /api/plugins` for installed plugins alone: each one's version folder, installed and active. */
export function hostList(plugins: InstalledPlugin[], extra: Partial<PluginList> = {}): PluginList {
  return {
    plugins,
    refused: [],
    versions: plugins.map((plugin) => ({
      id: plugin.id,
      version: plugin.version,
      name: plugin.name,
      active: true,
      verdict: 'installed' as const,
      reason: null,
    })),
    ...extra,
  };
}

/** `GET .../plugin-settings` for a member of `plugin`. */
export function hostSettings(
  plugin: InstalledPlugin,
  settings: Partial<PluginMemberSettings> & Pick<PluginMemberSettings, 'team' | 'member'>,
): PluginMemberSettings {
  return {
    plugin: plugin.id,
    version: plugin.version,
    config: {},
    secrets: {},
    fields: plugin.config,
    secretFields: plugin.secrets,
    ...settings,
  };
}
