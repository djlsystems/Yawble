// THE MEMBERS' AVATARS. A member's tile is derived from its team's id and its own, so it is the same
// on every view and after a restart; the sheet's columns and rows come from its size, so another
// sheet of the same tile size needs no change; and a tile is placed by percentages that land the
// cut-in picture exactly on the element.
import { afterEach, describe, expect, it } from 'vitest';
import {
  AvatarInset,
  AvatarTilePx,
  avatarIndex,
  avatarSheet,
  memberAvatar,
  setAvatarSheet,
  tileOf,
  tileSource,
  tileStyle,
} from '../avatars';
import { memberStateOf, stateToken } from '../memberState';
import { asMemberId, asTeamId, type ContainerSnapshot } from '../../api/types';

afterEach(() => setAvatarSheet(null, 0, 0));

describe('a member\'s avatar', () => {
  it('is the same tile every time for the same team and member, whatever the case', () => {
    const first = avatarIndex('alpha', 'Developer', 100);
    expect(avatarIndex('alpha', 'Developer', 100)).toBe(first);
    expect(avatarIndex('ALPHA', 'developer', 100)).toBe(first);
    expect(first).toBeGreaterThanOrEqual(0);
    expect(first).toBeLessThan(100);
  });

  it('is dealt across the sheet, not piled on one tile', () => {
    const tiles = new Set(Array.from({ length: 200 }, (_, i) => avatarIndex('alpha', `Member${i}`, 100)));
    expect(tiles.size).toBeGreaterThan(70);
  });

  it('is none while no sheet has loaded', () => {
    expect(avatarIndex('alpha', 'Developer', 0)).toBe(-1);
    expect(memberAvatar('alpha', 'Developer')).toBe(-1);
  });
});

describe('the sheet', () => {
  it('reads its columns and rows from its size, so a sheet of any number of tiles works', () => {
    setAvatarSheet(null, 1024, 1024);
    expect([avatarSheet.cols, avatarSheet.rows]).toEqual([10, 10]);
    setAvatarSheet(null, AvatarTilePx * 16, AvatarTilePx * 4);
    expect([avatarSheet.cols, avatarSheet.rows]).toEqual([16, 4]);
    expect(memberAvatar('alpha', 'Developer')).toBe(avatarIndex('alpha', 'Developer', 64));
  });

  it('numbers its tiles across then down', () => {
    expect(tileOf(0, 10)).toEqual({ col: 0, row: 0 });
    expect(tileOf(13, 10)).toEqual({ col: 3, row: 1 });
    expect(tileOf(99, 10)).toEqual({ col: 9, row: 9 });
  });

  it('places a tile so its cut-in picture fills the element exactly', () => {
    const style = tileStyle(13, 10, 10);
    const tile = 1 / (1 - 2 * AvatarInset);
    expect(style.backgroundSize).toBe(`${10 * tile * 100}% ${10 * tile * 100}%`);
    // A percentage position p moves the background by p * (element - background): for a 1px element
    // the picture's left edge, (col + inset) tiles in, must land on 0.
    const [x, y] = style.backgroundPosition!.split(' ').map((part) => parseFloat(part) / 100);
    expect(x! * (1 - 10 * tile) + (3 + AvatarInset) * tile).toBeCloseTo(0, 9);
    expect(y! * (1 - 10 * tile) + (1 + AvatarInset) * tile).toBeCloseTo(0, 9);
  });

  it('gives the canvas the same cut-in picture in the sheet\'s own pixels', () => {
    const src = tileSource(13, { cols: 10, rows: 10, width: 1024, height: 1024 });
    expect(src.sx).toBeCloseTo((3 + AvatarInset) * 102.4, 9);
    expect(src.sy).toBeCloseTo((1 + AvatarInset) * 102.4, 9);
    expect(src.sw).toBeCloseTo(102.4 * (1 - 2 * AvatarInset), 9);
  });
});

describe('the state a ring draws', () => {
  const snapshot = (over: Partial<ContainerSnapshot> = {}): ContainerSnapshot => ({
    team: asTeamId('alpha'),
    id: asMemberId('Developer'),
    name: 'Developer',
    agent: 'echo',
    state: 'Idle',
    queueDepth: 0,
    ceiling: 16,
    subscribes: [],
    currentCorrelation: null,
    sinceSeq: 0,
    ...over,
  });

  it('is a mark first, then the run, then idle', () => {
    expect(memberStateOf(snapshot({ state: 'Running', failed: 'boom' }))).toBe('failed');
    expect(memberStateOf(snapshot({ needsDecision: 'which?', blocked: 'stuck' }))).toBe('decision');
    expect(memberStateOf(snapshot({ blocked: 'stuck' }))).toBe('blocked');
    expect(memberStateOf(snapshot({ state: 'Running' }))).toBe('running');
    expect(memberStateOf(snapshot({ state: 'Running', held: true }))).toBe('waiting');
    expect(memberStateOf(snapshot({ queueDepth: 2 }))).toBe('waiting');
    expect(memberStateOf(snapshot())).toBe('idle');
  });

  it('is drawn in the state\'s own token, needs you for a decision', () => {
    expect(stateToken('decision')).toBe('needs-you');
    expect(stateToken('running')).toBe('running');
  });
});
