import { describe, expect, it } from 'vitest';
import { createMemoryHistory, createRouter } from 'vue-router';
import routes from '../routes';
import { consolePageName, pageTitle, titleFor } from '../pageTitle';
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

describe("the Console's page name", () => {
  it('names the Teams table, the Kanban and the team whose board is open', () => {
    expect(consolePageName('teams', 'Alpha')).toBe('Teams');
    expect(consolePageName('kanban', 'Alpha')).toBe('Kanban');
    expect(consolePageName('board', 'Alpha')).toBe('Alpha');
    expect(consolePageName('board', null)).toBe('Console');
    expect(titleFor('Kanban')).toBe(`${productTitle} - Kanban`);
  });
});
