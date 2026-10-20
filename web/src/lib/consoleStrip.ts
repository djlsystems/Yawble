/**
 * THE CONSOLE STRIP ON A PHONE scrolls sideways, and the active tab is kept in view.
 *
 * The strip's own scroll offset that shows a tab whole, moving as little as possible: unchanged
 * when the tab is already in view, else just far enough to bring its nearer edge in. Written on the
 * strip's `scrollLeft` rather than through `scrollIntoView`, which would scroll the page as well.
 *
 * Every figure is in the strip's own coordinates: `tabLeft` from the strip's content start.
 */
export function stripScrollToShow(scrollLeft: number, stripWidth: number, tabLeft: number, tabWidth: number): number {
  if (stripWidth <= 0) return scrollLeft;
  if (tabLeft < scrollLeft) return Math.max(0, tabLeft);
  if (tabLeft + tabWidth > scrollLeft + stripWidth) return Math.max(0, Math.min(tabLeft, tabLeft + tabWidth - stripWidth));

  return scrollLeft;
}
