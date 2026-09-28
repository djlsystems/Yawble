import { describe, expect, it } from 'vitest';
import type { PluginList } from '../../api/types';
import { formatManifest, pluginEvents, pluginRows, pluginSkill } from '../plugins';
import { hostList, hostPlugin } from '../../test/pluginFixtures';

const echo = hostPlugin({
  id: 'sample-echo',
  name: 'Sample Echo',
  version: '0.2.0',
  publishes: [{ type: 'plugin.sample-echo.echoed', summary: 'One echo done.' }],
  skills: ['skills/SKILL.md'],
  skill: 'plugin-sample-echo',
});

describe('pluginRows', () => {
  it('is one row per version folder, by id then version, the installed one carrying its plugin', () => {
    const list: PluginList = {
      plugins: [echo],
      refused: [{ id: 'broken', reason: 'bad manifest.' }],
      versions: [
        { id: 'sample-echo', version: '0.10.0', name: 'Sample Echo', active: false, verdict: 'inactive', reason: null },
        { id: 'sample-echo', version: '0.2.0', name: 'Sample Echo', active: true, verdict: 'installed', reason: null },
        { id: 'broken', version: '1.0.0', name: null, active: true, verdict: 'refused', reason: 'bad manifest.' },
      ],
    };

    const rows = pluginRows(list);

    expect(rows.map((row) => [row.id, row.version, row.verdict])).toEqual([
      ['broken', '1.0.0', 'refused'],
      ['sample-echo', '0.2.0', 'installed'],
      ['sample-echo', '0.10.0', 'inactive'],
    ]);
    expect(rows[0]).toMatchObject({ name: 'broken', reason: 'bad manifest.', active: true });
    expect(rows[1]).toMatchObject({ verdict: 'installed', active: true, plugin: echo });
  });

  it('adds a row for a refusal no version folder carries: a missing active folder, a dot-named folder', () => {
    const missing = "`active` names '9.9', which is not a version directory.";
    const list: PluginList = {
      plugins: [],
      refused: [{ id: 'relay', reason: missing }, { id: '.staging', reason: 'the directory name is not a plugin id.' }],
      versions: [{ id: 'relay', version: '1.0.0', name: 'Relay', active: false, verdict: 'inactive', reason: null }],
    };

    const rows = pluginRows(list);

    expect(rows.map((row) => [row.id, row.version, row.verdict])).toEqual([
      ['.staging', null, 'refused'],
      ['relay', null, 'refused'],
      ['relay', '1.0.0', 'inactive'],
    ]);
    expect(rows.find((row) => row.id === 'relay' && row.verdict === 'refused')).toMatchObject({ reason: missing });
  });

  it('does not repeat a refusal its version row already says', () => {
    const list: PluginList = {
      plugins: [],
      refused: [{ id: 'broken', reason: 'bad manifest.' }],
      versions: [{ id: 'broken', version: '1.0.0', name: null, active: true, verdict: 'refused', reason: 'bad manifest.' }],
    };

    expect(pluginRows(list)).toHaveLength(1);
  });

  it('lists a refused plugin with no version folder once, from its versions row', () => {
    const list: PluginList = {
      plugins: [],
      refused: [{ id: 'empty', reason: 'it has no version directories.' }],
      versions: [{ id: 'empty', version: null, name: null, active: false, verdict: 'refused', reason: 'it has no version directories.' }],
    };

    expect(pluginRows(list)).toEqual([
      expect.objectContaining({ id: 'empty', version: null, verdict: 'refused', reason: 'it has no version directories.' }),
    ]);
  });

  it('is empty when nothing is in the plugins folder', () => {
    expect(pluginRows(hostList([]))).toEqual([]);
  });
});

describe('pluginEvents', () => {
  it("reads the Host's `publishes`: full types, each with its summary", () => {
    expect(pluginEvents(echo)).toEqual([{ type: 'plugin.sample-echo.echoed', summary: 'One echo done.' }]);
  });

  it('is empty for a plugin that publishes nothing', () => {
    expect(pluginEvents(hostPlugin({ id: 'quiet' }))).toEqual([]);
  });
});

describe('pluginSkill', () => {
  it("is the skill's name the Host sends, never a file name", () => {
    expect(pluginSkill(echo)).toBe('plugin-sample-echo');
  });

  it('is null for a plugin with no skill', () => {
    expect(pluginSkill(hostPlugin({ id: 'bare' }))).toBeNull();
  });
});

describe('formatManifest', () => {
  it('re-indents a manifest that parses', () => {
    expect(formatManifest('{"id":"x","version":"1"}')).toBe('{\n  "id": "x",\n  "version": "1"\n}');
  });

  it('keeps one that does not parse exactly as the Host holds it', () => {
    expect(formatManifest('{"id": ')).toBe('{"id": ');
  });
});
