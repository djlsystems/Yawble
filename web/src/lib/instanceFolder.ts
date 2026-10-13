/**
 * A FOLDER THE INSTALL ROUTES TAKE is a path inside the instance (`/data/documents/...`), never one
 * on the person's computer. What reads as one - a drive letter (`Z:\` or `Z:/`), a backslash, `~`,
 * or anything not starting with `/` - is refused here, before anything is sent, in one sentence.
 * The Host still checks what it is given; this only says it earlier.
 */
export const COMPUTER_PATH_REFUSAL =
  'That is a path on your computer. Give a folder inside this instance (it starts with /), or use Upload a package (.zip) to send it here.';

export function computerPathRefusal(path: string): string | null {
  const value = path.trim();
  if (value === '') return null;

  const onComputer =
    /^[A-Za-z]:[\\/]/.test(value) || value.includes('\\') || value.startsWith('~') || !value.startsWith('/');

  return onComputer ? COMPUTER_PATH_REFUSAL : null;
}
