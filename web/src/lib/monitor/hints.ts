import type { EventDefinition, MonitorHint } from '../../api/types'

/**
 * MONITOR HINTS: how a plugin says what one of its events did to something it manages.
 *
 * A plugin's manifest may give an event a `monitor` object, served on that event by
 * `GET /api/events`: an icon (a Material Symbols name, or one picked by a payload field), what
 * happened (created, changed or deleted, fixed or read from a field) and a label made from the
 * payload. The Monitor resolves it against each row of that type and draws the thing beside the
 * plugin member, instead of the plain plugin burst. Nothing here decides anything the plugin did
 * not say; a row whose type has no hint draws as before.
 */

export type ResourceAction = 'created' | 'changed' | 'deleted'

/** A hint resolved against one row: what the scene draws and the activity list says. */
export interface Resource {
  /** The plugin the event belongs to: `plugin.<id>.<suffix>`. */
  plugin: string
  icon: string
  action: ResourceAction
  label: string
}

/** What the person chose in the Monitor's settings, remembered in this browser. */
export interface ResourceSettings {
  /** Off: a hinted row draws as the plain plugin burst. */
  show: boolean
  /** How long each stays on screen, in seconds of display time. */
  seconds: number
  /** Plugins switched off. A plugin not named here is on, so a new one starts on. */
  off: string[]
}

export const SecondsDefault = 30
export const SecondsMin = 5
export const SecondsMax = 300

export const DefaultSettings: ResourceSettings = { show: true, seconds: SecondsDefault, off: [] }

const PluginPrefix = 'plugin.'

/** The plugin id of a plugin event type, or null for any other type. */
export function pluginOf(type: string): string | null {
  if (!type.startsWith(PluginPrefix)) return null
  const dot = type.indexOf('.', PluginPrefix.length)
  return dot > PluginPrefix.length ? type.slice(PluginPrefix.length, dot) : null
}

/** The part of a plugin event type after its plugin id: `plugin.azure.sqlCreated` -> `sqlCreated`. */
export function suffixOf(type: string): string {
  const plugin = pluginOf(type)
  return plugin === null ? type : type.slice(PluginPrefix.length + plugin.length + 1)
}

/** A payload value as words, or '' for anything that is not a string, number or boolean. */
function words(value: unknown): string {
  if (typeof value === 'string') return value
  if (typeof value === 'number' && Number.isFinite(value)) return String(value)
  if (typeof value === 'boolean') return String(value)
  return ''
}

/** An action field's spelling, read as one of the three. */
export function actionOf(value: string): ResourceAction {
  const word = value.trim().toLowerCase()
  if (word === 'created' || word === 'added' || word === 'create') return 'created'
  if (word === 'deleted' || word === 'removed' || word === 'delete') return 'deleted'
  return 'changed'
}

/** `{field}` placeholders filled from the payload; a field the payload lacks reads as nothing. */
export function fillLabel(template: string, payload: Record<string, unknown>): string {
  return template
    .replace(/\{([^{}]+)\}/g, (_, field: string) => words(payload[field.trim()]))
    .replace(/\s+/g, ' ')
    .trim()
}

/** A hint as the wire sent it, or null when it is not one: the Monitor never trusts the shape. */
export function readHint(value: unknown): MonitorHint | null {
  if (typeof value !== 'object' || value === null) return null
  const raw = value as Record<string, unknown>
  if (typeof raw.icon !== 'string' || raw.icon === '') return null
  const hint: MonitorHint = { icon: raw.icon }

  const by = raw.iconBy as Record<string, unknown> | null | undefined
  if (typeof by === 'object' && by !== null && typeof by.field === 'string' && typeof by.icons === 'object' && by.icons !== null) {
    const icons: Record<string, string> = {}
    for (const [key, icon] of Object.entries(by.icons as Record<string, unknown>)) if (typeof icon === 'string' && icon !== '') icons[key] = icon
    hint.iconBy = { field: by.field, icons }
  }
  if (raw.action === 'created' || raw.action === 'changed' || raw.action === 'deleted') hint.action = raw.action
  else if (typeof raw.actionField === 'string' && raw.actionField !== '') hint.actionField = raw.actionField
  else hint.action = 'changed'
  if (typeof raw.label === 'string' && raw.label.trim() !== '') hint.label = raw.label
  return hint
}

/** Every plugin event type the catalog gives a hint, by type. */
export function hintsFrom(events: readonly EventDefinition[]): Map<string, MonitorHint> {
  const hints = new Map<string, MonitorHint>()
  for (const event of events) {
    if (pluginOf(event.type) === null) continue
    const hint = readHint(event.monitor)
    if (hint) hints.set(event.type, hint)
  }
  return hints
}

/** The plugins that declare a hint on any event, sorted: one switch each in the settings. */
export function hintedPlugins(hints: ReadonlyMap<string, MonitorHint>): string[] {
  return [...new Set([...hints.keys()].map((type) => pluginOf(type)!))].sort((a, b) => a.localeCompare(b))
}

/** One row of a hinted type, resolved: which icon, what happened, and the words beside it. */
export function resolveHint(hint: MonitorHint, type: string, payload: Record<string, unknown>): Resource {
  const by = hint.iconBy
  const key = by ? words(payload[by.field]) : ''
  const icon = by && Object.hasOwn(by.icons, key) ? by.icons[key]! : hint.icon
  const action = hint.action ?? (hint.actionField ? actionOf(words(payload[hint.actionField])) : 'changed')
  const label = hint.label ? fillLabel(hint.label, payload) : ''
  return { plugin: pluginOf(type) ?? '', icon, action, label: label !== '' ? label : suffixOf(type) }
}

/** Whether a resolved row is drawn as itself, by these settings, rather than as the plain burst. */
export function drawsResource(settings: ResourceSettings, plugin: string): boolean {
  return settings.show && !settings.off.includes(plugin)
}

export function clampSeconds(value: unknown): number {
  const seconds = typeof value === 'number' ? value : Number(value)
  if (!Number.isFinite(seconds)) return SecondsDefault
  return Math.min(SecondsMax, Math.max(SecondsMin, Math.round(seconds)))
}

/** Remembered per browser, a convenience only: storage may be missing or refuse, and the defaults stand. */
export const SettingsKey = 'harness.monitor.pluginChanges'

export function readSettings(): ResourceSettings {
  try {
    const raw: unknown = JSON.parse(localStorage.getItem(SettingsKey) ?? 'null')
    if (typeof raw !== 'object' || raw === null) return { ...DefaultSettings, off: [] }
    const saved = raw as Record<string, unknown>
    return {
      show: typeof saved.show === 'boolean' ? saved.show : DefaultSettings.show,
      seconds: saved.seconds === undefined ? SecondsDefault : clampSeconds(saved.seconds),
      off: Array.isArray(saved.off) ? saved.off.filter((id): id is string => typeof id === 'string') : [],
    }
  } catch {
    return { ...DefaultSettings, off: [] }
  }
}

export function writeSettings(settings: ResourceSettings) {
  try {
    localStorage.setItem(SettingsKey, JSON.stringify(settings))
  } catch {
    // Remembered only while this page is open.
  }
}
