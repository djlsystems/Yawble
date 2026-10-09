import type { CatalogNeeds, MarketplacePackage } from '../api/types';

/**
 * THE PUBLISHED CATALOG'S THREE ENTRIES, copied from the package repository's released `catalog.json`
 * (generated 2026-10-09T00:37:43Z) as they are but for the repository address in `download.url` and
 * `source`, here example.test; and each as the Host answers it in `GET /api/marketplace`: its needs
 * fields without their `why`, and its needs in the Host's full sentences. No test reads the network.
 */
export const PublishedCatalog = {
  schema: 1,
  generatedAt: '2026-10-09T00:37:43Z',
  packages: [
    {
      id: 'job-tracker',
      kind: 'solution',
      name: 'Job Tracker',
      version: '1.1.0',
      summary: 'Finds job postings that match your keywords, tracks them on a page, and drafts a cover letter from your resume when you press Apply.',
      description: 'Finds job postings that match your keywords, tracks them on a page, and drafts a cover letter from your resume when you press Apply.',
      needs: {
        connections: [],
        secrets: [
          {
            key: 'ADZUNA_APP_ID',
            why: 'Your Adzuna application id, from developer.adzuna.com. Needed to read Adzuna.',
            when: 'when the sources setting includes adzuna',
          },
          {
            key: 'ADZUNA_APP_KEY',
            why: 'Your Adzuna application key, from developer.adzuna.com. Needed to read Adzuna.',
            when: 'when the sources setting includes adzuna',
          },
          {
            key: 'USAJOBS_API_KEY',
            why: 'Your USAJOBS API key, from developer.usajobs.gov. Needed to read USAJOBS.',
            when: 'when the sources setting includes usajobs',
          },
          {
            key: 'USAJOBS_USER_AGENT',
            why: 'The email address you registered the USAJOBS key with; USAJOBS asks for it on every request.',
            when: 'when the sources setting includes usajobs',
          },
          {
            key: 'THEMUSE_API_KEY',
            why: 'Your The Muse API key, from themuse.com/developers. Needed to read The Muse.',
            when: 'when the sources setting includes themuse',
          },
        ],
        inputs: [
          {
            name: 'Resume',
            kind: 'documents',
            required: true,
            why: 'Your reference resume, .docx or PDF. The Writer starts every letter from it.',
          },
          {
            name: 'sources',
            kind: 'setting',
            required: false,
            why: 'Which job boards the Scout may read. It reads only the boards you tick; adzuna, usajobs and themuse each need their keys set on the Host.',
          },
        ],
        runtimes: [
          'python3',
        ],
      },
      plugins: [
        {
          id: 'job-board',
          version: '0.2.0',
        },
      ],
      platforms: [],
      download: {
        url: 'https://example.test/owner/packages/releases/download/catalog-2026.10.09.1/job-tracker-1.1.0.zip',
        sha256: 'a290c76fe3bc506204d98f29f05ef657e36129633a1368f016260e8eda515394',
        bytes: 12218,
      },
      source: 'https://example.test/owner/packages/tree/main/packages/job-tracker',
    },
    {
      id: 'mail',
      kind: 'solution',
      name: 'Mail',
      version: '2.0.3',
      summary: 'A team that works with your mailbox - an IMAP app password (Gmail, iCloud, Yahoo, other), a Microsoft account or a Google account: it lists, searches and reads your mail, watches your Inbox for new mail, and writes email for you.',
      description: 'A team that works with your mailbox - an IMAP app password (Gmail, iCloud, Yahoo, other), a Microsoft account or a Google account: it lists, searches and reads your mail, watches your Inbox for new mail, and writes email for you. The email it drafts or sends is HTML made from the text by default, or plain text with format: text. By default it only saves drafts for you to send; it sends only when you switch it to send, and only to addresses you allow. It never deletes mail.',
      needs: {
        connections: [
          {
            slot: 'mailbox',
            providers: [
              'imap',
              'microsoft',
              'google',
            ],
            required: true,
            why: 'Your mailbox, connected in Admin, Connections: an app password for Gmail, iCloud, Yahoo or another IMAP mailbox (the simplest), or a Microsoft or Google account. Mailer reads, drafts and sends from it.',
          },
        ],
        secrets: [],
        inputs: [
          {
            name: 'mode',
            kind: 'setting',
            required: false,
            why: 'draft (the default) only saves drafts you send yourself; nothing is ever sent. send sends email, and only to the addresses and domains in sendAllowlist.',
          },
          {
            name: 'sendAllowlist',
            kind: 'setting',
            required: false,
            why: 'The only addresses (person+tag@example.com) or whole domains (@example.com) Mailer may draft to or send to. Every recipient must be on it, in draft mode too. Send mode with an empty list refuses every run.',
          },
          {
            name: 'markRead',
            kind: 'setting',
            required: false,
            why: 'Off (the default): Mailer never changes whether a message is read. On: it may mark messages read or unread when asked.',
          },
          {
            name: 'moveTo',
            kind: 'setting',
            required: false,
            why: 'The only folders and labels Mailer may move mail to or label it with (Archive, Receipts). Empty (the default): it never moves or labels anything. It never deletes.',
          },
        ],
        runtimes: [],
      },
      plugins: [
        {
          id: 'mail',
          version: '2.0.3',
        },
      ],
      platforms: [
        'linux-x64',
        'linux-arm64',
      ],
      download: {
        url: 'https://example.test/owner/packages/releases/download/catalog-2026.10.09.1/mail-2.0.3.zip',
        sha256: '8d3c8d4cebd7af00a2916ab6692899c3fe51e65628a1ab1abf6c5e5ab4fabded',
        bytes: 8097623,
      },
      source: 'https://example.test/owner/packages/tree/main/packages/mail',
    },
    {
      id: 'sample-whoami-go',
      kind: 'plugin',
      name: 'Sample Who Am I (Go)',
      version: '0.1.0',
      summary: "End-to-end check for connections: reads the account and scopes of the Google connection bound to it, from the provider's userinfo.",
      description: "End-to-end check for connections: reads the account and scopes of the Google connection bound to it, from the provider's userinfo.",
      needs: {
        connections: [
          {
            slot: 'account',
            providers: [
              'google',
            ],
            required: true,
            why: 'The Google account to report on.',
          },
        ],
        secrets: [],
        inputs: [
          {
            name: 'userinfoUrl',
            kind: 'setting',
            required: false,
            why: 'Where the access token is sent to read the account. Change it only to point at a test double.',
          },
        ],
        runtimes: [],
      },
      plugins: [
        {
          id: 'sample-whoami-go',
          version: '0.1.0',
        },
      ],
      platforms: [
        'linux-x64',
        'linux-arm64',
      ],
      download: {
        url: 'https://example.test/owner/packages/releases/download/catalog-2026.10.09.1/sample-whoami-go-0.1.0.zip',
        sha256: '5206f97b6bebe7313068ce22615b8c76e11c086592a23a4cbf77de78e4415b2c',
        bytes: 5597305,
      },
      source: 'https://example.test/owner/packages/tree/main/packages/sample-whoami-go',
    },
  ],
} as const;

