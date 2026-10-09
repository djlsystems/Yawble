import { beforeEach, describe, expect, it } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { RouteLocationNormalized } from 'vue-router';
import { useSessionStore } from '../../stores/session';
import { sessionGuard } from '../guard';

/** A navigation to the public front door, with the query a sign-in redirect may carry. */
function door(path: '/' | '/login', query: Record<string, string> = {}): RouteLocationNormalized {
  return {
    path,
    fullPath: path,
    query,
    matched: [{ meta: { publicRoute: true } }],
  } as unknown as RouteLocationNormalized;
}

describe('the front door when the session is already known', () => {
  beforeEach(() => setActivePinia(createPinia()));

  it('sends a signed-in person on from / before the sign-in form can draw', async () => {
    const session = useSessionStore();
    session.$patch({ checked: true, user: { id: 'u1', email: 'person@example.com' } as never });

    expect(await sessionGuard(door('/'))).toBe('/console');
    expect(await sessionGuard(door('/', { next: '/console?view=kanban' }))).toBe('/console?view=kanban');
    expect(await sessionGuard(door('/login'))).toBe('/console');
  });

  it('lets a signed-out visitor see the front door', async () => {
    const session = useSessionStore();
    session.$patch({ checked: true, user: null });

    expect(await sessionGuard(door('/'))).toBe(true);
    expect(await sessionGuard(door('/login'))).toBe(true);
  });
});
