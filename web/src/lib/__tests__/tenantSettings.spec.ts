import { describe, expect, it } from 'vitest';
import {
  TenantSettingFields,
  WipMaxRunning,
  catalogHealth,
  changedSettings,
  draftErrors,
  durationSeconds,
  laneLimitsOf,
  normaliseTenantSettings,
  rejectedField,
  settingErrorText,
  sourceLine,
  validateDraft,
  timeSpanOf,
  validateLaneLimit,
  draftOf,
  packageNames,
  wireValue,
  tagMapOf,
  validateTags,
} from '../tenantSettings';
import type { TenantSetting } from '../../api/types';

const setting = (name: string, value: unknown, extra: Partial<TenantSetting> = {}): TenantSetting => ({
  name,
  value,
  default: value,
  source: 'appsettings',
  updatedAt: null,
  updatedBy: null,
  description: '',
  ...extra,
});

describe('the Admission field', () => {
  it('carries the decided wording, and the key is not its label', () => {
    const field = TenantSettingFields.find((entry) => entry.name === WipMaxRunning)!;

    expect(field.label).toBe('Agents running at once');
    expect(field.hint).toBe(
      'Across all teams, Managers included. Work over this number waits its turn; nothing is refused. 0 means no limit.',
    );
  });

  it('names every setting, system packages included, and no label or hint shows a key', () => {
    const names = TenantSettingFields.map((field) => field.name).sort();
    expect(names).toEqual([
      'causation.depthLimit',
      'concierge.idleTimeout',
      'kanban.wipLimits',
      'quiet.window',
      'resume.maxAutomatic',
      'system.packages',
      'theme.default',
      'wip.maxRunning',
      'workflow.spendLimit',
    ]);

    for (const field of TenantSettingFields) {
      for (const name of names) {
        expect(field.label).not.toContain(name);
        expect(field.hint).not.toContain(name);
      }
    }
  });
});

describe('normaliseTenantSettings', () => {
  it('reads a map keyed by name, and the roots', () => {
    const read = normaliseTenantSettings({
      settings: { 'wip.maxRunning': { value: 4, default: 8, source: 'row', updatedAt: 'T', updatedBy: 'a@b', description: 'd' } },
      roots: ['/data'],
    });

    expect(read.settings).toEqual([
      { name: 'wip.maxRunning', value: 4, default: 8, source: 'row', updatedAt: 'T', updatedBy: 'a@b', description: 'd' },
    ]);
    expect(read.roots).toEqual([{ name: '', path: '/data', note: '' }]);
  });

  it('reads the list the server sends, splitting the read-only roots out of the settings', () => {
    const read = normaliseTenantSettings({
      settings: [
        { name: 'quiet.window', value: '00:30:00', default: '00:30:00', source: 'appsettings', updatedAt: null, updatedBy: null, readOnly: false },
        { name: 'fileBrowser.roots.Projects', value: '/mnt/projects', source: 'appsettings', readOnly: true, note: 'mount it too' },
      ],
    });

    expect(read.settings.map((entry) => entry.name)).toEqual(['quiet.window']);
    expect(read.settings[0]!.updatedBy).toBeNull();
    expect(read.roots).toEqual([{ name: 'Projects', path: '/mnt/projects', note: 'mount it too' }]);
  });
});

describe('sourceLine', () => {
  it('names the person and the time for a saved row', () => {
    expect(sourceLine(setting('x', 1, { source: 'row', updatedBy: 'a@b.com', updatedAt: 'NOW' }), () => '23 Sep'))
      .toBe('Set by a@b.com, 23 Sep');
  });

  it('names the file and the default for an appsettings value', () => {
    expect(sourceLine(setting('x', 4, { default: 4 }))).toBe('From the host configuration (default 4)');
  });
});

