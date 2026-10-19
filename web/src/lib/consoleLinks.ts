/**
 * EVERY NAME ON SCREEN IS A WAY IN, and these are the addresses the names link to: a team's board,
 * a member's card on its board, a card on the Kanban. Hash links over the routes there already are
 * (`/console` and its query), never a new route, and built from what the name already holds -
 * nothing is fetched to render one.
 *
 * `member` and `card` are the highlight: the view the address names opens, scrolls that card into
 * view and flashes it once (`stores/highlight`), and the key is dropped from the address once it is
 * consumed (`useConsoleHistory`), so Back and a reload after it do not flash it again.
 */

/** The address of a team's board. */
export function boardHref(team: string): string {
  return `#/console?${new URLSearchParams({ team }).toString()}`
}

/** The address of a member's card on its team's board. `member` is the member's id. */
export function memberHref(team: string, member: string): string {
  return `#/console?${new URLSearchParams({ team, member }).toString()}`
}

/** The address of a card on the Kanban. */
export function cardHref(card: string): string {
  return `#/console?${new URLSearchParams({ view: 'kanban', card }).toString()}`
}

/**
 * Follows one of the addresses above from a control that cannot be an `<a>` (a span inside a card
 * that is itself a button): the same hash change a click on the link makes. In its own module so a
 * mount spec can replace it and assert where a click went.
 */
export function follow(href: string): void {
  globalThis.location.hash = href.replace(/^#/, '')
}
