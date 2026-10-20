// @vitest-environment happy-dom
import { describe, expect, it } from 'vitest';
import { itemLabel } from '../backlog';
import { Ribbon, needsActiveWorkTeam, ribbonEntries } from '../ribbon';
import { inTextField, matchPalette, opensPalette, paletteEntries, type PaletteKeyPress, type PaletteSource } from '../palette';

function source(patch: Partial<PaletteSource> = {}): PaletteSource {
  return {
    teams: [
      { id: 'alpha', name: 'Alpha', state: { text: 'running', token: 'running' }, members: [{ id: 'Manager', name: 'Manager' }, { id: 'mailer', name: 'Mail Sorter' }] },
      { id: 'beta', name: 'Beta Squad', state: null, members: [{ id: 'Manager', name: 'Manager' }] },
    ],
    items: [
      { id: 17, title: 'Sort the inbound mail', state: 'ready' },
      { id: 18, title: 'Index the archive', state: 'pending' },
      { id: 545, title: 'Mail merge for the newsletter', state: 'declared' },
    ],
    outcomes: [
      { id: 'o-1', name: 'Faster replies', status: 'active' },
      { id: 'o-2', name: 'Cleaner archive', status: 'proposed' },
    ],
    ribbon: Ribbon,
    activeWorkTeamId: 'alpha',
    activeTeamName: 'Alpha',
    ...patch,
  };
}

const keys = (query: string, from = source()) => matchPalette(paletteEntries(from), query).map((entry) => entry.key);

describe('the palette matches by words', () => {
  it('matches case-insensitively on the start of any word', () => {
    // A label the query begins first, then rows with a word it begins, in the list's order.
    expect(keys('MAIL')).toEqual(['item:545', 'member:alpha/mailer', 'item:17']);
    expect(keys('mail')).toEqual(keys('MAIL'));
  });

  it('needs every word of the query, in any order', () => {
    expect(keys('mail inbound')).toEqual(['item:17']);
    expect(keys('inbound mail')).toEqual(['item:17']);
    expect(keys('mail nothing')).toEqual([]);
  });

  it('finds a member as team / member, by either half', () => {
    expect(keys('alpha sorter')).toEqual(['member:alpha/mailer']);
    expect(keys('beta manager')).toEqual(['member:beta/Manager']);
    const member = paletteEntries(source()).find((entry) => entry.key === 'member:alpha/mailer');
    expect(member?.label).toBe('Alpha / Mail Sorter');
    expect(member?.target).toEqual({ kind: 'member', team: 'alpha', member: 'mailer' });
  });

  it('finds teams with their state chip, views, ribbon commands and outcomes by name', () => {
    const entries = paletteEntries(source());
    expect(matchPalette(entries, 'beta squad')[0]?.target).toEqual({ kind: 'team', team: 'beta' });
    expect(entries.find((entry) => entry.key === 'team:alpha')?.state).toEqual({ text: 'running', token: 'running' });
    expect(matchPalette(entries, 'monitor')[0]?.target).toEqual({ kind: 'view', view: 'monitor' });
    expect(matchPalette(entries, 'new team')[0]?.target).toEqual({ kind: 'action', action: 'admin-new-team' });
    expect(matchPalette(entries, 'faster')[0]?.target).toEqual({ kind: 'outcome', id: 'o-1' });
    expect(entries.find((entry) => entry.key === 'outcome:o-2')?.state?.text).toBe('proposed');
  });

  it('an exact label ranks above a label it only begins', () => {
    expect(keys('kanban').slice(0, 2).sort()).toEqual(['action:team-kanban', 'view:kanban']);
  });

  it('an empty query lists everything in the list order, views first', () => {
    const all = paletteEntries(source());
    expect(matchPalette(all, '', 1000).map((entry) => entry.key)).toEqual(all.map((entry) => entry.key));
    expect(all[0]?.key).toBe('view:teams');
  });
});