describe('validation', () => {
  it('takes whole numbers of 0 or more for a count', () => {
    expect(validateDraft('count', '0')).toBeNull();
    expect(validateDraft('count', '12')).toBeNull();
    expect(validateDraft('count', '-1')).not.toBeNull();
    expect(validateDraft('count', '2.5')).not.toBeNull();
    expect(validateDraft('count', '')).not.toBeNull();
  });

  it('reads durations in both spellings and bounds them', () => {
    expect(durationSeconds('8h')).toBe(28800);
    expect(durationSeconds('1h30m')).toBe(5400);
    expect(durationSeconds('08:00:00')).toBe(28800);
    expect(durationSeconds('1.00:00:00')).toBe(86400);
    expect(durationSeconds('soon')).toBeNull();
    expect(validateDraft('duration', '30s')).not.toBeNull();
    expect(validateDraft('duration', '31d')).not.toBeNull();
    expect(validateDraft('duration', '30.00:00:00')).toBeNull();
    expect(validateDraft('duration', '30m')).toBeNull();
  });

  it('holds a count to its server bound', () => {
    expect(validateDraft('count', '1000', 1000)).toBeNull();
    expect(validateDraft('count', '1001', 1000)).toBe('At most 1000.');
  });

  it('spells a duration as the server does', () => {
    expect(timeSpanOf(45 * 60)).toBe('00:45:00');
    expect(timeSpanOf(8 * 3600)).toBe('08:00:00');
    expect(timeSpanOf(2 * 86400 + 90)).toBe('2.00:01:30');
  });

  it('takes an empty lane box as no limit and refuses zero', () => {
    expect(validateLaneLimit('')).toBeNull();
    expect(validateLaneLimit('3')).toBeNull();
    expect(validateLaneLimit('0')).not.toBeNull();
  });

  it('reports only fields the server has', () => {
    expect(draftErrors([setting('wip.maxRunning', 4)], { 'wip.maxRunning': 'x' }, {})).toEqual({
      'wip.maxRunning': 'A whole number, 0 or more.',
    });
  });
});

describe('changedSettings', () => {
  const all = [setting('wip.maxRunning', 4), setting('quiet.window', '00:30:00'), setting('kanban.wipLimits', { review: 3 })];

  it('is empty while nothing moved', () => {
    expect(changedSettings(all, { 'wip.maxRunning': '4', 'quiet.window': '30m' }, { review: '3', todo: '' })).toEqual({});
  });

  it('sends counts as numbers and only what moved', () => {
    expect(changedSettings(all, { 'wip.maxRunning': '2', 'quiet.window': '30m' }, { review: '3' })).toEqual({
      'wip.maxRunning': 2,
    });
  });

  it('sends the whole lane map when one lane moved, without any running lane', () => {
    expect(changedSettings(all, { 'wip.maxRunning': '4', 'quiet.window': '30m' }, { review: '', todo: '5', 'in-progress': '9', doing: '2' }))
      .toEqual({ 'kanban.wipLimits': { todo: 5 } });
  });

  it('sends a duration as hh:mm:ss and reads the same length of time as unchanged', () => {
    expect(changedSettings(all, { 'wip.maxRunning': '4', 'quiet.window': '0.00:30:00' }, { review: '3' })).toEqual({});
    expect(changedSettings(all, { 'wip.maxRunning': '4', 'quiet.window': '1h' }, { review: '3' })).toEqual({
      'quiet.window': '01:00:00',
    });
  });

  it('reads a stored lane map as an object or a JSON string', () => {
    expect(laneLimitsOf({ a: 2, b: 0 })).toEqual({ a: 2 });
    expect(laneLimitsOf('{"a":2}')).toEqual({ a: 2 });
    expect(laneLimitsOf('nope')).toEqual({});
  });
});

describe('rejectedField', () => {
  it('prefers the field the body names, then a setting named in the message', () => {
    expect(rejectedField('quiet.window', 'x')).toBe('quiet.window');
    expect(rejectedField(null, 'causation.depthLimit must be at least 0')).toBe('causation.depthLimit');
    expect(rejectedField(null, 'something else')).toBeNull();
  });
});

