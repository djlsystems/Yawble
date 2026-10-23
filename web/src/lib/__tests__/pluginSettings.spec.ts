import { describe, expect, it } from 'vitest';
import type { PluginSecretState } from '../../api/types';
import {
  defaultLabel,
  defaultValue,
  fieldLabel,
  fieldWords,
  firstParagraph,
  initialConfig,
  initialSecrets,
  isDefault,
  missingRequired,
  placeholderOf,
  saveBody,
  secretsSet,
  setByPerson,
  settingsBody,
  type PluginSettingsShape,
} from '../pluginSettings';
import { hostField, hostPlugin, hostSecret } from '../../test/pluginFixtures';

/** A manifest with one field of each type, in the Host's shape. */
const shape: PluginSettingsShape = hostPlugin({
  id: 'sample-echo',
  config: {
    greeting: hostField({ type: 'string', description: 'What it says first.', default: 'hello' }),
    mode: hostField({ type: 'string', default: 'upper', enum: ['upper', 'reverse'], setBy: 'person' }),
    repeat: hostField({ type: 'number', default: 1 }),
    limit: hostField({ type: 'number', required: true }),
    loud: hostField({ type: 'bool', default: false }),
    labels: hostField({ type: 'list' }),
    recipients: hostField({ type: 'list', required: true, enum: ['ops', 'dev'] }),
  },
  secrets: {
    token: hostSecret({ required: true }),
    signing: hostSecret(),
  },
});

describe('defaultValue and defaultLabel', () => {
  it("read each type's default in the form's terms: an empty box for text and numbers, whose placeholder it is", () => {
    expect(defaultValue(shape.config.greeting!)).toBe('');
    expect(placeholderOf(shape.config.greeting!)).toBe('hello');
    expect(defaultValue(shape.config.repeat!)).toBe('');
    expect(placeholderOf(shape.config.repeat!)).toBe('1');
    expect(defaultValue(shape.config.mode!)).toBe('upper');
    expect(placeholderOf(shape.config.mode!)).toBeUndefined();
    expect(defaultValue(shape.config.limit!)).toBe('');
    expect(placeholderOf(shape.config.limit!)).toBeUndefined();
    expect(defaultValue(shape.config.loud!)).toBe(false);
    expect(defaultValue(shape.config.labels!)).toEqual([]);
  });

  it('say the default, or nothing when the manifest gives none', () => {
    expect(defaultLabel(shape.config.greeting!)).toBe('Default: hello');
    expect(defaultLabel(shape.config.limit!)).toBeNull();
    expect(defaultLabel(shape.config.loud!)).toBe('Default: off');
    expect(defaultLabel(shape.config.labels!)).toBe('Default: none');
  });
});

describe('setByPerson', () => {
  it("is true only for the Host's `person`", () => {
    expect(setByPerson(shape.config.mode!)).toBe(true);
    expect(setByPerson(shape.config.greeting!)).toBe(false);
  });
});

describe('initialConfig and initialSecrets', () => {
  it('start from what is stored, else the default', () => {
    expect(initialConfig(shape, { repeat: 3, recipients: ['ops'], loud: true })).toEqual({
      greeting: '',
      mode: 'upper',
      repeat: '3',
      limit: '',
      loud: true,
      labels: [],
      recipients: ['ops'],
    });
  });

  it('hold each secret as its bound logical key, or empty', () => {
    expect(initialSecrets(shape, { token: 'ECHO_TOKEN' })).toEqual({ token: 'ECHO_TOKEN', signing: '' });
  });
});

describe('isDefault', () => {
  it('compares a number by value and a list item by item, and takes an empty box as the default', () => {
    expect(isDefault(shape.config.repeat!, '')).toBe(true);
    expect(isDefault(shape.config.greeting!, '')).toBe(true);
    expect(isDefault(shape.config.greeting!, 'hello')).toBe(true);
    expect(isDefault(shape.config.repeat!, '1.0')).toBe(true);
    expect(isDefault(shape.config.repeat!, '2')).toBe(false);
    expect(isDefault(shape.config.labels!, [])).toBe(true);
    expect(isDefault(shape.config.labels!, ['a'])).toBe(false);
  });
});

