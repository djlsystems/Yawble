import { describe, expect, it } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';
import routes from '../routes';
import { pageTitle } from '../pageTitle';
import { productTitle } from '../../presentation/product';

describe('the page title', () => {
  const router = createRouter({ history: createMemoryHistory(), routes });

  it.each([
    ['/', 'Sign in'],
    ['/login', 'Sign in'],
    ['/console', 'Console'],
    ['/solutions', 'Marketplace'],
    ['/solutions/some-team', 'Marketplace'],
    ['/solutions/install', 'Install a solution'],
    ['/no/such/page', 'Page not found'],
  ])('%s reads the product then its name', (path, name) => {
    expect(pageTitle(router.resolve(path))).toBe(`${productTitle} - ${name}`);
  });

  it('a route that names no page is the product alone', () => {
    expect(pageTitle({ matched: [{ meta: {} }] } as never)).toBe(productTitle);
  });
});