type Entry = (typeof PublishedCatalog.packages)[number];

/** The catalog's needs as the Host passes them through (`catalogNeeds`): the fields, without `why`. */
function catalogNeeds(entry: Entry): CatalogNeeds {
  return {
    connections: entry.needs.connections.map((c) => ({ slot: c.slot, providers: [...c.providers], required: c.required })),
    secrets: entry.needs.secrets.map((s) => ({ key: s.key, when: s.when ?? null })),
    inputs: entry.needs.inputs.map((i) => ({ name: i.name, kind: i.kind, required: i.required })),
    runtimes: [...entry.needs.runtimes],
  };
}

/** Each entry's needs in the Host's full sentences (`Marketplace.Needs`), as Details shows them. */
export const HostSentences: Record<Entry['id'], string[]> = {
  'job-tracker': [
    'Needs the secret ADZUNA_APP_ID set on the instance, when the sources setting includes adzuna: Your Adzuna application id, from developer.adzuna.com. Needed to read Adzuna.',
    'Needs the secret ADZUNA_APP_KEY set on the instance, when the sources setting includes adzuna: Your Adzuna application key, from developer.adzuna.com. Needed to read Adzuna.',
    'Needs the secret USAJOBS_API_KEY set on the instance, when the sources setting includes usajobs: Your USAJOBS API key, from developer.usajobs.gov. Needed to read USAJOBS.',
    'Needs the secret USAJOBS_USER_AGENT set on the instance, when the sources setting includes usajobs: The email address you registered the USAJOBS key with; USAJOBS asks for it on every request.',
    'Needs the secret THEMUSE_API_KEY set on the instance, when the sources setting includes themuse: Your The Muse API key, from themuse.com/developers. Needed to read The Muse.',
    'Needs documents in Resume at install: Your reference resume, .docx or PDF. The Writer starts every letter from it.',
    'Can take the setting sources at install: Which job boards the Scout may read. It reads only the boards you tick; adzuna, usajobs and themuse each need their keys set on the Host.',
    'Needs python3 on the instance.',
  ],
  mail: [
    'Needs a mailbox account connected (imap, microsoft or google): Your mailbox, connected in Admin, Connections: an app password for Gmail, iCloud, Yahoo or another IMAP mailbox (the simplest), or a Microsoft or Google account. Mailer reads, drafts and sends from it.',
    'Can take the setting mode at install: draft (the default) only saves drafts you send yourself; nothing is ever sent. send sends email, and only to the addresses and domains in sendAllowlist.',
    'Can take the setting sendAllowlist at install: The only addresses (person+tag@example.com) or whole domains (@example.com) Mailer may draft to or send to. Every recipient must be on it, in draft mode too. Send mode with an empty list refuses every run.',
    'Can take the setting markRead at install: Off (the default): Mailer never changes whether a message is read. On: it may mark messages read or unread when asked.',
    'Can take the setting moveTo at install: The only folders and labels Mailer may move mail to or label it with (Archive, Receipts). Empty (the default): it never moves or labels anything. It never deletes.',
  ],
  'sample-whoami-go': [
    'Needs an account connected (google): The Google account to report on.',
    'Can take the setting userinfoUrl at install: Where the access token is sent to read the account. Change it only to point at a test double.',
  ],
};

/** An entry as `GET /api/marketplace` answers it, not installed. */
export function published(id: Entry['id']): MarketplacePackage {
  const entry = PublishedCatalog.packages.find((p) => p.id === id)!;
  return {
    id: entry.id,
    kind: entry.kind,
    name: entry.name,
    summary: entry.summary,
    version: entry.version,
    needs: HostSentences[id],
    catalogNeeds: catalogNeeds(entry),
    installed: false,
    installedVersion: null,
    installedOn: [],
    updateAvailable: false,
  };
}
