import { describe, expect, it } from 'vitest';
import { doorFor } from '../door';

describe('the landing page door', () => {
  it('offers the console to someone signed in', () => {
    expect(doorFor({ user: { id: '1' }, needsFirstAccount: false })).toBe('console');
  });

  it('offers account creation on an instance with no account', () => {
    expect(doorFor({ user: null, needsFirstAccount: true })).toBe('create');
  });

  it('offers a log in once an account exists', () => {
    expect(doorFor({ user: null, needsFirstAccount: false })).toBe('login');
  });

  // The first account is created once and registration then closes permanently, so a signed-in
  // user on an instance that still reports needsFirstAccount is a contradiction the server should
  // never produce. If it ever does, showing "Create the first account" to someone already inside
  // is the worse failure: their next attempt 409s and the page offers nothing that works.
  it('prefers the console when the two signals disagree', () => {
    expect(doorFor({ user: { id: '1' }, needsFirstAccount: true })).toBe('console');
  });
});
