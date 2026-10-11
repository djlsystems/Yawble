import type {
  InstalledSolution,
  SolutionCheck,
  SolutionDiff,
  SolutionKept,
  SolutionPlan,
  SolutionPlanTrigger,
  SolutionPreview,
  SolutionSecret,
  SolutionStep,
} from '../api/types';

/**
 * SOLUTION FIXTURES IN THE HOST'S SHAPE, key for key as the contract writes them (camelCase, every
 * key present), modelled on the Job Tracker test package, `tests/Fixtures/Packages/job-tracker`. Shared so the wizard, deep link,
 * Plugins dialog and banner specs cannot drift into a shape the Host never sends.
 */

export const Folder = '/data/documents/packages/job-tracker';

/** Long on purpose: the review must show it whole, never shortened. */
export const LongInstruction =
  '{event.by} pressed Apply on {event.payload}. Run `python3 {solution}/make-cover-letter.py` with that ' +
  "posting's JSON and the resume's path to get a first draft, then finish the letter and save it as " +
  "Drafts/<job id>.md in the team's documents. Set the posting's status to `drafted` on the tracker and " +
  'hand back naming the file. Keep it to one page, never invent experience the resume does not show, ' +
  'and END-OF-THE-APPLY-INSTRUCTION.';

export const WriterInstructions =
  'You write cover letters. Read the person\'s resume from the Resume folder, read the posting from the ' +
  'tracker, and start from the draft {solution}/make-cover-letter.py makes. Keep it to one page. ' +
  'Write plainly, in the first person, and never claim anything the resume does not support. END-OF-WRITER.';

function trigger(fields: Partial<SolutionPlanTrigger> & Pick<SolutionPlanTrigger, 'name' | 'kind' | 'member' | 'instruction'>): SolutionPlanTrigger {
  return {
    platformKind: fields.kind,
    wakeManager: 'onHandbackOrFailure',
    dailyTokenCap: null,
    idleOnly: true,
    schedule: null,
    cron: null,
    timezone: null,
    everySeconds: null,
    eventType: null,
    filter: null,
    folderPath: null,
    folderGlob: null,
    ...fields,
  };
}

