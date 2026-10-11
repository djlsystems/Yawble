import type {
  Connection,
  SolutionMissing,
  SolutionPersonSetting,
  SolutionPlanSetup,
  SolutionSetupConnection,
  SolutionSetupDocument,
  SolutionSetupSetting,
} from '../api/types';
import { isBlank, settingInputKind, settingKey, slotKey } from './solutions';

/**
 * THE GUIDED SETUP WIZARD'S RULES, apart from its screens: which screens a package's `setup` block
 * makes and in what order, which input each answer belongs to, the install body made from the
 * answers, and the one "Finish setup" line the panel shows for a skipped required step.
 *
 * Every word here can reach the main flow, so none is a builder's: no trigger, cron, plugin, token,
 * slot, member, and no folder path (`BuilderWords` in setup-wizard.mount.spec.ts pins it).
 */

export type SetupScreenKind = 'welcome' | 'mailbox' | 'documents' | 'preferences' | 'ready';

export interface SetupScreen {
  /** Unique within the run: `welcome`, `mailbox:Scout/mail`, `documents:Resume`, `preferences`, `ready`. */
  id: string;
  kind: SetupScreenKind;
  connection?: SolutionSetupConnection;
  document?: SolutionSetupDocument;
}

export const SetupStepTitles: Record<SetupScreenKind, string> = {
  welcome: 'Welcome',
  mailbox: 'Your mailbox',
  documents: 'Your documents',
  preferences: 'Your preferences',
  ready: 'Ready',
};

/**
 * WHICH FOLDER TO WATCH sits on a mailbox screen's Advanced…, not with the preferences: a setting of
 * the mailbox's own member named `folder` (or ending in `Folder`), which the package asks nothing about
 * in the main flow. The contract does not name it, so it is found by its name.
 */
export function isWatchFolder(setting: SolutionSetupSetting, connection: SolutionSetupConnection): boolean {
  return (
    setting.advanced &&
    setting.member.toLowerCase() === connection.member.toLowerCase() &&
    /^(folder|.*Folder)$/.test(setting.setting)
  );
}

/** The settings a mailbox screen's Advanced… shows: the folder it watches. */
export function mailboxSettings(setup: SolutionPlanSetup, connection: SolutionSetupConnection): SolutionSetupSetting[] {
  return setup.settings.filter((setting) => isWatchFolder(setting, connection));
}

/** The connections asked on screens of their own: each question not behind Advanced…. */
export const mailboxQuestions = (setup: SolutionPlanSetup) => setup.connections.filter((entry) => !entry.advanced);
export const documentQuestions = (setup: SolutionPlanSetup) => setup.documents.filter((entry) => !entry.advanced);

/** The settings of the preferences screen: every one not shown on a mailbox screen. */
export function preferenceSettings(setup: SolutionPlanSetup): SolutionSetupSetting[] {
  const mailboxes = mailboxQuestions(setup);
  return setup.settings.filter((setting) => !mailboxes.some((connection) => isWatchFolder(setting, connection)));
}

/** What the preferences screen's Advanced… adds besides settings: the inputs nobody asked about. */
export const advancedConnections = (setup: SolutionPlanSetup) => setup.connections.filter((entry) => entry.advanced);
export const advancedDocuments = (setup: SolutionPlanSetup) => setup.documents.filter((entry) => entry.advanced);

/**
 * The screens in order: Welcome, one per mailbox question, one per document question, Your
 * preferences when there is anything to set, Ready. A package asking nothing of a kind skips its screen.
 */
export function setupScreens(setup: SolutionPlanSetup): SetupScreen[] {
  const screens: SetupScreen[] = [{ id: 'welcome', kind: 'welcome' }];
  for (const connection of mailboxQuestions(setup)) {
    screens.push({ id: `mailbox:${slotKey(connection)}`, kind: 'mailbox', connection });
  }
  for (const document of documentQuestions(setup)) {
    screens.push({ id: `documents:${document.folder}`, kind: 'documents', document });
  }
  if (preferenceSettings(setup).length > 0 || advancedConnections(setup).length > 0 || advancedDocuments(setup).length > 0) {
    screens.push({ id: 'preferences', kind: 'preferences' });
  }
  screens.push({ id: 'ready', kind: 'ready' });
  return screens;
}

/** The named steps shown as progress: one per kind of screen, in order. */
export function setupSteps(screens: SetupScreen[]): SetupScreenKind[] {
  return [...new Set(screens.map((screen) => screen.kind))];
}

/** What a question says: its question, else the input's own description. */
export const questionText = (entry: { question: string | null; description: string }) =>
  entry.question ?? entry.description;

/** A setup setting read as the person-setting shape the shared inputs and bounds rules take. */
export function asPersonSetting(setting: SolutionSetupSetting): SolutionPersonSetting {
  return {
    member: setting.member,
    setting: setting.setting,
    description: setting.description,
    required: setting.required,
    type: setting.type,
    default: setting.default,
    choices: setting.choices,
    min: setting.min,
    max: setting.max,
    integer: setting.integer,
  };
}

/** The value a question starts at: its default, else empty of the right kind. */
export function startValue(setting: SolutionSetupSetting): unknown {
  const kind = settingInputKind(asPersonSetting(setting));
  if (setting.default !== null && setting.default !== undefined) return setting.default;
  if (kind === 'toggle') return false;
  if (kind === 'list' || kind === 'choices') return [];
  if (kind === 'number') return null;
  return '';
}

