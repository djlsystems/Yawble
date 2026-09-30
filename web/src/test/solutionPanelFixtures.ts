import type { InstalledSolution, PluginMemberSettings, SolutionPanel } from '../api/types';
import { reply, type Route } from './solutionFixtures';

/**
 * THE LAUNCHER AND CONTROL PANEL IN THE HOST'S SHAPE, key for key as the B001J contract writes them
 * (`GET /api/solutions/installed` extended, `GET /api/teams/{team}/solution/panel`), modelled on the
 * Job Tracker sample. Shared so every panel spec reads the same shape the Host sends.
 */

/** A status value that looks like markup: it must read as these characters, never become an element. */
export const HtmlLooking = '<img src=x onerror="alert(1)"> <b>3</b> new jobs';

export function launcherRow(over: Partial<InstalledSolution> = {}): InstalledSolution {
  return {
    team: 'job-tracker',
    teamName: 'Job Tracker',
    id: 'job-tracker',
    name: 'Job Tracker',
    version: '1.1.0',
    installedAt: '2026-09-29T10:00:00Z',
    updatedAt: null,
    installedBy: 'dana@example.com',
    plugins: ['job-board'],
    folder: '/data/documents/packages/job-tracker',
    primarySite: { name: 'tracker', url: '/sites/job-tracker/tracker/', published: true },
    status: '3 new jobs · last checked 2026-09-30T08:00:00Z',
    state: { kind: 'idle', reason: null },
    paused: false,
    ...over,
  };
}

export function panelRead(over: Partial<SolutionPanel> = {}): SolutionPanel {
  return {
    team: 'job-tracker',
    teamName: 'Job Tracker',
    id: 'job-tracker',
    name: 'Job Tracker',
    version: '1.1.0',
    description: 'Finds job postings, tracks them on a page, and drafts a cover letter when you press Apply.',
    installedAt: '2026-09-29T10:00:00Z',
    updatedAt: null,
    installedBy: 'dana@example.com',
    folder: '/data/documents/packages/job-tracker',
    paused: false,
    state: { kind: 'blocked', reason: 'Upload a file to Resume/' },
    status: '3 new jobs · last checked 2026-09-30T08:00:00Z',
    primarySite: { name: 'tracker', url: '/sites/job-tracker/tracker/', published: true },
    members: [
      { packageName: 'Manager', member: 'Manager', kind: 'agent', role: 'manager', state: 'idle', lastRun: null },
      {
        packageName: 'Scout',
        member: 'scout',
        kind: 'plugin',
        role: 'member',
        state: 'idle',
        lastRun: { seq: 41, at: '2026-09-30T08:00:00Z', outcome: 'completed' },
      },
    ],
    triggers: [
      {
        id: 'trg_scan',
        name: 'Scan for postings',
        kind: 'schedule',
        member: 'scout',
        enabled: true,
        nextFireAt: '2026-09-30T12:00:00Z',
        runNow: true,
        dailyTokenCap: 200000,
        spentToday: { tokens: 12345, measuredRuns: 3, unmeasuredRuns: 1 },
        capped: false,
      },
      {
        id: 'trg_apply',
        name: 'Apply pressed',
        kind: 'event',
        member: 'Manager',
        enabled: true,
        nextFireAt: null,
        runNow: false,
        dailyTokenCap: null,
        spentToday: { tokens: 0, measuredRuns: 0, unmeasuredRuns: 0 },
        capped: false,
      },
    ],
    blocked: [
      {
        kind: 'document',
        name: 'Resume/',
        member: null,
        description: 'Your reference resume, .docx or PDF.',
        fix: { upload: { folder: 'Resume' } },
      },
      {
        kind: 'connection',
        name: 'mailbox',
        member: 'scout',
        packageMember: 'Scout',
        description: 'Where postings are emailed from.',
        fix: { connection: { member: 'scout', slot: 'mailbox' } },
      },
    ],
    settings: [{ member: 'scout', packageMember: 'Scout', setting: 'keywords', personOnly: false }],
    outputs: [
      {
        folder: 'Applications',
        exists: true,
        files: [
          {
            path: 'Applications/acme.md',
            name: 'acme.md',
            size: 2048,
            modifiedAt: '2026-09-30T09:00:00Z',
            download: '/api/teams/job-tracker/documents/content?path=Applications%2Facme.md',
          },
          {
            path: 'Applications/older.md',
            name: 'older.md',
            size: 100,
            modifiedAt: '2026-09-29T09:00:00Z',
            download: '/api/teams/job-tracker/documents/content?path=Applications%2Folder.md',
          },
        ],
        more: false,
      },
      { folder: 'Drafts', exists: false, files: [], more: false },
    ],
    recentRuns: [
      {
        member: 'scout',
        packageMember: 'Scout',
        seq: 41,
        startedAt: '2026-09-30T07:59:00Z',
        endedAt: '2026-09-30T08:00:00Z',
        outcome: 'completed',
        output: '3 new postings <script>x</script>',
        reason: null,
        quiet: false,
      },
    ],
    ...over,
  };
}