export function hostPlan(version = '1.1.0'): SolutionPlan {
  return {
    package: {
      id: 'job-tracker',
      name: 'Job Tracker',
      version,
      description: 'Finds job postings, tracks them on a page, and drafts a cover letter when you press Apply.',
      folder: Folder,
      readme: true,
    },
    team: { name: 'Job Tracker', instructions: 'This team runs a job search for one person.' },
    members: [
      {
        name: 'Coordinator',
        kind: 'agent',
        role: 'manager',
        preset: 'Manager',
        instructions: 'You coordinate the job search. Each weekday morning you tell the person what is new. END-OF-COORDINATOR.',
        pluginId: null,
        pluginVersion: null,
        settings: {},
      },
      {
        name: 'Scout',
        kind: 'plugin',
        role: 'member',
        preset: null,
        instructions: '',
        pluginId: 'job-board',
        pluginVersion: '0.2.0',
        settings: { keywords: ['engineer'] },
      },
      {
        name: 'Writer',
        kind: 'agent',
        role: 'member',
        preset: null,
        instructions: WriterInstructions,
        pluginId: null,
        pluginVersion: null,
        settings: {},
      },
    ],
    plugins: [
      {
        id: 'job-board',
        name: 'Job Board (sample)',
        version: '0.2.0',
        description: 'A stand-in job board.',
        folder: `${Folder}/plugins/job-board`,
        events: ['plugin.job-board.posting-found'],
      },
    ],
    triggers: [
      trigger({
        name: 'Scan for postings',
        kind: 'schedule',
        member: 'Scout',
        instruction: 'scan',
        wakeManager: 'never',
        everySeconds: 3600,
      }),
      trigger({
        name: 'Apply pressed',
        kind: 'event',
        member: 'Writer',
        instruction: LongInstruction,
        dailyTokenCap: 400000,
        eventType: 'site.action',
        filter: 'siteAction eq tracker/apply',
      }),
      trigger({
        name: 'Resume changed',
        kind: 'folder',
        member: 'Writer',
        instruction: 'The resume in Resume/ changed ({event.changed}). Re-read it. END-OF-RESUME.',
        wakeManager: 'always',
        dailyTokenCap: 300000,
        folderPath: 'Resume',
        folderGlob: '*',
      }),
      trigger({
        name: 'Morning summary',
        kind: 'schedule',
        member: 'Coordinator',
        instruction: 'Read the tracker. Tell the person, in five lines at most, what is new. END-OF-MORNING.',
        wakeManager: 'never',
        dailyTokenCap: 100000,
        schedule: 'At 08:00, Monday to Friday (Europe/London)',
        cron: '0 0 8 * * 1-5',
        timezone: 'Europe/London',
      }),
    ],
    skills: [
      {
        name: 'job-search-playbook',
        description: 'How this team runs a job search.',
        roles: ['manager', 'member'],
        file: 'skills/job-search-playbook.md',
        body: '# The playbook\n\nRead the tracker first. END-OF-PLAYBOOK.',
      },
    ],
    sites: [{ name: 'tracker', folder: `${Folder}/sites/tracker`, files: ['index.html', 'app.js', 'style.css'] }],
    tools: { folder: 'tools', installedAs: 'solution', files: ['make-cover-letter.py'] },
    inputs: {
      settings: [{ member: 'Scout', setting: 'sources', description: 'Which job boards the Scout may read.', required: false }],
      connections: [{ member: 'Scout', slot: 'mail', description: 'Where postings are emailed from.', required: true }],
      documents: [{ folder: 'Resume', description: 'Your reference resume, .docx or PDF.', required: true }],
    },
    personSettings: [
      {
        member: 'Scout',
        setting: 'sources',
        description: 'Which job boards the Scout may read.',
        required: false,
        type: 'list',
        default: [],
        choices: ['sample', 'other'],
      },
      {
        member: 'Scout',
        setting: 'region',
        description: 'Where to look.',
        required: true,
        type: 'string',
        default: null,
        choices: null,
      },
      {
        member: 'Scout',
        setting: 'limit',
        description: 'Most postings a scan keeps.',
        required: false,
        type: 'integer',
        default: 20,
        choices: null,
      },
    ],
    personConnections: [
      {
        member: 'Scout',
        slot: 'mail',
        description: 'Where postings are emailed from.',
        required: true,
        plugin: 'job-board',
        providers: ['microsoft'],
        scopes: { microsoft: ['https://graph.microsoft.com/Mail.Send'] },
      },
    ],
    ignored: [],
  };
}

/**
 * The preview's secrets, as the Host names them: by key, never a value. ADZUNA_APP_ID is set on the
 * Host, USAJOBS_API_KEY is not, and THEMUSE_API_KEY is needed only when the Scout's sources hold
 * `other` - which waits on the person's answer, so the Host says `needed: null`.
 */
export function hostSecrets(): SolutionSecret[] {
  const setWith = (key: string) => `the operator CLI's \`secret set ${key}\` (it prompts for the value), then its \`up\` to restart the Host`;
  return [
    { member: 'Scout', field: 'adzunaAppId', key: 'ADZUNA_APP_ID', description: 'Your Adzuna application id.', required: false, when: null, set: true, needed: true, setWith: setWith('ADZUNA_APP_ID') },
    { member: 'Scout', field: 'usajobsApiKey', key: 'USAJOBS_API_KEY', description: 'Your USAJOBS API key.', required: false, when: null, set: false, needed: true, setWith: setWith('USAJOBS_API_KEY') },
    { member: 'Scout', field: 'themuseApiKey', key: 'THEMUSE_API_KEY', description: 'Your The Muse API key.', required: false, when: { setting: 'sources', value: 'other' }, set: false, needed: null, setWith: setWith('THEMUSE_API_KEY') },
  ];
}

export function okCheck(plan = hostPlan()): SolutionCheck {
  return { ok: true, folder: plan.package.folder, plan, refusals: [] };
}

export const Connections = [
  { id: 'conn-1', name: 'Work mail', provider: 'microsoft', account: 'dana@example.com', status: 'ok' },
];