describe('the palette matches by citation', () => {
  it('builds each item citation with itemLabel over its number', () => {
    const item = paletteEntries(source()).find((entry) => entry.key === 'item:17');
    expect(item?.citation).toBe(itemLabel(17));
    expect(item?.state).toEqual({ text: 'ready', token: 'ready' });
  });

  it('matches a citation case-insensitively', () => {
    expect(keys(itemLabel(545).toLowerCase())).toEqual(['item:545']);
    expect(keys(itemLabel(545))).toEqual(['item:545']);
  });

  it('ranks the exact citation first, ahead of citations it begins and of titles', () => {
    // A citation's first characters match every item they begin; the whole citation, only its own.
    const partial = itemLabel(17).slice(0, -1);
    expect(itemLabel(18).startsWith(partial)).toBe(true);
    expect(keys(partial).slice(0, 2)).toEqual(['item:17', 'item:18']);
    expect(keys(itemLabel(18))).toEqual(['item:18']);

    // A title naming the citation in words ranks below the item that is it.
    const titled = source({ items: [...source().items, { id: 9, title: `Follow up ${itemLabel(17)}`, state: 'ready' }] });
    const ranked = keys(itemLabel(17), titled);
    expect(ranked[0]).toBe('item:17');
    expect(ranked).toEqual(['item:17', 'item:9']);
  });
});

describe('ribbon commands are gated as the ribbon gates them', () => {
  const teamActions = ribbonEntries(Ribbon)
    .map((entry) => entry.item.action)
    .filter((action): action is string => action?.startsWith('team-') === true);

  it('offers no team- command without a team in scope', () => {
    expect(teamActions.length).toBeGreaterThan(0);
    const offered = paletteEntries(source({ activeWorkTeamId: undefined })).map((entry) => entry.key);
    for (const action of teamActions) expect(offered).not.toContain(`action:${action}`);
    expect(offered).toContain('action:backlog');
    expect(offered).toContain('action:admin-new-team');
  });

  it('offers every team- command with one, said under the active team', () => {
    const entries = paletteEntries(source());
    for (const action of teamActions) expect(entries.map((entry) => entry.key)).toContain(`action:${action}`);
    expect(entries.find((entry) => entry.key === 'action:team-settings')?.detail).toBe('Active Team · Alpha');
    expect(entries.find((entry) => entry.key === 'action:admin-settings')?.detail).toBe('Admin');
  });

  it('offers exactly the ribbon buttons the ribbon itself enables', () => {
    for (const active of ['alpha', undefined]) {
      const enabled = ribbonEntries(Ribbon)
        .filter((entry) => entry.item.kind === 'button' && !needsActiveWorkTeam(entry.item.action, active))
        .map((entry) => `action:${entry.item.action}`);
      const offered = paletteEntries(source({ activeWorkTeamId: active }))
        .filter((entry) => entry.target.kind === 'action')
        .map((entry) => entry.key);
      expect(offered).toEqual(enabled);
    }
  });
});

describe('the shortcut', () => {
  const key = (patch: Partial<PaletteKeyPress>, target: EventTarget | null = document.body) =>
    opensPalette({ key: 'k', ctrlKey: false, metaKey: false, altKey: false, target, ...patch });

  it('opens on Ctrl+K, Cmd+K and a bare slash', () => {
    expect(key({ ctrlKey: true })).toBe(true);
    expect(key({ metaKey: true, key: 'K' })).toBe(true);
    expect(key({ key: '/' })).toBe(true);
    expect(key({})).toBe(false);
    expect(key({ ctrlKey: true, altKey: true })).toBe(false);
    expect(key({ key: '/', ctrlKey: true })).toBe(false);
  });

  it('never opens from a text field, a textarea or contenteditable', () => {
    const input = document.createElement('input');
    const textarea = document.createElement('textarea');
    const editable = document.createElement('div');
    editable.setAttribute('contenteditable', 'true');
    const inside = document.createElement('span');
    editable.appendChild(inside);
    const checkbox = document.createElement('input');
    checkbox.type = 'checkbox';

    for (const target of [input, textarea, editable, inside]) {
      expect(inTextField(target)).toBe(true);
      expect(key({ key: '/' }, target)).toBe(false);
      expect(key({ ctrlKey: true }, target)).toBe(false);
    }
    expect(inTextField(checkbox)).toBe(false);
    expect(key({ key: '/' }, checkbox)).toBe(true);
  });
});
