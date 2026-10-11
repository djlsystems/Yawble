import type { TenantEvent } from '../api/types';
import { localInstants } from './localTime';

/**
 * THE ADMIN LOG IN WORDS. A row's detail is JSON the Host writes; every action the Host writes is
 * said here as a phrase in Did and a sentence in Detail, and the JSON stays one toggle away. A row
 * these words do not know - a verb from an older version, say - reads as before: its action and its
 * JSON. Every instant in a sentence is in the reader's own zone and format.
 *
 * `__tests__/tenantLogWords.spec.ts` reads the Host's action names from its source and fails on any
 * this table does not say, so a new action cannot reach a person as raw JSON.
 */

type Detail = Record<string, unknown>;

/** One entry of a `schedule.changed` row's `changed` list: a field, and its value before and after. */
interface FieldChange {
  field: string;
  from?: unknown;
  to?: unknown;
}

function parse(detail: string | null | undefined): Detail | null {
  if (!detail) return null;
  try {
    const value: unknown = JSON.parse(detail);
    return value && typeof value === 'object' && !Array.isArray(value) ? (value as Detail) : null;
  } catch {
    return null;
  }
}

function changesOf(detail: Detail | null): FieldChange[] | null {
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

/** The parts that are words, joined as one sentence; a part that is null, false or 0 is left out. */
const sentence = (parts: unknown[]) => {
  const text = parts.filter((part): part is string => typeof part === 'string' && part !== '').join('; ');
  return text ? `${text.charAt(0).toUpperCase()}${text.slice(1)}.` : '';
};

// ---- Reading a detail. The Host writes most fields camelCase and a few PascalCase (a C# record
// serialised with default options), so a field is looked up either way.

function get(detail: Detail | null | undefined, key: string): unknown {
  if (!detail) return undefined;
  if (key in detail) return detail[key];
  const lower = key.toLowerCase();
  const found = Object.keys(detail).find((name) => name.toLowerCase() === lower);
  return found === undefined ? undefined : detail[found];
}

/** A string field, or null when it is missing or empty. Instants read in the reader's own zone. */
function text(detail: Detail | null | undefined, key: string): string | null {
  const value = get(detail, key);
  if (typeof value === 'number') return value.toLocaleString();
  return typeof value === 'string' && value !== '' ? localInstants(value) : null;
}

function num(detail: Detail | null | undefined, key: string): number | null {
  const value = get(detail, key);
  return typeof value === 'number' ? value : null;
}

const objectOf = (value: unknown): Detail | null =>
  value && typeof value === 'object' && !Array.isArray(value) ? (value as Detail) : null;

function count(n: number, one: string, many = `${one}s`) {
  return `${n.toLocaleString()} ${n === 1 ? one : many}`;
}

/** A camelCase or PascalCase field name as words: `pendingDeliveries` is "pending deliveries". */
const keyWords = (key: string) =>
  key
    .replace(/[_-]+/g, ' ')
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .toLowerCase();

/** Any value in words, with no JSON punctuation: what a field the sentences do not shape reads as. */
function valueWords(value: unknown): string | null {
  if (value === null || value === undefined || value === '') return null;
  if (typeof value === 'string') return localInstants(value);
  if (typeof value === 'number') return value.toLocaleString();
  if (typeof value === 'boolean') return value ? 'yes' : 'no';
  if (Array.isArray(value)) {
    const items = value.map(valueWords).filter((item): item is string => !!item);
    return items.length ? items.join(', ') : null;
  }
  const fields = Object.entries(value as Detail)
    .map(([key, inner]) => {
      const words = valueWords(inner);
      return words === null ? null : `${keyWords(key)} ${words}`;
    })
    .filter((field): field is string => !!field);
  return fields.length ? `(${fields.join(', ')})` : null;
}

/** A list field's items in words, or null when it is missing or empty. */
function names(detail: Detail | null | undefined, key: string): string | null {
  const value = get(detail, key);
  return Array.isArray(value) && value.length ? valueWords(value) : null;
}

const listLength = (detail: Detail | null | undefined, key: string) => {
  const value = get(detail, key);
  return Array.isArray(value) ? value.length : 0;
};

/** Every field of a detail in words, for an act whose detail is rarely more than a fact or two. */
function fieldsInWords(detail: Detail): string {
  return (
    sentence(
      Object.entries(detail).map(([key, value]) => {
        const words = valueWords(value);
        return words === null ? null : `${keyWords(key)} ${words}`;
      }),
    ) || 'No detail.'
  );
}

/** "; on team x" when the detail names a team. */
const onTeam = (detail: Detail) => (text(detail, 'team') ? ` on team ${text(detail, 'team')}` : '');

const bySolution = (detail: Detail) => {
  const solution = get(detail, 'solution');
  if (solution === true) return 'by its solution';
  return typeof solution === 'string' && solution ? `by solution ${solution}` : null;
};

const nextDue = (detail: Detail) => (text(detail, 'nextDueAt') ? `next due ${text(detail, 'nextDueAt')}` : null);

/** A repository act's result: refused, failed, or done in the act's own words. */
function repoResult(detail: Detail, done: (detail: Detail) => string): string {
  const why = text(detail, 'reason') ?? text(detail, 'stderr') ?? text(detail, 'detail');
  if (get(detail, 'refused') === true) return sentence([`refused${why ? `: ${why}` : ''}`]);
  if (get(detail, 'conflicted') === true) return sentence([`stopped on conflicts${why ? `: ${why}` : ''}`, names(detail, 'conflicts')]);
  if (get(detail, 'success') === false) return sentence([`failed${why ? `: ${why}` : ''}`]);
  return done(detail);
}

/** A removal's leftovers: what failed and what is still on disk. */
const leftovers = (detail: Detail) => [
  names(detail, 'failures') && `failures: ${names(detail, 'failures')}`,
  names(detail, 'remaining') && `still left: ${names(detail, 'remaining')}`,
];

const sweepAge = (detail: Detail) => {
  const seconds = num(detail, 'ageSeconds');
  return seconds ? ` for ${count(Math.round(seconds / 60), 'minute')}` : '';
};

/** One section of a solution update's diff, in words, or null when nothing in it changed. */
function diffSection(label: string, section: unknown): string | null {
  const value = objectOf(section);
  if (!value) return null;
  const parts = (['added', 'changed', 'removed'] as const)
    .map((how) => (names(value, how) ? `${how} ${names(value, how)}` : null))
    .filter((part): part is string => !!part);
  return parts.length ? `${label} ${parts.join(', ')}` : null;
}

function documentItems(detail: Detail): string | null {
  const items = get(detail, 'items');
  if (!Array.isArray(items) || !items.length) return null;
  return items
    .map((item) => {
      const entry = objectOf(item);
      return entry ? `${text(entry, 'from') ?? 'an item'} to ${text(entry, 'to') ?? 'the same name'}` : valueWords(item);
    })
    .join(', ');
}

const destination = (detail: Detail) => {
  const to = objectOf(get(detail, 'to'));
  if (!to) return '';
  const path = text(to, 'path');
  return ` into ${text(to, 'folder') ?? 'a folder'}${path ? `/${path}` : ''}`;
};

function connectionSlots(detail: Detail): string | null {
  const slots = get(detail, 'slots');
  if (!Array.isArray(slots) || !slots.length) return null;
  return slots
    .map((slot) => {
      const entry = objectOf(slot);
      if (!entry) return valueWords(slot);
      return `${text(entry, 'slot') ?? 'a slot'} set to ${text(entry, 'to') ?? 'nothing'}${
        text(entry, 'from') ? ` (was ${text(entry, 'from')})` : ''
      }`;
    })
    .join(', ');
}

const runNowOutcome: Record<string, string> = {
  fired: 'ran',
  skipped: 'skipped',
  capped: 'skipped: its daily cap is spent',
  'member-missing': 'did not run: its member is gone',
};

/** What a row says: a phrase for Did, and the Detail as a sentence. */
interface Words {
  did: string;
  detail: (detail: Detail) => string;
}

const fields = (did: string): Words => ({ did, detail: fieldsInWords });

const repo = (did: string, done: (detail: Detail) => string): Words => ({ did, detail: (d) => repoResult(d, done) });

const outcomeFields = (d: Detail) => {
  const target = [text(d, 'targetValue'), text(d, 'targetUnit')].filter(Boolean).join(' ');
  return (
    sentence([
      text(d, 'name') && `name ${text(d, 'name')}`,
      text(d, 'description') && `description ${text(d, 'description')}`,
      target && `target ${target}${text(d, 'targetMetric') ? ` of ${text(d, 'targetMetric')}` : ''}`,
      text(d, 'value') && `value ${text(d, 'value')}`,
    ]) || 'Changed.'
  );
};

const sweep = (did: string, said: (detail: Detail) => string): Words => ({ did, detail: said });

const cleared: Words = { did: 'cleared a warning', detail: (d) => sentence([`cleared${text(d, 'clearedAction') ? ` (${text(d, 'clearedAction')})` : ''}`]) };

const documentsIncomplete = (verb: string): Words => ({
  did: `${verb} documents, not all`,
  detail: (d) =>
    sentence([
      `${verb} ${names(d, 'done') ?? 'nothing'}${destination(d)}`,
      names(d, 'failed') && `not ${verb}: ${names(d, 'failed')}`,
    ]),
});

const documentsDone = (verb: string): Words => ({
  did: `${verb} documents`,
  detail: (d) =>
    sentence([
      `${verb} ${documentItems(d) ?? 'nothing'}${text(d, 'folder') ? ` in ${text(d, 'folder')}` : ''}${destination(d) ? `,${destination(d)}` : ''}`,
      names(d, 'replaced') && `replaced ${names(d, 'replaced')}`,
    ]),
});

const credential = (did: string, verb: string): Words => ({
  did,
  detail: (d) =>
    sentence([`${text(d, 'kind') ?? 'credential'} ${verb} for ${text(d, 'command') ?? 'the agent'}${text(d, 'variable') ? ` in ${text(d, 'variable')}` : ''}`]),
});

const fireOrSkip = (d: Detail, what: string) =>
  sentence([
    `${what}${text(d, 'container') ? ` for ${text(d, 'container')}` : ''}${get(d, 'runNow') === true ? ' (Run now)' : ''}${get(d, 'late') === true ? ', late' : ''}`,
    text(d, 'reason'),
    num(d, 'spentToday') !== null && `spent ${tokens(num(d, 'spentToday'))} of ${tokens(get(d, 'dailyTokenCap'))} today`,
    nextDue(d),
  ]);

const runNow = (did: string): Words => ({
  did,
  detail: (d) => sentence([runNowOutcome[text(d, 'outcome') ?? ''] ?? text(d, 'outcome') ?? 'asked to run', text(d, 'reason')]),
});

const backlogFact = (did: string, said: string): Words => ({ did, detail: () => said });

/**
 * EVERY ACTION THE HOST WRITES, keyed by its name in `TenantActions`. An action with no entry here
 * fails `tenantLogWords.spec.ts`.
 */
const words: Record<string, Words> = {
  'user.signed-in': fields('signed in'),
  'user.signed-out': fields('signed out'),
  'user.created': fields('created account'),
  'user.changed': fields('changed account'),
  'user.deleted': fields('deleted account'),
  'user.password-reset': fields('reset password'),

  'tenant.settingChanged': {
    did: 'changed a setting',
    detail: (d) =>
      `The setting ${shown(get(d, 'setting'))} changed from ${shown(get(d, 'old'))} to ${shown(get(d, 'new'))}.` +
      (num(d, 'items') !== null ? ` ${count(num(d, 'items')!, 'backlog item')} took an outcome.` : ''),
  },
  'tenant.setting-reset': {
    did: 'reset a setting',
    detail: (d) => `The setting ${shown(get(d, 'setting'))} was reset to its default (was ${shown(get(d, 'old'))}).`,
  },

  'team.created': {
    did: 'created team',
    detail: (d) => {
      const local = objectOf(get(d, 'localRepository'));
      return (
        sentence([
          text(d, 'manager') && `manager ${text(d, 'manager')}`,
          text(d, 'concierge') && `Concierge ${text(d, 'concierge')}`,
          text(d, 'solution') && `from solution ${text(d, 'solution')}${text(d, 'version') ? ` ${text(d, 'version')}` : ''}`,
          local && `local repository ${text(local, 'name') ?? text(local, 'reference')} ${get(local, 'created') ? 'created' : 'linked'}`,
          names(d, 'createdOnGitHub') && `created on GitHub: ${names(d, 'createdOnGitHub')}`,
          text(d, 'retiredDocuments') && `earlier documents kept in ${text(d, 'retiredDocuments')}`,
        ]) || 'Created.'
      );
    },
  },
  'team.renamed': fields('relabelled team'),
  'team.deleting': {
    did: 'started deleting team',
    detail: (d) =>
      sentence([
        `stopping ${count(listLength(d, 'containers'), 'member')} and removing its folder`,
        get(d, 'deleteLocalRepositories') === true && 'its local repositories too',
      ]),
  },
  'team.deleted': {
    did: 'deleted team',
    detail: (d) =>
      sentence([
        num(d, 'containers') !== null && `${count(num(d, 'containers')!, 'member')} stopped`,
        num(d, 'schedules') && `${count(num(d, 'schedules')!, 'trigger')} removed`,
        num(d, 'pendingDeliveries') && `${count(num(d, 'pendingDeliveries')!, 'waiting delivery', 'waiting deliveries')} dropped`,
        listLength(d, 'directories') && `${count(listLength(d, 'directories'), 'folder')} removed`,
        names(d, 'localRepositoriesDeleted') && `local repositories deleted: ${names(d, 'localRepositoriesDeleted')}`,
        names(d, 'localRepositoriesKept') && `local repositories kept: ${names(d, 'localRepositoriesKept')}`,
        names(d, 'localRepositoryFailures') && `local repositories not deleted: ${names(d, 'localRepositoryFailures')}`,
        ...leftovers(d),
        names(d, 'sessionFoldersRemaining') && `session folders left: ${names(d, 'sessionFoldersRemaining')}`,
      ]) || 'Deleted.',
  },
  'removal.retried': {
    did: 'retried an unfinished removal',
    detail: (d) => {
      const retried = Array.isArray(get(d, 'retried')) ? (get(d, 'retried') as unknown[]).map(objectOf) : [];
      const finished = retried.filter((entry) => get(entry, 'finished') === true).length;
      return sentence([
        `retried ${count(retried.length, 'removal')}${get(d, 'atStart') === true ? ' at start' : ''}`,
        `${finished.toLocaleString()} finished`,
        retried.length - finished && `${(retried.length - finished).toLocaleString()} still unfinished`,
      ]);
    },
  },
  'team.paused': { did: 'paused team', detail: () => 'Paused: no new work is delivered.' },
  'team.resumed': { did: 'resumed team', detail: () => 'Resumed.' },
  'team.archived': {
    did: 'archived team',
    detail: (d) => sentence([`archived and paused${get(d, 'viaConcierge') === true ? ' through the Concierge' : ''}`]),
  },
  'team.unarchived': {
    did: 'unarchived team',
    detail: (d) => sentence([`unarchived${get(d, 'viaConcierge') === true ? ' through the Concierge' : ''}`, 'still paused until resumed']),
  },
  'team.budgetChanged': {
    did: 'changed workflow budget',
    detail: (d) => {
      const budget = get(d, 'budgetTokens');
      return budget === null || budget === undefined ? 'Workflow budget cleared.' : `Workflow budget set to ${tokens(budget)}.`;
    },
  },
  'team.reset': {
    did: 'reset team',
    detail: (d) => {
      const repositories = objectOf(get(d, 'repositories'));
      return (
        sentence([
          num(d, 'purged') !== null && `${count(num(d, 'purged')!, 'message')} cleared`,
          num(d, 'retained') !== null && `${count(num(d, 'retained')!, 'message')} kept`,
          names(d, 'cleared') && `cleared ${names(d, 'cleared')}`,
          repositories && `${count(listLength(repositories, 'worktreesRemoved'), 'worktree')} and ${count(listLength(repositories, 'branchesDeleted'), 'branch', 'branches')} removed`,
          ...leftovers(d),
        ]) || 'Reset.'
      );
    },
  },
  'team.reset-repositories': {
    did: "reset team's repositories",
    detail: (d) =>
      get(d, 'refused') === true
        ? sentence([`refused${text(d, 'reason') ? `: ${text(d, 'reason')}` : ''}`])
        : sentence([
            `${count(listLength(d, 'worktrees'), 'worktree')} and ${count(listLength(d, 'branches'), 'branch', 'branches')} removed`,
            names(d, 'members') && `for ${names(d, 'members')}`,
            text(d, 'teamBranch') && `team branch ${text(d, 'teamBranch')} reset`,
          ]),
  },
  'team.cloned': {
    did: 'cloned team',
    detail: (d) =>
      sentence([
        `cloned from ${text(d, 'source') ?? 'another team'}`,
        num(d, 'members') !== null && count(num(d, 'members')!, 'member'),
        num(d, 'repos') !== null && count(num(d, 'repos')!, 'repository', 'repositories'),
        num(d, 'envKeys') !== null && count(num(d, 'envKeys')!, 'environment key'),
        num(d, 'failures') && count(num(d, 'failures')!, 'failure'),
        'admin credentials minted fresh',
      ]),
  },
  'team.concierge-changed': {
    did: 'changed Concierge',
    detail: (d) => sentence([`the Concierge now runs ${text(d, 'agent') ?? 'the default agent'}`]),
  },
  'team.member-agent-changed': {
    did: "changed members' agents",
    detail: (d) => sentence([`members may use ${names(d, 'agents') ?? 'any agent'}`]),
  },
  'team.member-prompt-changed': fields("changed members' prompt"),
  'team.instructions-changed': {
    did: 'changed team instructions',
    detail: (d) => (get(d, 'set') === false ? 'Team instructions cleared.' : 'Team instructions set.'),
  },

  'backlog.item-created': {
    did: 'created backlog item',
    detail: (d) => sentence([`created${onTeam(d)}`]),
  },
  'backlog.item-edited': {
    did: 'edited backlog item',
    detail: (d) => {
      const outcome = get(d, 'outcomeId');
      return (
        sentence([
          text(d, 'state') && `state set to ${text(d, 'state')}`,
          outcome === '' && 'outcome cleared',
          typeof outcome === 'string' && outcome && `outcome set to ${outcome}`,
        ]) || 'Edited.'
      );
    },
  },
  'backlog.item-implemented': {
    did: 'marked backlog item implemented',
    detail: (d) =>
      sentence([`marked implemented${text(d, 'from') ? ` (was ${text(d, 'from')})` : ''}${get(d, 'viaConcierge') === true ? ' through the Concierge' : ''}`]),
  },
  'backlog.item-outcome-inherited': {
    did: 'gave backlog item an outcome',
    detail: (d) =>
      sentence([
        `took the outcome ${text(d, 'outcomeName') ?? text(d, 'outcomeId') ?? 'of its workflow'}${num(d, 'workflow') !== null ? ` from workflow ${num(d, 'workflow')}` : ''}`,
        text(d, 'how'),
      ]),
  },
  'backlog.item-archived': backlogFact('archived backlog item', 'Archived.'),
  'backlog.item-restored': backlogFact('restored backlog item', 'Restored.'),
  'backlog.item-deleted': backlogFact('deleted backlog item', 'Deleted.'),
  'backlog.item-dispatched': {
    did: 'sent backlog item to a team',
    detail: (d) => sentence([`sent to team ${text(d, 'team') ?? 'unknown'}${num(d, 'correlation') !== null ? ` as workflow ${num(d, 'correlation')}` : ''}`]),
  },
  'backlog.item-start-recorded': {
    did: 'recorded backlog item started',
    detail: (d) => sentence([`started${onTeam(d)}`]),
  },

  'member.added': {
    did: 'added member',
    detail: (d) => {
      const asked = text(d, 'requestedTag');
      const agent = text(d, 'resolvedAgent');
      return sentence([
        `hired${onTeam(d)}${agent ? ` running ${agent}` : ''}${asked && asked !== agent ? ` (asked for ${asked})` : ''}`,
        bySolution(d),
        names(d, 'secrets') && `secrets ${names(d, 'secrets')}`,
      ]);
    },
  },
  'member.changed': {
    did: 'changed member',
    detail: (d) =>
      sentence([
        get(d, 'renamed') === true && 'renamed',
        get(d, 'promptChanged') === true && 'prompt changed',
        text(d, 'agent') && `agent set to ${text(d, 'agent')}`,
        bySolution(d),
      ]) || 'Saved with nothing changed.',
  },
  'member.deleted': {
    did: 'removed member',
    detail: (d) =>
      sentence([
        `removed${onTeam(d)}`,
        bySolution(d) && `${bySolution(d)}${get(d, 'uninstalled') === true ? ' when it was uninstalled' : ''}`,
        num(d, 'schedules') && `${count(num(d, 'schedules')!, 'trigger')} removed`,
        num(d, 'pendingDeliveries') && `${count(num(d, 'pendingDeliveries')!, 'waiting delivery', 'waiting deliveries')} dropped`,
        ...leftovers(d),
      ]),
  },
  'member.instructions-changed': {
    did: "changed member's instructions",
    detail: (d) =>
      sentence([
        get(d, 'cleared') === true ? 'instructions cleared' : 'instructions changed',
        text(d, 'setBy') && `by ${text(d, 'setBy')}`,
        bySolution(d) && `${bySolution(d)}${text(d, 'version') ? ` ${text(d, 'version')}` : ''}${get(d, 'uninstalled') === true ? ' when it was uninstalled' : ''}`,
      ]),
  },
  'member.plugin-settings-changed': {
    did: "changed a plugin member's settings",
    detail: (d) =>
      sentence([
        `plugin ${text(d, 'plugin') ?? 'settings'}`,
        names(d, 'config') && `settings ${names(d, 'config')}`,
        names(d, 'secrets') && `secrets ${names(d, 'secrets')}`,
        names(d, 'connections') && `connections ${names(d, 'connections')}`,
      ]),
  },
  'member.connections-changed': {
    did: "changed a member's connections",
    detail: (d) =>
      sentence([
        text(d, 'plugin') && `plugin ${text(d, 'plugin')}`,
        connectionSlots(d) ?? 'no connections',
        text(d, 'clonedFrom') && `cloned from ${text(d, 'clonedFrom')}`,
      ]),
  },

  'agents.saved': {
    did: 'saved Agent catalog',
    detail: (d) => sentence([`saved ${count(num(d, 'count') ?? listLength(d, 'names'), 'agent')}${names(d, 'names') ? `: ${names(d, 'names')}` : ''}`]),
  },
  'agent.updated': {
    did: 'updated an agent',
    detail: (d) => {
      const exit = num(d, 'exitCode');
      return sentence([
        `ran ${text(d, 'command') ?? 'its update'}`,
        exit !== null && exit !== 0 && `failed with exit code ${exit}`,
        get(d, 'updated') === true
          ? `updated from ${text(d, 'versionBefore') ?? 'a version not known'} to ${text(d, 'versionAfter') ?? 'a version not known'}`
          : `version unchanged${text(d, 'versionAfter') ? ` at ${text(d, 'versionAfter')}` : ''}`,
      ]);
    },
  },
  'agent.update-cancelled': {
    did: "cancelled an agent's update",
    detail: (d) => sentence([`cancelled ${text(d, 'command') ?? 'the update'}`, num(d, 'held') !== null && `${count(num(d, 'held')!, 'run')} were held for it`]),
  },
  'agents.reset-to-seed': fields('reset the Agent catalog'),
  'agents.credential-set': credential("set an agent's credential", 'set'),
  'agents.credential-replaced': credential("replaced an agent's credential", 'replaced'),
  'agents.credential-cleared': credential("cleared an agent's credential", 'cleared'),
  'agent.foreign-tools': {
    did: 'saw an agent call foreign tools',
    detail: (d) =>
      sentence([
        `${text(d, 'agent') ?? 'the agent'}${text(d, 'member') ? ` for ${text(d, 'member')}` : ''} called ${names(d, 'called') ?? 'no tools'}`,
        `offered ${names(d, 'offered') ?? 'none'}`,
        text(d, 'run') && `run ${text(d, 'run')}`,
        d.verified === false && 'not verified',
      ]),
  },
  'tenant-agent.converted': fields('converted an agent'),

  'plugins.rescanned': {
    did: 'rescanned plugins',
    detail: (d) =>
      sentence([
        `installed ${names(d, 'installed') ?? 'nothing new'}`,
        names(d, 'refused') && `refused ${names(d, 'refused')}`,
        text(d, 'by') && `by the ${text(d, 'by')}`,
      ]),
  },
  'plugins.installed': {
    did: 'installed a plugin',
    detail: (d) =>
      get(d, 'installed') === false
        ? sentence([`not installed${text(d, 'reason') ? `: ${text(d, 'reason')}` : ''}`])
        : sentence([
            `installed ${text(d, 'id') ?? 'the plugin'}${text(d, 'version') ? ` ${text(d, 'version')}` : ''}${text(d, 'source') ? ` from ${text(d, 'source')}` : ''}`,
            get(d, 'replaced') === true && 'replacing the version there',
            bySolution(d),
          ]),
  },
  'plugins.removed': {
    did: 'removed a plugin',
    detail: (d) => sentence([`removed ${text(d, 'id') ?? 'the plugin'}${get(d, 'whole') === true ? ', every version' : names(d, 'versions') ? ` ${names(d, 'versions')}` : ''}`]),
  },

  'connections.provider-saved': {
    did: 'saved a sign-in provider',
    detail: (d) =>
      sentence([`${get(d, 'created') === true ? 'added' : 'changed'} ${text(d, 'provider') ?? 'the provider'}${names(d, 'changed') ? `: ${names(d, 'changed')}` : ''}`]),
  },
  'connections.provider-removed': {
    did: 'removed a sign-in provider',
    detail: (d) => sentence([`removed ${text(d, 'provider') ?? 'the provider'}`]),
  },
  'connections.connected': {
    did: 'connected an account',
    detail: (d) =>
      sentence([
        `connected ${text(d, 'account') ?? 'an account'} with ${text(d, 'provider') ?? 'its provider'}`,
        names(d, 'scopes') && `scopes ${names(d, 'scopes')}`,
        text(d, 'imap') && `mail in ${text(d, 'imap')}`,
        text(d, 'smtp') && `mail out ${text(d, 'smtp')}`,
      ]),
  },
  'connections.reconnected': {
    did: 'reconnected an account',
    detail: (d) =>
      sentence([
        `reconnected ${text(d, 'account') ?? 'an account'} with ${text(d, 'provider') ?? 'its provider'}`,
        get(d, 'wasNeedingReconnect') === true && 'it needed reconnecting',
        names(d, 'scopes') && `scopes ${names(d, 'scopes')}`,
      ]),
  },
  'connections.renamed': {
    did: 'renamed a connection',
    detail: (d) => `Renamed from ${shown(get(d, 'from'))} to ${shown(get(d, 'to'))}.`,
  },
  'connections.disconnected': {
    did: 'disconnected an account',
    detail: (d) =>
      sentence([`disconnected ${text(d, 'account') ?? 'an account'} from ${text(d, 'provider') ?? 'its provider'}`, text(d, 'revoke') && `revoking ${text(d, 'revoke')}`]),
  },
  'connections.revoked': {
    did: "revoked an account's access",
    detail: (d) =>
      sentence([`revoked ${text(d, 'account') ?? 'an account'} at ${text(d, 'provider') ?? 'its provider'}`, text(d, 'revoke')]),
  },
  'connections.needs-reconnect': {
    did: 'found a connection needs reconnecting',
    detail: (d) =>
      sentence([`${text(d, 'account') ?? 'the account'} with ${text(d, 'provider') ?? 'its provider'} needs reconnecting`, text(d, 'reason')]),
  },
  'connections.password-updated': {
    did: "updated a mailbox's password",
    detail: (d) =>
      sentence([`password updated for ${text(d, 'account') ?? 'the mailbox'}`, get(d, 'wasNeedingReconnect') === true && 'it needed reconnecting']),
  },

  'skill.created': {
    did: 'created skill',
    detail: (d) =>
      sentence([
        `skill ${text(d, 'name') ?? 'created'}${onTeam(d)}`,
        names(d, 'roles') && `for ${names(d, 'roles')}`,
        bySolution(d),
      ]),
  },
  'skill.changed': {
    did: 'changed skill',
    detail: (d) => {
      const renamed = text(d, 'to') && text(d, 'to') !== text(d, 'name');
      return sentence([
        `skill ${text(d, 'name') ?? 'changed'}${onTeam(d)}${renamed ? ` renamed to ${text(d, 'to')}` : ' changed'}`,
        names(d, 'roles') && `for ${names(d, 'roles')}`,
        bySolution(d),
      ]);
    },
  },
  'skill.deleted': {
    did: 'deleted skill',
    detail: (d) =>
      sentence([
        `skill ${text(d, 'name') ?? 'deleted'}${onTeam(d)} deleted`,
        text(d, 'reason'),
        bySolution(d) && `${bySolution(d)}${get(d, 'uninstalled') === true ? ' when it was uninstalled' : ''}`,
      ]),
  },

  'documents.deleted': {
    did: 'deleted documents',
    detail: (d) =>
      sentence([
        `deleted ${get(d, 'isFolder') === true ? 'the folder' : ''} ${text(d, 'path') ?? 'everything'}${text(d, 'folder') ? ` in ${text(d, 'folder')}` : ''}`.replace(/\s+/g, ' '),
        num(d, 'files') !== null && count(num(d, 'files')!, 'file'),
      ]),
  },
  'documents.delete-incomplete': {
    did: 'deleted documents, not all',
    detail: (d) =>
      sentence([
        `deleted ${num(d, 'removed')?.toLocaleString() ?? 'some'} of ${count(num(d, 'files') ?? 0, 'file')} in ${text(d, 'path') ?? text(d, 'folder') ?? 'the folder'}`,
        names(d, 'remaining') && `still there: ${names(d, 'remaining')}`,
      ]),
  },
  'documents.renamed': documentsDone('renamed'),
  'documents.moved': documentsDone('moved'),
  'documents.copied': documentsDone('copied'),
  'documents.rename-incomplete': documentsIncomplete('renamed'),
  'documents.move-incomplete': documentsIncomplete('moved'),
  'documents.copy-incomplete': documentsIncomplete('copied'),
  'document.uploaded': {
    did: 'uploaded a document',
    detail: (d) =>
      sentence([
        `uploaded ${text(d, 'path') ?? 'a file'}${num(d, 'size') !== null ? ` (${count(num(d, 'size')!, 'byte')})` : ''}`,
        num(d, 'files') !== null && `${count(num(d, 'files')!, 'file')} from a ${text(d, 'kind') ?? 'folder'}`,
        text(d, 'onClash') && `on a clash: ${text(d, 'onClash')}`,
      ]),
  },

  'concierge.attachment-added': {
    did: 'attached a file for the Concierge',
    detail: (d) => sentence([`attached ${text(d, 'type') ?? 'a file'}${num(d, 'size') !== null ? ` of ${count(num(d, 'size')!, 'byte')}` : ''}`]),
  },
  'concierge.ended-idle': {
    did: 'Concierge ended (idle)',
    detail: (d) =>
      sentence([
        `ended${text(d, 'reason') ? `: ${text(d, 'reason')}` : ''}`,
        text(d, 'lastActivity') && `last activity ${text(d, 'lastActivity')}${text(d, 'lastActivityAt') ? ` at ${text(d, 'lastActivityAt')}` : ''}`,
        get(d, 'neverViewed') === true ? 'never viewed' : text(d, 'lastViewerAt') && `last viewed ${text(d, 'lastViewerAt')}`,
      ]),
  },

  'schedule.created': {
    did: 'created trigger',
    detail: (d) => sentence([`runs ${text(d, 'member') ?? 'a member'}${onTeam(d)}`, bySolution(d)]),
  },
  'schedule.changed': {
    did: 'changed trigger',
    detail: (d) => {
      const changes = changesOf(d);
      if (changes && changes.length > 0) return sentence(changes.map(fieldWords));
      if (changes) return 'Saved with nothing changed.';
      if (text(d, 'reason')) return `Trigger changed: ${text(d, 'reason')}.`;
      return sentence([`changed${onTeam(d)}`, bySolution(d)]);
    },
  },
  'schedule.deleted': {
    did: 'deleted trigger',
    detail: (d) =>
      sentence([
        `deleted${text(d, 'member') ? `; it ran ${text(d, 'member')}` : ''}${onTeam(d)}`,
        text(d, 'reason'),
        bySolution(d) && `${bySolution(d)}${get(d, 'uninstalled') === true ? ' when it was uninstalled' : ''}`,
      ]),
  },
  'schedule.fired': { did: 'trigger ran', detail: (d) => fireOrSkip(d, 'ran') },
  'schedule.skipped': { did: 'trigger skipped', detail: (d) => fireOrSkip(d, 'skipped') },
  'schedule.missed': {
    did: 'trigger missed',
    detail: (d) => sentence([`missed ${count(num(d, 'missed') ?? 1, 'time')}${text(d, 'container') ? ` for ${text(d, 'container')}` : ''}`, nextDue(d)]),
  },
  'schedule.member-missing': {
    did: "trigger's member is gone",
    detail: (d) => sentence([`did not run: ${text(d, 'container') ?? 'its member'} is gone`, nextDue(d)]),
  },
  'schedule.run-now': runNow('ran trigger now'),
  'schedule.run-at-install': runNow('ran trigger at install'),

  'key.minted': { did: 'minted an API key', detail: (d) => sentence([`key ${text(d, 'prefix') ?? ''}…`]) },
  'key.revoked': {
    did: 'revoked an API key',
    detail: (d) => sentence([`key ${text(d, 'prefix') ?? ''}…`, text(d, 'owner') && `held by ${text(d, 'owner')}`]),
  },

  'sweep.running-no-progress': sweep('saw a run with no progress', (d) =>
    sentence([`${text(d, 'container') ?? 'a member'} has run with no progress${sweepAge(d)}`]),
  ),
  'sweep.pending-never-terminal': sweep('saw work never finish', (d) =>
    sentence([`${text(d, 'container') ?? 'a member'} accepted work that never finished${sweepAge(d)}`]),
  ),
  'sweep.quiet-team': sweep('saw a quiet team', (d) => sentence([`team ${text(d, 'team') ?? ''} has been quiet${sweepAge(d)}`])),
  'sweep.wrap-up-not-pushed': sweep('saw wrapped-up work not pushed', (d) =>
    sentence([`team ${text(d, 'team') ?? ''} wrapped up with work not pushed`]),
  ),
  'sweep.running-no-progress-cleared': cleared,
  'sweep.pending-never-terminal-cleared': cleared,
  'sweep.quiet-team-cleared': cleared,
  'sweep.wrap-up-not-pushed-cleared': cleared,

  'repo.bring-current': repo('brought a branch current', (d) => sentence([`brought current${text(d, 'from') ? ` from ${text(d, 'from')}` : ''}`])),
  'repo.merge-to-main': repo('merged to main', (d) =>
    sentence([`merged ${text(d, 'mergedFrom') ?? 'the branch'} into main`, text(d, 'mergeCommit') && `commit ${text(d, 'mergeCommit')}`]),
  ),
  'repo.bring-current-and-merge': repo('brought current and merged', (d) =>
    sentence([
      `merged and pushed ${text(d, 'pushed') ?? 'the branch'}`,
      text(d, 'mergeCommit') && `commit ${text(d, 'mergeCommit')}`,
      num(d, 'behind') && `was ${count(num(d, 'behind')!, 'commit')} behind`,
    ]),
  ),
  'repo.cleanup-worktrees': repo('cleaned up worktrees', (d) =>
    sentence([
      `removed ${names(d, 'removed') ?? 'nothing'}`,
      names(d, 'kept') && `kept ${names(d, 'kept')}`,
      names(d, 'left') && `left ${names(d, 'left')}`,
    ]),
  ),
  'repo.fetch': repo('fetched a repository', (d) => sentence(['fetched', text(d, 'defaultBranch') && `default branch ${text(d, 'defaultBranch')}`])),
  'repo.rebase': repo('rebased a branch', (d) =>
    get(d, 'aborted') === true ? 'Rebase stopped and undone.' : sentence([`rebased${text(d, 'rebase') ? ` ${text(d, 'rebase')}` : ''}`]),
  ),
  'repo.push': repo('pushed a branch', (d) => sentence([`pushed ${text(d, 'pushed') ?? 'the branch'}`])),
  'repo.default-branch-set': {
    did: 'set the default branch',
    detail: (d) =>
      sentence([
        text(d, 'setByPerson') ? `default branch set to ${text(d, 'setByPerson')}` : 'default branch left to the remote',
        text(d, 'fromRemote') && `the remote says ${text(d, 'fromRemote')}`,
      ]),
  },
  'repo.contributor-set': {
    did: 'set contribution settings',
    detail: (d) =>
      sentence([
        text(d, 'upstream') && `upstream ${text(d, 'upstream')}`,
        text(d, 'forkOwner') && `fork owner ${text(d, 'forkOwner')}`,
        `sign-off ${get(d, 'dcoSignOff') === true ? 'on' : 'off'}`,
        text(d, 'claSignedNote') && `CLA ${text(d, 'claSignedNote')}`,
        text(d, 'clone') && `clone failed: ${text(d, 'clone')}`,
      ]),
  },
  'repo.pull-request-open': repo('opened a pull request', (d) =>
    sentence([
      `${get(d, 'linked') === true ? 'linked' : 'opened'} pull request${num(d, 'number') !== null ? ` #${num(d, 'number')}` : ''}`,
      text(d, 'url'),
    ]),
  ),
  'repo.fork': repo('forked a repository', (d) => sentence([`forked to ${text(d, 'fork') ?? 'a fork'}`, text(d, 'owner') && `owned by ${text(d, 'owner')}`])),
  'repo.ask-team': {
    did: 'asked the team to resolve conflicts',
    detail: (d) => sentence([`conflicts in ${names(d, 'conflicts') ?? 'the branch'}`]),
  },
  'repo.delete-remote-branch': repo('deleted a remote branch', (d) =>
    get(d, 'alreadyGone') === true
      ? 'Already gone.'
      : sentence([`deleted${text(d, 'permittedBy') ? `: its work is in main by ${text(d, 'permittedBy')}` : ''}`]),
  ),
  'local-repo.created': {
    did: 'created a local repository',
    detail: (d) =>
      sentence([
        `${get(d, 'created') === false ? 'linked' : 'created'} ${text(d, 'reference') ?? 'the repository'}${onTeam(d)}`,
        text(d, 'defaultBranch') && `default branch ${text(d, 'defaultBranch')}`,
      ]),
  },
  'local-repo.deleted': {
    did: 'deleted a local repository',
    detail: (d) => sentence([`deleted ${text(d, 'reference') ?? 'the repository'}`]),
  },
  'local-repo.delete-incomplete': {
    did: 'deleted a local repository, not all',
    detail: (d) => sentence([`${text(d, 'reference') ?? 'the repository'} not wholly deleted`, names(d, 'remaining') && `still there: ${names(d, 'remaining')}`]),
  },

  'outcome.created': {
    did: 'created an outcome',
    detail: (d) =>
      sentence([
        `created ${text(d, 'status') ?? ''}`.trim(),
        num(d, 'workflow') !== null && `for workflow ${num(d, 'workflow')}${onTeam(d)}`,
        bySolution(d),
      ]),
  },
  'outcome.renamed': { did: 'renamed an outcome', detail: outcomeFields },
  'outcome.changed': { did: 'changed an outcome', detail: outcomeFields },
  'outcome.confirmed': { did: 'confirmed an outcome', detail: () => 'Confirmed.' },
  'outcome.merged': {
    did: 'merged an outcome',
    detail: (d) => sentence([`merged into ${text(d, 'intoName') ?? text(d, 'into') ?? 'another outcome'}`]),
  },
  'outcome.retired': { did: 'retired an outcome', detail: () => 'Retired.' },
  'outcome.reactivated': { did: 'reactivated an outcome', detail: () => 'Reactivated.' },
  'outcome.rejected': { did: 'rejected an outcome', detail: (d) => sentence([`rejected${text(d, 'reason') ? `: ${text(d, 'reason')}` : ''}`]) },
  'workflow.outcome-changed': {
    did: "changed a workflow's outcome",
    detail: (d) => {
      const workflow = num(d, 'workflow') !== null ? `workflow ${num(d, 'workflow')}` : 'a workflow';
      if (get(d, 'to') === null && text(d, 'from')) return sentence([`${workflow}${onTeam(d)} unlinked from this outcome`]);
      if (text(d, 'status') === 'proposed') return sentence([`proposed for ${workflow}${onTeam(d)}`]);
      return sentence([`${workflow}${onTeam(d)} linked to this outcome${text(d, 'how') === 'tell' ? ' when it was sent' : ''}`]);
    },
  },

  'site.created': fields('created a site'),
  'site.published': {
    did: 'published a site',
    detail: (d) =>
      sentence([
        `version ${text(d, 'version') ?? 'new'} published${text(d, 'source') ? ` from ${text(d, 'source')}` : ''}`,
        num(d, 'files') !== null && `${count(num(d, 'files')!, 'file')}, ${count(num(d, 'bytes') ?? 0, 'byte')}`,
        text(d, 'previous') && `was version ${text(d, 'previous')}`,
      ]),
  },
  'site.rolled-back': {
    did: 'rolled a site back',
    detail: (d) => sentence([`rolled back to version ${text(d, 'version') ?? 'an earlier one'}`, text(d, 'previous') && `was version ${text(d, 'previous')}`]),
  },
  'site.unpublished': {
    did: 'took a site offline',
    detail: (d) => sentence(['taken offline', text(d, 'previous') && `was version ${text(d, 'previous')}`]),
  },
  'site.deleted': {
    did: 'deleted a site',
    detail: (d) =>
      sentence([
        'deleted',
        text(d, 'reason'),
        num(d, 'documents') !== null && `${count(num(d, 'documents')!, 'record')}, ${count(num(d, 'bytes') ?? 0, 'byte')}`,
      ]),
  },

  'solution.installed': {
    did: 'installed a solution',
    detail: (d) => {
      const members = objectOf(get(d, 'members'));
      return sentence([
        `installed ${text(d, 'id') ?? 'the solution'}${text(d, 'version') ? ` ${text(d, 'version')}` : ''}${text(d, 'reinstalledFrom') ? ` over ${text(d, 'reinstalledFrom')}` : ''}`,
        members && Object.keys(members).length && `members ${Object.keys(members).join(', ')}`,
        names(d, 'triggers') && `triggers ${names(d, 'triggers')}`,
        names(d, 'skills') && `skills ${names(d, 'skills')}`,
        names(d, 'sites') && `sites ${names(d, 'sites')}`,
        names(d, 'tools') && `tools ${names(d, 'tools')}`,
        objectOf(get(d, 'plugins')) && Object.keys(objectOf(get(d, 'plugins'))!).length && `plugins ${Object.keys(objectOf(get(d, 'plugins'))!).join(', ')}`,
      ]);
    },
  },
  'marketplace.fetched': {
    did: 'fetched a package',
    detail: (d) =>
      sentence([
        `fetched ${text(d, 'id') ?? 'a package'}${text(d, 'version') ? ` ${text(d, 'version')}` : ''}${text(d, 'folder') ? ` into ${text(d, 'folder')}` : ''}`,
        text(d, 'sha256') && `sha256 ${text(d, 'sha256')}`,
      ]),
  },
  'solution.updated': {
    did: 'updated a solution',
    detail: (d) => {
      const diff = objectOf(get(d, 'diff'));
      const sections = (['members', 'triggers', 'skills', 'sites', 'tools', 'plugins'] as const).map((section) => diffSection(section, get(diff, section)));
      return sentence([
        `updated ${text(d, 'id') ?? 'the solution'}${text(d, 'from') ? ` from ${text(d, 'from')}` : ''}${text(d, 'to') ? ` to ${text(d, 'to')}` : ''}`,
        ...(sections.some(Boolean) ? sections : ['nothing in it changed']),
      ]);
    },
  },
  'solution.failed': {
    did: 'solution failed',
    detail: (d) =>
      text(d, 'step')
        ? sentence([
            `${text(d, 'id') ?? 'the solution'}${text(d, 'version') ? ` ${text(d, 'version')}` : ''} failed at ${text(d, 'step')}${text(d, 'reason') ? `: ${text(d, 'reason')}` : ''}`,
            names(d, 'undone') && `undone: ${names(d, 'undone')}`,
            names(d, 'notUndone') && `not undone: ${names(d, 'notUndone')}`,
          ])
        : sentence([`${text(d, 'id') ?? 'the solution'} failed`, text(d, 'restored') && `restored to ${text(d, 'restored')}`]),
  },
  'solution.uninstalled': {
    did: 'uninstalled a solution',
    detail: (d) => {
      const removed = objectOf(get(d, 'removed'));
      const plugins = objectOf(get(d, 'plugins'));
      return sentence([
        `uninstalled ${text(d, 'id') ?? 'the solution'}${text(d, 'version') ? ` ${text(d, 'version')}` : ''}`,
        names(removed, 'members') && `members removed ${names(removed, 'members')}`,
        names(removed, 'triggers') && `triggers removed ${names(removed, 'triggers')}`,
        names(removed, 'skills') && `skills removed ${names(removed, 'skills')}`,
        get(removed, 'tools') === true && 'tools folder removed',
        names(d, 'sitesKept') && `sites kept offline ${names(d, 'sitesKept')}`,
        names(plugins, 'removed') && `plugins removed ${names(plugins, 'removed')}`,
        names(plugins, 'kept') && `plugins kept ${names(plugins, 'kept')}`,
        text(d, 'documentsKept') && `documents kept in ${text(d, 'documentsKept')}`,
        names(d, 'failures') && `could not remove ${names(d, 'failures')}`,
      ]);
    },
  },
};

/**
 * What a row's Did column says, or null for an action these words do not know: a trigger change
 * names what it did when that was one thing - turned off, turned on, its cap set - so the two read
 * apart at a glance.
 */
export function didWords(row: Pick<TenantEvent, 'action' | 'detail'>): string | null {
  if (row.action === 'schedule.changed') {
    const changes = changesOf(parse(row.detail));
    if (!changes || changes.length !== 1) return 'changed trigger';
    const only = changes[0]!;
    if (only.field === 'enabled') return only.to === false ? 'turned trigger off' : 'turned trigger on';
    if (only.field === 'dailyTokenCap') return "set trigger's daily cap";
    return 'changed trigger';
  }
  return words[row.action]?.did ?? null;
}

/** The Detail column's sentence for a row, or null when these words do not know the row. */
export function detailWords(row: Pick<TenantEvent, 'action' | 'detail'>): string | null {
  const detail = parse(row.detail);
  const known = words[row.action];
  return detail && known ? known.detail(detail) : null;
}
