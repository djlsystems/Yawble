import type { Connection, MailboxPreset, SolutionPlan, SolutionPlanSetup } from '../api/types';
import { hostPlan } from './solutionFixtures';

/**
 * A PACKAGE WITH A `setup` BLOCK, in the Host's shape (setup-contract.md): Job Tracker with one
 * mailbox question, one document question and the person's preferences - `region` asked in the main
 * flow, `limit` and `sources` behind Advanced…, and `folder` (the mail folder to watch) on the
 * mailbox screen's Advanced…. Shared by the guided wizard and the panel's Change setup specs.
 */
export function hostSetup(): SolutionPlanSetup {
  return {
    welcome: 'Finds jobs that match what you are looking for.\nWrites a cover letter for each one you choose.',
    needs: 'a resume (.docx) and a mailbox',
    connections: [
      {
        member: 'Scout',
        slot: 'mail',
        question: 'Which mailbox should job emails come from?',
        hint: 'Gmail, Outlook or any other mailbox.',
        advanced: false,
        required: true,
        description: 'Where postings are emailed from.',
        plugin: 'job-board',
        providers: ['imap', 'microsoft', 'google'],
        scopes: { microsoft: ['https://graph.microsoft.com/Mail.Read'], google: ['https://mail.google.com/'] },
      },
    ],
    documents: [
      {
        folder: 'Resume',
        question: 'Upload your resume',
        hint: 'A .docx file works best.',
        advanced: false,
        required: true,
        description: 'Your reference resume, .docx or PDF.',
      },
    ],
    settings: [
      {
        member: 'Scout',
        setting: 'region',
        question: 'Where would you like to work?',
        hint: 'A city, a country, or remote.',
        advanced: false,
        required: true,
        description: 'Where to look.',
        default: 'Remote',
        type: 'string',
        choices: null,
        min: null,
        max: null,
        integer: false,
      },
      {
        member: 'Scout',
        setting: 'limit',
        question: 'How many jobs a day at most?',
        hint: null,
        advanced: true,
        required: false,
        description: 'Most postings a scan keeps.',
        default: 10,
        type: 'integer',
        choices: null,
        min: 1,
        max: 100,
        integer: true,
      },
      {
        member: 'Scout',
        setting: 'folder',
        question: null,
        hint: null,
        advanced: true,
        required: false,
        description: 'The mail folder to watch.',
        default: 'INBOX',
        type: 'string',
        choices: null,
        min: null,
        max: null,
        integer: false,
      },
      {
        member: 'Scout',
        setting: 'sources',
        question: null,
        hint: null,
        advanced: true,
        required: false,
        description: 'Which job boards to read.',
        default: [],
        type: 'list',
        choices: ['sample', 'other'],
        min: null,
        max: null,
        integer: false,
      },
    ],
  };
}

/** Job Tracker with its `setup` block, the mailbox slot taking a mailbox, Outlook or Google. */
export function setupPlan(): SolutionPlan {
  const plan = hostPlan();
  return {
    ...plan,
    personConnections: plan.personConnections.map((entry) => ({ ...entry, providers: ['imap', 'microsoft', 'google'] })),
    setup: hostSetup(),
  };
}

export const MailboxPresets: MailboxPreset[] = [
  { id: 'gmail', name: 'Gmail', imap: { host: 'imap.gmail.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.gmail.com', port: 465, security: 'TLS' } },
  { id: 'icloud', name: 'iCloud', imap: { host: 'imap.mail.me.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.mail.me.com', port: 587, security: 'STARTTLS' } },
  { id: 'yahoo', name: 'Yahoo', imap: { host: 'imap.mail.yahoo.com', port: 993, security: 'TLS' }, smtp: { host: 'smtp.mail.yahoo.com', port: 465, security: 'TLS' } },
  { id: 'other', name: 'Other', imap: null, smtp: null },
];

/** A mailbox connection as the Host answers it after a login that worked. */
export function savedMailbox(over: Partial<Connection> = {}): Connection {
  return {
    id: 'conn-mail',
    name: 'dana@example.com',
    provider: 'imap',
    providerKind: 'imap',
    account: 'dana@example.com',
    scopes: [],
    connectedAt: '2026-10-10T09:00:00Z',
    refreshedAt: null,
    status: 'ok',
    statusReason: null,
    usedBy: [],
    ...over,
  } as Connection;
}

/**
 * The words a person never reads in the guided wizard's main flow: the builder's names for things,
 * and a folder path (a slash-separated name, or a name with a trailing slash).
 */
export const BuilderWords = /\b(triggers?|cron|plugins?|tokens?|slots?|members?)\b/i;
export const FolderPath = /(^|\s)\/?[\w.-]+\/[\w./-]*|[\w-]+\/(\s|$)/;
