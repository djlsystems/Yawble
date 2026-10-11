/**
 * WHETHER A TYPED PASSWORD LOOKS LIKE THE PROVIDER'S APP PASSWORD, for the mailbox form's warning.
 *
 * A WARNING, NEVER A REFUSAL: a provider may change its format, so Save stays allowed and the Host's
 * login test decides. The check runs here, in the browser, on the field's value alone: it is never
 * logged, echoed into the words, or sent anywhere.
 *
 * ONLY A SHAPE THE PROVIDER PUBLISHES, with spaces and hyphens ignored (Google shows its in groups of
 * four):
 * - Gmail: 16 characters - https://support.google.com/accounts/answer/185833: "An app password is a
 *   16-digit passcode that gives a less secure app or device permission to access your Google
 *   Account." The page says "digit" while the passwords Google shows are letters, so only the length
 *   is checked, not which characters.
 * - iCloud: none. Apple's https://support.apple.com/en-us/102654 says how to make an app-specific
 *   password but not what it looks like, so iCloud never warns.
 * - Yahoo: none. https://help.yahoo.com/kb/SLN15241.html says how to make an app password but not
 *   what it looks like, so Yahoo never warns.
 * - Other: never; a custom server's passwords have no known shape.
 */

interface AppPasswordShape {
  /** The provider's name in the warning. */
  provider: string;
  /** The Host's own hint for a refused login at this provider, word for word. */
  hint: string;
  /** Whether the password, spaces and hyphens removed, has the published shape. */
  fits: (bare: string) => boolean;
}

const shapes: Record<string, AppPasswordShape> = {
  gmail: {
    provider: 'Gmail',
    hint: 'For Gmail, make an app password: it needs 2-Step Verification.',
    fits: (bare) => bare.length === 16,
  },
};

/** The warning for `password` typed at mailbox preset `preset`, or null when there is nothing to say. */
export function appPasswordWarning(preset: string | null | undefined, password: string): string | null {
  const shape = preset ? shapes[preset] : undefined;
  if (!shape || password === '') return null;
  if (shape.fits(password.replace(/[\s-]/g, ''))) return null;

  return `This looks like an ordinary account password: ${shape.provider} wants an app password here. ${shape.hint} `
    + 'It can still be saved: the login test decides.';
}
