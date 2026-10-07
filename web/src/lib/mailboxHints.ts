/**
 * THE WAY IN TO EACH MAILBOX, AND ITS HINTS: what Add connection's provider tiles say under each
 * name, and the short steps shown beside the form once one is chosen. The servers stay the Host's
 * presets; only the words a person follows at their provider live here.
 *
 * Gmail, iCloud and Yahoo take an app password made at the provider; Outlook and Microsoft 365 take
 * no password at all - Microsoft turned password sign-in off for them - so their tile signs in with a
 * code. Other is the server settings, typed.
 */

export interface HintStep {
  text: string;
  link?: { label: string; href: string };
}

export interface MailboxHint {
  /** The way in, in two or three words, under the tile's name. */
  wayIn: string;
  steps: HintStep[];
  /** For whom this way in does not work, and what to do instead. */
  cannot?: string;
}

export const OutlookHint: MailboxHint = {
  wayIn: 'Sign in with a code',
  steps: [
    { text: 'You sign in on Microsoft\'s own page with a short code shown here. No password is typed here.' },
    { text: 'A work or school account may need your IT department to approve the app before it can sign in.' },
  ],
};

const hints: Record<string, MailboxHint> = {
  gmail: {
    wayIn: 'App password',
    steps: [
      {
        text: 'Turn on 2-Step Verification for the Google account, if it is not on already.',
        link: { label: 'Open 2-Step Verification', href: 'https://myaccount.google.com/signinoptions/twosv' },
      },
      {
        text: 'Make an app password. Any name for it will do.',
        link: { label: 'Open App passwords', href: 'https://myaccount.google.com/apppasswords' },
      },
      { text: 'Paste the 16 characters Google shows into App password below.' },
    ],
    cannot:
      'No App passwords page? A work or school account, or one with Advanced Protection, often cannot make one. '
      + 'Sign in through a Google app of your own instead.',
  },
  icloud: {
    wayIn: 'App-specific password',
    steps: [
      {
        text: 'At account.apple.com, open Sign-In and Security, then App-Specific Passwords, and make one.',
        link: { label: 'Open account.apple.com', href: 'https://account.apple.com' },
      },
      { text: 'Apple shows the password only once: paste it into App password below straight away.' },
      { text: 'The username is often the part of your iCloud address before the @.' },
    ],
  },
  yahoo: {
    wayIn: 'App password',
    steps: [
      {
        text: 'In Yahoo\'s Account security, choose Generate app password, and paste it into App password below.',
        link: { label: 'Open Account security', href: 'https://login.yahoo.com/account/security' },
      },
    ],
  },
  other: {
    wayIn: 'Server settings',
    steps: [
      {
        text: 'Type the IMAP and SMTP server settings your mail provider gives, with your password - or an app '
          + 'password, if the provider offers them.',
      },
    ],
  },
};

/** The hint for a mailbox preset: a preset the Host adds later gets the app password's own words. */
export function mailboxHint(preset: string): MailboxHint {
  return hints[preset] ?? { wayIn: 'App password', steps: [] };
}

/** Roughly how long setting up a provider's own app takes, the one time it is done. */
export const setupTime: Record<string, string> = {
  google: 'about 20 to 30 minutes',
  microsoft: 'about 10 minutes',
};
