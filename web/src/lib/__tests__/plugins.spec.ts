import { describe, expect, it } from 'vitest';
import type { PluginList } from '../../api/types';
import { formatManifest, pluginEvents, pluginRows, pluginSkill } from '../plugins';
import { pluginChoiceLabel } from '../plugins';
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

  it('has no row for a built-in plugin, with or without a version row naming it', () => {
    const builtIn = hostPlugin({ id: 'team-connections', name: 'Team Connections', builtIn: true, locked: true });

    // Without a version row, the built-in still has none; an installed plugin always has its own (PLG 3, rename).
    expect(pluginRows({ plugins: [builtIn, echo], refused: [], versions: [] }).map((row) => row.id)).toEqual(['sample-echo']);
    expect(pluginRows(hostList([builtIn, echo])).map((row) => row.id)).toEqual(['sample-echo']);
  });

  it("keeps a folder under a built-in's id, refused with the Host's sentence", () => {
    const builtIn = hostPlugin({ id: 'team-connections', name: 'Team Connections', builtIn: true, locked: true });
    const reason = "'team-connections' is a built-in plugin's id; the platform's own is used.";

    expect(pluginRows({ plugins: [builtIn], refused: [{ id: 'team-connections', reason }], versions: [] })).toEqual([
      expect.objectContaining({ id: 'team-connections', verdict: 'refused', reason }),
    ]);
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

describe('pluginChoiceLabel: no two plugin choices read alike', () => {
  const installed = [
    { id: 'fixture-old', name: 'Fixture Cloud', version: '1.1.4' },
    { id: 'fixture-new', name: ' fixture cloud ', version: '3.0.0' },
    { id: 'fixture-mail', name: 'Fixture Mail', version: '2.0.0' },
    { id: 'team-connections', name: 'Fixture Mail', version: '1.0.0', builtIn: true },
  ];

  it('names id and version when another installed plugin shares the name, trimmed and in any case', () => {
    expect(pluginChoiceLabel(installed[0]!, installed)).toBe('Fixture Cloud (plugin, fixture-old 1.1.4)');
    expect(pluginChoiceLabel(installed[1]!, installed)).toBe('fixture cloud (plugin, fixture-new 3.0.0)');
  });

  it('reads as before for a name nobody else has - a built-in sharing it does not count', () => {
    expect(pluginChoiceLabel(installed[2]!, installed)).toBe('Fixture Mail (plugin)');
    expect(pluginChoiceLabel(installed[0]!, [installed[0]!])).toBe('Fixture Cloud (plugin)');
  });
});
