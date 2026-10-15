import type { RouteLocationNormalized } from 'vue-router';
import { productTitle } from '../presentation/product';

/**
 * The browser tab's title: the product, then the page's name from its route's `meta.title`. The
 * innermost matched route that names one wins; a route that names none gets the product alone.
 */
export function pageTitle(route: Pick<RouteLocationNormalized, 'matched'>): string {
  const name = [...route.matched].reverse().find((r) => typeof r.meta?.title === 'string')?.meta.title as
    | string
    | undefined;
  return name ? `${productTitle} - ${name}` : productTitle;
}

/**
 * The Console's page name, which its address alone does not say: the Teams table, the Kanban, or
 * the team whose board is showing ("Console" while no team is).
 */
export function consolePageName(view: string, teamName: string | null | undefined): string {
  if (view === 'teams') return 'Teams';
  if (view === 'kanban') return 'Kanban';
  if (view === 'monitor') return 'Monitor';
  return teamName || 'Console';
}

/** The tab title for a Console page name. */
export function titleFor(name: string): string {
  return `${productTitle} - ${name}`;
}