export function installPreview(
  teamName = 'Job Tracker',
  nameRefusal: string | null = null,
  plan = hostPlan(),
  secrets: SolutionSecret[] = [],
): SolutionPreview {
  return { ok: true, mode: 'install', teamName, nameRefusal, plan, connections: Connections, secrets };
}

export const UpdateDiff: SolutionDiff = {
  members: { added: ['Writer'], changed: ['Coordinator'], removed: ['Clerk'] },
  triggers: { added: ['Apply pressed'], changed: ['Scan for postings'], removed: ['Evening summary'] },
  skills: { added: [], changed: ['job-search-playbook'], removed: [] },
  sites: { added: [], changed: [], removed: [] },
  tools: { added: ['make-cover-letter.py'], changed: [], removed: [] },
  plugins: { added: [], changed: ['job-board 0.1.0 -> 0.2.0'], removed: [] },
};

export function updatePreview(team = 'job-tracker', plan = hostPlan(), kept?: SolutionKept): SolutionPreview {
  return {
    ok: true,
    mode: 'update',
    team,
    teamName: 'Job Tracker',
    from: '1.0.0',
    to: plan.package.version,
    plan,
    diff: UpdateDiff,
    connections: Connections,
    ...(kept ? { kept } : {}),
  };
}

export function installedRow(team: string, teamName: string, version: string, id = 'job-tracker'): InstalledSolution {
  return {
    team,
    teamName,
    id,
    name: 'Job Tracker',
    version,
    installedAt: '2026-09-01T10:00:00Z',
    installedBy: 'dana@example.com',
    plugins: ['job-board 0.1.0'],
  };
}

const StepNames = ['plugins', 'team', 'members', 'skills', 'tools', 'sites', 'triggers', 'record'];
const StepTitles = [
  'Install the plugins',
  'Create the team',
  'Hire the members',
  'Register the team skills',
  'Copy the tools',
  'Publish the sites',
  'Create the triggers',
  'Record the package',
];

/** The steps as the Host answers them: the first `done` are done. */
export function steps(done: number): SolutionStep[] {
  return StepNames.map((step, index) => ({ step, number: index + 1, title: StepTitles[index]!, done: index < done }));
}

// --- A Host that records every request ----------------------------------------------------------

export interface Call {
  method: string;
  url: string;
  body: unknown;
}

export function reply(status: number, body?: unknown): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), {
    status,
    headers: { 'content-type': 'application/json' },
  });
}

export type Route = (call: Call) => Response | undefined;

/**
 * A fake Host: each route is tried in turn, and the first answer wins; anything unanswered is a 404
 * naming the request, so a spec that forgot a route sees why. A multipart body is kept as FormData.
 */
export function fakeHost(routes: Route[], calls: Call[]) {
  return (url: string, init?: RequestInit): Promise<Response> => {
    const method = init?.method ?? 'GET';
    const raw = init?.body;
    const body = typeof raw === 'string' ? JSON.parse(raw) : raw;
    const call = { method, url, body };
    calls.push(call);
    for (const route of routes) {
      const answer = route(call);
      if (answer) return Promise.resolve(answer);
    }
    return Promise.resolve(reply(404, { error: `No route for ${method} ${url}.` }));
  };
}

/** The routes an ordinary wizard run needs, each overridable by a route listed before it. */
export function wizardRoutes(options: { installed?: InstalledSolution[]; plan?: SolutionPlan; secrets?: SolutionSecret[] } = {}): Route[] {
  const plan = options.plan ?? hostPlan();
  return [
    (call) => (call.method === 'POST' && call.url === '/api/solutions/check' ? reply(200, okCheck(plan)) : undefined),
    (call) => (call.method === 'GET' && call.url === '/api/solutions/installed' ? reply(200, options.installed ?? []) : undefined),
    (call) => {
      if (call.method !== 'POST' || call.url !== '/api/solutions/preview') return undefined;
      const team = (call.body as { team?: string }).team;
      return reply(200, installPreview(team ?? plan.team.name, null, plan, options.secrets ?? []));
    },
  ];
}

export const sent = (calls: Call[], method: string, url: string) =>
  calls.filter((call) => call.method === method && call.url === url);