const state = (over: Partial<PluginSecretState> & Pick<PluginSecretState, 'field'>): PluginSecretState => ({
  label: null,
  masked: true,
  required: false,
  needed: true,
  set: false,
  setBy: null,
  setAt: null,
  source: null,
  key: null,
  ...over,
});

describe('missingRequired and secretsSet', () => {
  it('names a required text or number left empty with no default, and a required secret with no value', () => {
    const config = initialConfig(shape);
    expect(missingRequired(shape, config, secretsSet(shape, undefined, {}))).toEqual(['limit', 'token']);
  });

  it('takes a typed value, or one set now and not cleared, as a secret that will be set', () => {
    expect(secretsSet(shape, undefined, { token: 'abc' })).toEqual({ token: true, signing: false });
    const set = [state({ field: 'token', set: true, source: 'member' })];
    expect(secretsSet(shape, set, {})).toEqual({ token: true, signing: false });
    expect(secretsSet(shape, set, { token: null })).toEqual({ token: false, signing: false });
    expect(secretsSet(shape, [state({ field: 'token', set: true, source: 'member', key: 'K' })], { token: null }).token).toBe(true);
    expect(secretsSet(shape, [state({ field: 'token', needed: false })], {}).token).toBe(true);
  });

  it('never holds back a required list left empty: the Host defaults a list to []', () => {
    const config = { ...initialConfig(shape), limit: '5', recipients: [] };
    expect(missingRequired(shape, config, { token: true })).toEqual([]);
  });
});

describe('fieldLabel and fieldWords', () => {
  it("say a field's name in words, short forms in capitals, unless the manifest labels it", () => {
    expect(fieldWords('apiKey')).toBe('API key');
    expect(fieldWords('clientIp')).toBe('Client IP');
    expect(fieldWords('apiUser')).toBe('API user');
    expect(fieldWords('daily_send_cap')).toBe('Daily send cap');
    expect(fieldWords('webhookURL')).toBe('Webhook URL');
    expect(fieldWords('mode')).toBe('Mode');
    expect(fieldLabel('apiKey', { label: 'Account key' })).toBe('Account key');
    expect(fieldLabel('apiKey', { label: null })).toBe('API key');
  });
});

describe('firstParagraph', () => {
  it('is the description up to its first blank line', () => {
    expect(firstParagraph('One line.\nStill one.\n\nSecond paragraph.')).toBe('One line.\nStill one.');
    expect(firstParagraph('  Only one.  ')).toBe('Only one.');
  });
});

describe('saveBody', () => {
  it('sends no key bindings, so the Host keeps them, and the values typed or cleared', () => {
    const config = { ...initialConfig(shape), limit: '5' };
    expect(saveBody(shape, config, { token: 'abc', signing: null })).toEqual({
      config: { limit: 5 },
      secretValues: { token: 'abc', signing: null },
    });
    expect(saveBody(shape, config, { token: '' })).toEqual({ config: { limit: 5 } });
  });
});

describe('settingsBody', () => {
  it('sends only what differs from its default, a number as a number, and each bound key', () => {
    const config = {
      ...initialConfig(shape),
      greeting: 'hi',
      repeat: '1',
      limit: '5',
      loud: true,
      recipients: ['ops'],
    };

    expect(settingsBody(shape, config, { token: ' ECHO_TOKEN ', signing: '' })).toEqual({
      config: { greeting: 'hi', limit: 5, loud: true, recipients: ['ops'] },
      secrets: { token: 'ECHO_TOKEN' },
    });
  });

  it('leaves a required list left empty out, for the Host to default to []', () => {
    const config = { ...initialConfig(shape), limit: '5', recipients: [] };
    expect(settingsBody(shape, config, { token: 'K' }).config).toEqual({ limit: 5 });
  });

  it('sends a number that does not parse as typed, so the refusal names it', () => {
    const config = { ...initialConfig(shape), limit: 'lots' };
    expect(settingsBody(shape, config, {}).config).toEqual({ limit: 'lots' });
  });
});
