import { copyToClipboard } from 'quasar';

/**
 * Puts a value on the system clipboard: a redirect URI, a scope or an API to paste at a provider.
 * In its own module so a mount spec can replace it and assert what was copied.
 */
export function copyText(text: string): Promise<void> {
  return copyToClipboard(text);
}