describe('settingErrorText', () => {
  it('puts the field’s label where the message names its key', () => {
    expect(settingErrorText('quiet.window', 'quiet.window must be between 00:01:00 and 30.00:00:00.')).toBe(
      'Quiet team window must be between 00:01:00 and 30.00:00:00.',
    );
  });

  it('replaces every known key, quoted or not, and leaves a message with none alone', () => {
    expect(settingErrorText('theme.default', "'theme.default' and causation.depthLimit")).toBe(
      'Default theme and Longest chain of instructions',
    );
    expect(settingErrorText('quiet.window', 'Too long: at most 7 days.')).toBe('Too long: at most 7 days.');
  });

  it('never shows a key it has no label for', () => {
    expect(settingErrorText('mystery.key', "'mystery.key' is not a setting that can be changed here.")).toBe(
      'This setting is not a setting that can be changed here.',
    );
  });
});

describe('catalogHealth', () => {
  it('says not measured rather than healthy when a probe has not answered', () => {
    const lines = catalogHealth({ notInstalled: null, auth: null });

    expect(lines.every((line) => line.ok === null)).toBe(true);
  });

  it('names the missing and signed-out Agents', () => {
    const lines = catalogHealth({
      notInstalled: ['codex'],
      auth: [{ agent: 'claude', installed: true, authenticated: false }],
    });

    expect(lines.map((line) => [line.label, line.ok])).toEqual([
      ['Install probe', false],
      ['Auth probe', false],
    ]);
    expect(lines[0]!.text).toContain('codex');
    expect(lines[1]!.text).toContain('claude');
  });
});

describe('system packages', () => {
  it('reads a list as its names and sends names as a list, without repeats', () => {
    expect(draftOf(['htop', 'jq'])).toBe('htop jq');
    expect(packageNames(' htop,jq\n htop ')).toEqual(['htop', 'jq']);
    expect(wireValue('packages', 'htop, g++-12 libstdc++6')).toEqual(['htop', 'g++-12', 'libstdc++6']);
    expect(wireValue('packages', '   ')).toEqual([]);
  });

  it('accepts package names and refuses anything a shell or apt-get would read as more', () => {
    expect(validateDraft('packages', '')).toBeNull();
    expect(validateDraft('packages', 'htop postgresql-client g++-12 libstdc++6')).toBeNull();

    for (const bad of ['vim;', '$(id)', '-o=APT::X=1', 'Vim', 'x', 'vim=2:9.1', '`id`']) {
      expect(validateDraft('packages', `htop ${bad}`), bad).toContain('is not a package name');
    }
  });

  it('is unchanged when the same names are typed in another spelling', () => {
    const settings = [setting('system.packages', ['htop', 'jq'])];

    expect(changedSettings(settings, { 'system.packages': 'htop,  jq' }, {})).toEqual({});
    expect(changedSettings(settings, { 'system.packages': 'htop' }, {})).toEqual({ 'system.packages': ['htop'] });
  });
});

describe('agents.tags', () => {
  it('reads the map as an object or a JSON string, dropping what is not a list of words', () => {
    expect(tagMapOf({ 'grok-headless': ['developer', 'researcher'] })).toEqual({ 'grok-headless': ['developer', 'researcher'] });
    expect(tagMapOf('{"grok-headless":["developer",1]}')).toEqual({ 'grok-headless': ['developer'] });
    expect(tagMapOf({ 'grok-headless': 'developer' })).toEqual({});
    expect(tagMapOf('not json')).toEqual({});
    expect(tagMapOf(['developer'])).toEqual({});
  });

  it('refuses an empty or over-long tag, as the server does', () => {
    expect(validateTags(['developer', 'tester'])).toBeNull();
    expect(validateTags([])).toBeNull();
    expect(validateTags([''])).not.toBeNull();
    expect(validateTags(['x'.repeat(65)])).not.toBeNull();
  });
});
