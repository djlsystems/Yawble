import { describe, expect, it } from 'vitest';
import {
  emptyNamedSecretsDraft,
  namedSecretNameRefusal,
  namedSecretValuesBody,
  namedSecretsCount,
  namedSecretsHeld,
  namedSecretsMax,
  type NamedSecretsDraft,
} from '../namedSecrets';

const block = { label: 'App secrets', description: 'Named by you.', max: 2 };
const states = [{ name: 'ApiKey', setBy: 'person@example.com', setAt: '2026-10-23T10:00:00Z' }];
const draft = (over: Partial<NamedSecretsDraft> = {}): NamedSecretsDraft => ({ ...emptyNamedSecretsDraft(), ...over });

describe('named secrets', () => {
  it('sends added and replaced values by name and null for a removed name, and nothing when nothing waits', () => {
    expect(namedSecretValuesBody(draft())).toBeUndefined();
    expect(
      namedSecretValuesBody(
        draft({ added: [{ key: 1, name: ' Token ', value: 'abcd' }, { key: 2, name: '', value: '' }], replaced: { ApiKey: 'new-value' } }),
      ),
    ).toEqual({ Token: 'abcd', ApiKey: 'new-value' });
    expect(namedSecretValuesBody(draft({ replaced: { ApiKey: 'new-value' }, removed: ['ApiKey'] }))).toEqual({ ApiKey: null });
  });

  it('refuses a bad name, a held name in any case, a name twice and a field name, each naming the name', () => {
    const row = (name: string, key = 1) => ({ key, name, value: 'abcd' });
    expect(namedSecretNameRefusal(row('9lives'), states, draft(), [])).toContain('"9lives"');
    expect(namedSecretNameRefusal(row('apikey'), states, draft(), [])).toBe('"apikey" is already a secret of this member.');
    expect(namedSecretNameRefusal(row('apikey'), states, draft({ removed: ['ApiKey'] }), [])).toBeNull();
    expect(namedSecretNameRefusal(row('Token'), [], draft({ added: [row('Token'), row('TOKEN', 2)] }), [])).toBe('"Token" is named twice.');
    expect(namedSecretNameRefusal(row('appSecret1'), [], draft(), ['appSecret1'])).toBe('"appSecret1" is the name of one of this plugin\'s own secrets.');
    expect(namedSecretNameRefusal({ key: 1, name: '', value: '' }, [], draft(), [])).toBeNull();
  });

  it('holds over the max and a row without a value, and counts a removed name out', () => {
    expect(namedSecretsMax({ ...block, max: 0 })).toBe(20);
    const two = draft({ added: [{ key: 1, name: 'One', value: 'abcd' }, { key: 2, name: 'Two', value: 'abcd' }] });
    expect(namedSecretsCount(states, two)).toBe(3);
    expect(namedSecretsHeld(block, states, two, [])).toBe(true);
    expect(namedSecretsHeld(block, states, { ...two, removed: ['ApiKey'] }, [])).toBe(false);
    expect(namedSecretsHeld(block, [], draft({ added: [{ key: 1, name: 'One', value: '' }] }), [])).toBe(true);
    expect(namedSecretsHeld(null, states, two, [])).toBe(false);
  });
});
