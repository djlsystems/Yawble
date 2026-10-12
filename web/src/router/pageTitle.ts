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
