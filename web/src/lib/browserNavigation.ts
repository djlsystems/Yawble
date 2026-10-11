/**
 * LEAVING THE CONSOLE FOR ANOTHER SITE, in one place: a provider's consent page. The only full-page
 * navigation the web makes, kept in its own module so a mount spec can replace it and assert where
 * the browser would have gone instead of navigating the test's document away.
 */
export function goTo(url: string): void {
  window.location.assign(url);
}

/** The address the person is using now - what a redirect URI is built from. */
export function currentOrigin(): string {
  return window.location.origin;
}