/** An answer in words, for the summary: a list joined, a toggle as yes or no, nothing as "nothing yet". */
export function answerWords(value: unknown): string {
  if (typeof value === 'boolean') return value ? 'yes' : 'no';
  if (isBlank(value)) return 'nothing yet';
  if (Array.isArray(value)) return value.map(String).join(', ');
  return String(value);
}

/**
 * THE INSTALL'S `settings`, as today's wizard sends them, from the guided answers: every answer that
 * says something, except one equal to the plugin's own default (from the plan's person settings),
 * which is left for the plugin - as today's wizard leaves an untouched input. A package's own
 * default is NOT the plugin's, so an untouched question with one is sent.
 */
export function setupSettingsBody(
  settings: SolutionSetupSetting[],
  values: Record<string, unknown>,
  personSettings: SolutionPersonSetting[],
): Record<string, Record<string, unknown>> {
  const body: Record<string, Record<string, unknown>> = {};
  for (const setting of settings) {
    const key = settingKey(setting);
    const value = values[key];
    const kind = settingInputKind(asPersonSetting(setting));
    if (value === undefined) continue;
    if (isBlank(value) && kind !== 'list' && kind !== 'choices') continue;
    const plugin = personSettings.find((entry) => settingKey(entry) === key);
    if (plugin && JSON.stringify(value) === JSON.stringify(plugin.default ?? (kind === 'list' || kind === 'choices' ? [] : null))) continue;
    (body[setting.member] ??= {})[setting.setting] = kind === 'number' && typeof value === 'string' ? Number(value) : value;
  }
  return body;
}

/** The team name the install is offered: the package's name, then "Name 2", "Name 3" when taken. */
export const teamNameCandidate = (name: string, attempt: number) => (attempt <= 1 ? name : `${name} ${attempt}`);

/** Whether a connection may be offered for a mailbox question: one of the kinds it takes, working. */
export function offeredFor(connection: Connection, question: SolutionSetupConnection): boolean {
  if (connection.status !== 'ok') return false;
  return question.providers.some(
    (provider) =>
      provider === connection.provider ||
      (provider === 'custom' && connection.providerKind === 'custom') ||
      (provider === 'imap' && connection.providerKind === 'imap'),
  );
}

// --- Finish setup ---------------------------------------------------------------------------------

const sameName = (a: string | null | undefined, b: string | null | undefined) =>
  (a ?? '').replace(/\/$/, '').toLowerCase() === (b ?? '').replace(/\/$/, '').toLowerCase();

/**
 * The screen a missing input is provided on, read from the same live `missing` the blocked banner
 * uses. `members` maps the team's stored member names to the package's, for an answer that names
 * the stored one. Null when no screen asks for it.
 */
export function missingScreen(
  missing: SolutionMissing,
  setup: SolutionPlanSetup,
  members: Record<string, string> = {},
): string | null {
  const packageMember = missing.member ? (members[missing.member] ?? missing.member) : null;
  const screens = setupScreens(setup);
  if (missing.kind === 'document') {
    const asked = screens.find((screen) => screen.document && sameName(screen.document.folder, missing.name));
    if (asked) return asked.id;
    return setup.documents.some((entry) => sameName(entry.folder, missing.name)) ? 'preferences' : null;
  }
  if (missing.kind === 'connection') {
    const asked = screens.find(
      (screen) => screen.connection && sameName(screen.connection.slot, missing.name) && (!packageMember || sameName(screen.connection.member, packageMember)),
    );
    if (asked) return asked.id;
    return setup.connections.some((entry) => sameName(entry.slot, missing.name)) ? 'preferences' : null;
  }
  const setting = setup.settings.find(
    (entry) => sameName(entry.setting, missing.name) && (!packageMember || sameName(entry.member, packageMember)),
  );
  if (!setting) return null;
  const mailbox = mailboxQuestions(setup).find((connection) => isWatchFolder(setting, connection));
  return mailbox ? `mailbox:${slotKey(mailbox)}` : 'preferences';
}

/** What to do for a missing input, in the person's words: "connect a mailbox", "upload your resume". */
export function finishPhrase(missing: SolutionMissing, setup: SolutionPlanSetup, members: Record<string, string> = {}): string {
  if (missing.kind === 'connection') return 'connect a mailbox';
  const packageMember = missing.member ? (members[missing.member] ?? missing.member) : null;
  if (missing.kind === 'document') {
    const asked = setup.documents.find((entry) => sameName(entry.folder, missing.name));
    if (asked?.question) return lowerFirst(asked.question);
    return `upload ${lowerFirst(asked?.description ?? 'a file').replace(/\.$/, '')}`;
  }
  const setting = setup.settings.find(
    (entry) => sameName(entry.setting, missing.name) && (!packageMember || sameName(entry.member, packageMember)),
  );
  return `answer "${setting ? questionText(setting).replace(/[.?]$/, '') : missing.description}"`;
}

/** "Finish setup: connect a mailbox", one line for every missing input a screen asks for; null when none. */
export function finishLine(missing: SolutionMissing[], setup: SolutionPlanSetup, members: Record<string, string> = {}): string | null {
  const phrases = [
    ...new Set(missing.filter((item) => missingScreen(item, setup, members) !== null).map((item) => finishPhrase(item, setup, members))),
  ];
  if (phrases.length === 0) return null;
  const last = phrases.pop()!;
  return `Finish setup: ${phrases.length > 0 ? `${phrases.join(', ')} and ${last}` : last}`;
}

function lowerFirst(text: string): string {
  return text.length > 0 ? text[0]!.toLowerCase() + text.slice(1) : text;
}