/** Scout's stored settings: `keywords` (listed by the package), `region` (person-only, not listed), a mailbox slot. */
export function scoutSettings(over: Partial<PluginMemberSettings> = {}): PluginMemberSettings {
  return {
    team: 'job-tracker',
    member: 'scout',
    plugin: 'job-board',
    version: '1.0.0',
    config: { keywords: ['dotnet'], region: 'EU' },
    secrets: { apiKey: 'JOB_BOARD_KEY' },
    fields: {
      keywords: { type: 'list', description: 'What to search for.', required: false, default: [], enum: null, setBy: 'anyone' },
      region: { type: 'string', description: 'Where to search.', required: false, default: null, enum: null, setBy: 'person' },
    },
    secretFields: { apiKey: { description: 'The job board key.', required: false } },
    connections: {},
    connectionFields: {
      mailbox: {
        description: 'Where postings are emailed from.',
        providers: ['google'],
        scopes: { google: ['mail.read'] },
        required: true,
        summary: 'needs a Google connection',
      },
    },
    ...over,
  };
}

export const MailConnection = {
  id: 'conn_1',
  name: 'Dana mail',
  provider: 'google',
  providerKind: 'google',
  account: 'dana@example.com',
  scopes: ['mail.read'],
  connectedAt: '2026-09-01T00:00:00Z',
  refreshedAt: null,
  status: 'ok',
  statusReason: null,
  usedBy: [],
};

/** The routes an ordinary panel read needs; a route listed before these overrides. */
export function panelRoutes(read: () => SolutionPanel = () => panelRead()): Route[] {
  return [
    (call) => (call.method === 'GET' && call.url === '/api/teams/job-tracker/solution/panel' ? reply(200, read()) : undefined),
    (call) =>
      call.method === 'GET' && call.url === '/api/teams/job-tracker/members/scout/plugin-settings'
        ? reply(200, scoutSettings())
        : undefined,
    (call) => (call.method === 'GET' && call.url === '/api/connections' ? reply(200, [MailConnection]) : undefined),
    (call) =>
      call.method === 'GET' && call.url === '/api/connections/providers'
        ? reply(200, [{ id: 'google', kind: 'google', name: 'Google', clientId: 'x', clientSecretSet: true, configured: true }])
        : undefined,
  ];
}

/** Opens a section of a mounted panel by its tab, and lets it render. */
export async function openSection(section: 'status' | 'controls' | 'results' | 'maintenance'): Promise<void> {
  document.body.querySelector<HTMLElement>(`[data-section-tab="${section}"]`)!.click();
  await new Promise((resolve) => setTimeout(resolve, 0));
  const { flushPromises } = await import('@vue/test-utils');
  await flushPromises();
}
