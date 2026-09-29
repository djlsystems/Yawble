import type {
  InstalledSolution,
  SolutionDiff,
  SolutionDiffSection,
  SolutionMissing,
  SolutionPersonSetting,
  SolutionPlanTrigger,
  SolutionRefusal,
  SolutionStep,
  SolutionWakeManager,
} from '../api/types';

/**
 * THE WORDS OF THE SOLUTION INSTALL WIZARD, kept out of the component so each can be read and
 * tested on its own: a trigger's source, its wake setting, its daily cap, a step's title, a
 * refusal line, which installed teams an update may choose, and what a missing input says.
 */

/** The install's steps in order, and the title the web shows for each. The Host sends `title` too. */
export const SolutionStepTitles: Record<string, string> = {
  plugins: 'Install the plugins',
  team: 'Create the team',
  members: 'Hire the members',
  skills: 'Register the team skills',
  tools: 'Copy the tools',
  sites: 'Publish the sites',
  triggers: 'Create the triggers',
  record: 'Record the package',
};

export function stepTitle(step: string, steps: SolutionStep[] = []): string {
  return steps.find((candidate) => candidate.step === step)?.title || SolutionStepTitles[step] || step;
}

/** "Failed at step 3 (Hire the members): <reason>. Nothing was left behind." */
export function failureSentence(step: string, stepNumber: number, reason: string, steps: SolutionStep[] = []): string {
  const why = reason.trim().replace(/\.$/, '');
  return `Failed at step ${stepNumber} (${stepTitle(step, steps)}): ${why}. Nothing was left behind: everything the install made was undone.`;
}

/** One refusal, as "file field: reason". */
export function refusalLine(refusal: SolutionRefusal): string {
  return `${refusal.file} ${refusal.field}: ${refusal.reason}`;
}

/**
 * THE CHECK'S ANSWER FOR A FOLDER WITH NO `solution.json`: a single refusal of the file itself.
 * Such a folder is not a package, so Install from a folder goes on to install it as a plugin.
 */
export function isNotAPackage(refusals: SolutionRefusal[]): boolean {
  return refusals.some((refusal) => refusal.file === 'solution.json' && refusal.field === '(file)');
}

/** How the Manager is woken when a run this trigger started ends, in words. */
export function wakeWords(wake: SolutionWakeManager): string {
  switch (wake) {
    case 'always':
      return 'Wakes the Manager whenever the run ends';
    case 'never':
      return 'Never wakes the Manager';
    default:
      return 'Wakes the Manager only if the run hands back or fails';
  }
}

/** "200,000 tokens a day", or "no cap". */
export function capWords(cap: number | null): string {
  return cap === null || cap === undefined ? 'no cap' : `${cap.toLocaleString('en-US')} tokens a day`;
}

function everyWords(seconds: number): string {
  if (seconds % 3600 === 0) {
    const hours = seconds / 3600;
    return hours === 1 ? 'every hour' : `every ${hours} hours`;
  }
  if (seconds % 60 === 0) {
    const minutes = seconds / 60;
    return minutes === 1 ? 'every minute' : `every ${minutes} minutes`;
  }
  return `every ${seconds} seconds`;
}

/** What fires the trigger: its schedule, its event type and filter, or its folder and glob. */
export function triggerSource(trigger: SolutionPlanTrigger): string {
  switch (trigger.kind) {
    case 'schedule': {
      if (trigger.schedule) return trigger.schedule;
      if (trigger.everySeconds) return everyWords(trigger.everySeconds);
      if (trigger.cron) return `cron ${trigger.cron} (${trigger.timezone || 'UTC'})`;
      return 'on a schedule';
    }
    case 'event':
      return `on ${trigger.eventType ?? 'an event'}${trigger.filter ? ` where ${trigger.filter}` : ''}`;
    case 'folder':
      return `a file in ${trigger.folderPath ?? ''}/${trigger.folderGlob ? ` matching ${trigger.folderGlob}` : ''}`;
    default:
      return '';
  }
}

/** Dotted versions compared number by number; a part that is not a number compares as text. */
export function compareVersions(a: string, b: string): number {
  const left = a.split(/[.+-]/);
  const right = b.split(/[.+-]/);
  for (let index = 0; index < Math.max(left.length, right.length); index++) {
    const x = left[index] ?? '0';
    const y = right[index] ?? '0';
    const nx = Number(x);
    const ny = Number(y);
    const order = Number.isNaN(nx) || Number.isNaN(ny) ? x.localeCompare(y) : nx - ny;
    if (order !== 0) return order < 0 ? -1 : 1;
  }
  return 0;
}

export interface UpdateCandidate {
  installed: InstalledSolution;
  /** Only a team on an OLDER version may be updated. */
  selectable: boolean;
  /** Why not, when it may not: "already on 1.1.0". */
  reason: string;
}

