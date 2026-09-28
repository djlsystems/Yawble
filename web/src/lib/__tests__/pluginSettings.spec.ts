import { describe, expect, it } from 'vitest';
import {
  addToList,
  defaultLabel,
  defaultValue,
  initialConfig,
  initialSecrets,
  isDefault,
  missingRequired,
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
  it("read each type's default in the form's terms", () => {
    expect(defaultValue(shape.config.greeting!)).toBe('hello');
    expect(defaultValue(shape.config.repeat!)).toBe('1');
    expect(defaultValue(shape.config.limit!)).toBe('');
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
      greeting: 'hello',
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
  it('compares a number by value and a list item by item', () => {
    expect(isDefault(shape.config.repeat!, '1.0')).toBe(true);
    expect(isDefault(shape.config.repeat!, '2')).toBe(false);
    expect(isDefault(shape.config.labels!, [])).toBe(true);
    expect(isDefault(shape.config.labels!, ['a'])).toBe(false);
  });
});

describe('missingRequired', () => {
  it('names a required text or number left empty and a required secret with no key', () => {
    const config = initialConfig(shape);
    expect(missingRequired(shape, config, { token: '', signing: '' })).toEqual(['limit', 'token']);
  });

  it('never holds back a required list left empty: the Host defaults a list to []', () => {
    const config = { ...initialConfig(shape), limit: '5', recipients: [] };
    expect(missingRequired(shape, config, { token: 'ECHO_TOKEN' })).toEqual([]);
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

describe('addToList', () => {
  it('adds a value, and refuses a blank, a repeat or one outside the enum', () => {
    const recipients = shape.config.recipients!;
    expect(addToList(recipients, ['ops'], ' dev ')).toEqual({ list: ['ops', 'dev'] });
    expect(addToList(recipients, ['ops'], ' ')).toEqual({ problem: 'Type a value first.' });
    expect(addToList(recipients, ['ops'], 'ops')).toEqual({ problem: 'ops is already in the list.' });
    expect(addToList(recipients, [], 'qa')).toEqual({ problem: 'qa is not allowed. Choose from: ops, dev.' });
  });
});
