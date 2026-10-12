/**
 * THE MARKETPLACE'S FIRST-OPEN REFRESH: the first time Browse reads a catalog the Host has checked in
 * this page load, it asks the Host to read the catalog again (`POST /api/marketplace/refresh`), so a
 * package published minutes ago shows without a Refresh. Later opens in the same page load do not;
 * the Refresh control still does. The flag lives for the page load - this module - and no longer.
 */
let refreshed = false;

/** True once per page load: the caller is the first open, and should refresh. */
export function claimFirstOpenRefresh(): boolean {
  if (refreshed) return false;
  refreshed = true;
  return true;
}

/** For tests: forget the first open (`done` false), or say it has happened (`done` true). */
export function resetFirstOpenRefresh(done = false): void {
  refreshed = done;
}