/** The installed teams from this package, each with whether an update to `version` may choose it. */
export function updateCandidates(installed: InstalledSolution[], id: string, version: string): UpdateCandidate[] {
  return installed
    .filter((row) => row.id === id)
    .map((row) => {
      const order = compareVersions(row.version, version);
      return {
        installed: row,
        selectable: order < 0,
        reason: order === 0 ? `already on ${row.version}` : order > 0 ? `already on ${row.version}, newer than ${version}` : '',
      };
    });
}

export type DiffMark = 'added' | 'changed' | null;

/** Whether a named item is new or changed in an update; null when it is neither or not an update. */
export function diffMark(section: SolutionDiffSection | undefined, name: string): DiffMark {
  if (!section) return null;
  if (section.added.includes(name)) return 'added';
  if (section.changed.some((entry) => entry === name || entry.startsWith(`${name} `))) return 'changed';
  return null;
}

/** Every removed item across the diff, as "Members: Scout". */
export function removedItems(diff: SolutionDiff): string[] {
  const labels: Record<keyof SolutionDiff, string> = {
    members: 'Member',
    triggers: 'Trigger',
    skills: 'Skill',
    sites: 'Site',
    tools: 'Tool',
    plugins: 'Plugin',
  };
  return (Object.keys(labels) as (keyof SolutionDiff)[]).flatMap((key) =>
    (diff[key]?.removed ?? []).map((name) => `${labels[key]}: ${name}`),
  );
}

/** "Waiting for Resume/ - <description>", one line per missing input. */
export function missingLine(missing: SolutionMissing): string {
  const what =
    missing.kind === 'document'
      ? `${missing.name.replace(/\/$/, '')}/`
      : missing.member
        ? `${missing.member} ${missing.kind === 'connection' ? 'connection' : 'setting'} ${missing.name}`
        : missing.name;
  return `Waiting for ${what}${missing.description ? ` - ${missing.description}` : ''}`;
}

// --- Person-only settings ------------------------------------------------------------------------

export type SettingInputKind = 'toggle' | 'number' | 'choice' | 'choices' | 'list' | 'text';

const isListType = (type: string | null) => type === 'list' || type === 'string[]' || (type ?? '').endsWith('[]');

/** Which input a person-only setting gets, from its manifest type and choices. */
export function settingInputKind(setting: SolutionPersonSetting): SettingInputKind {
  const type = setting.type;
  if (type === 'bool' || type === 'boolean') return 'toggle';
  if (isListType(type)) return setting.choices && setting.choices.length > 0 ? 'choices' : 'list';
  if (setting.choices && setting.choices.length > 0) return 'choice';
  if (type === 'number' || type === 'integer' || type === 'int') return 'number';
  return 'text';
}

/** The value an untouched input starts at: the manifest's default, else empty of the right kind. */
export function settingStartValue(setting: SolutionPersonSetting): unknown {
  const kind = settingInputKind(setting);
  if (setting.default !== null && setting.default !== undefined) return setting.default;
  if (kind === 'toggle') return false;
  if (kind === 'list' || kind === 'choices') return [];
  if (kind === 'number') return null;
  return '';
}

/** Whether a value says nothing was given: blank text, an empty list, no number. */
export function isBlank(value: unknown): boolean {
  if (value === null || value === undefined) return true;
  if (typeof value === 'string') return value.trim() === '';
  if (Array.isArray(value)) return value.length === 0;
  return false;
}

/** `settings` for the install: each value that differs from its default, by member then setting. */
export function settingsBody(
  settings: SolutionPersonSetting[],
  values: Record<string, unknown>,
): Record<string, Record<string, unknown>> {
  const body: Record<string, Record<string, unknown>> = {};
  for (const setting of settings) {
    const value = values[settingKey(setting)];
    if (value === undefined) continue;
    if (JSON.stringify(value) === JSON.stringify(settingStartValue(setting))) continue;
    if (isBlank(value) && settingInputKind(setting) !== 'list' && settingInputKind(setting) !== 'choices') continue;
    const kind = settingInputKind(setting);
    const sent = kind === 'number' && typeof value === 'string' ? Number(value) : value;
    (body[setting.member] ??= {})[setting.setting] = sent;
  }
  return body;
}

/** A kept setting's value in words: a list joined, nothing as "not set". */
export function keptValueWords(value: unknown): string {
  if (isBlank(value)) return 'not set';
  if (Array.isArray(value)) return value.map((item) => (typeof item === 'string' ? item : JSON.stringify(item))).join(', ');
  return typeof value === 'string' ? value : JSON.stringify(value);
}

export const settingKey = (setting: { member: string; setting: string }) => `${setting.member}/${setting.setting}`;
export const slotKey = (input: { member: string; slot: string }) => `${input.member}/${input.slot}`;
