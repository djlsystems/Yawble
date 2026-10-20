/**
 * Where each member's card sits on a team's board, as a per-viewer preference.
 *
 * A DISPLAY preference like the card size (`boardSize.ts`): it describes this browser's view of a
 * board and nothing the server or another viewer needs, so it lives in `localStorage` under one key
 * per team and follows the same wrapper discipline - storage throws in a private window with cookies
 * blocked, and an arrangement is not worth a crash.
 *
 * THE ARRANGEMENT IS A LIST OF MEMBER IDS, in the order the cards are laid; the grid wraps them into
 * rows and columns by the card size and the window, so a position is an order and nothing more. A
 * member the list does not name (hired since) comes after the named ones, in the roster's order; a
 * member the list names and the roster lacks (removed since) is passed over, and dropped at the next
 * write. Nothing is estimated and nothing is laid out here: the pure functions below decide the
 * order, and the drop-target rule works over boxes the board measured.
 */
import type { InjectionKey } from 'vue';

const StoragePrefix = 'harness.boardOrder.';

/** The key a team's arrangement is stored under. The team is its id, never its display name. */
export function boardOrderKey(team: string): string {
  return `${StoragePrefix}${team}`;
}

/** Every stored arrangement's key, for a reset: nothing else under the prefix is this file's. */
export function storedBoardOrderKeys(): string[] {
  try {
    const keys: string[] = [];
    for (let i = 0; i < localStorage.length; i += 1) {
      const key = localStorage.key(i);
      if (key && key.startsWith(StoragePrefix)) keys.push(key);
    }
    return keys;
  } catch {
    return [];
  }
}

export function readBoardOrder(team: string): string[] {
  try {
    const stored = localStorage.getItem(boardOrderKey(team));
    if (!stored) return [];
    const parsed: unknown = JSON.parse(stored);
    // Storage is editable by hand and shared with older builds: only a list of strings is an order.
    return Array.isArray(parsed) ? parsed.filter((entry): entry is string => typeof entry === 'string') : [];
  } catch {
    return [];
  }
}

export function writeBoardOrder(team: string, order: readonly string[]): void {
  try {
    if (order.length === 0) localStorage.removeItem(boardOrderKey(team));
    else localStorage.setItem(boardOrderKey(team), JSON.stringify(order));
  } catch {
    // Nothing to do and nothing worth saying: the arrangement still holds for this session.
  }
}

/**
 * The members in the arranged order: those the order names first, in its order, then the rest in
 * the roster's own order. An id the order names twice counts once; one the roster lacks is skipped.
 */
export function arrange<T extends { id: string }>(members: readonly T[], order: readonly string[]): T[] {
  const byId = new Map(members.map((member) => [member.id, member]));
  const placed: T[] = [];
  const seen = new Set<string>();
  for (const id of order) {
    const member = byId.get(id);
    if (member && !seen.has(id)) {
      placed.push(member);
      seen.add(id);
    }
  }
  for (const member of members) if (!seen.has(member.id)) placed.push(member);
  return placed;
}

/** The arranged order of a roster's ids, every id of the roster and nothing else. */
function current(roster: readonly string[], order: readonly string[]): string[] {
  return arrange(roster.map((id) => ({ id })), order).map((member) => member.id);
}

/**
 * The order once `moved` is put before `before` (null: at the end). Dropping a card on its own place
 * changes nothing; an id the roster lacks moves nothing. The answer names every member of the roster,
 * so writing it drops ids of members since removed.
 */
export function placed(
  roster: readonly string[],
  order: readonly string[],
  moved: string,
  before: string | null,
): string[] {
  const now = current(roster, order);
  if (!now.includes(moved) || before === moved) return now;
  const without = now.filter((id) => id !== moved);
  const at = before === null ? -1 : without.indexOf(before);
  if (at < 0) return [...without, moved];
  return [...without.slice(0, at), moved, ...without.slice(at)];
}

/**
 * The order once `moved` is moved `delta` places (negative: earlier), clamped to the ends, for the
 * keyboard. Names every member of the roster, as `placed` does.
 */
export function movedBy(roster: readonly string[], order: readonly string[], moved: string, delta: number): string[] {
  const now = current(roster, order);
  const from = now.indexOf(moved);
  if (from < 0 || delta === 0) return now;
  const to = Math.min(now.length - 1, Math.max(0, from + delta));
  if (to === from) return now;
  const without = now.filter((id) => id !== moved);
  return [...without.slice(0, to), moved, ...without.slice(to)];
}

/** A card as the board measured it: its id and its box in the same coordinates as the pointer. */
export interface CardBox {
  id: string;
  left: number;
  top: number;
  right: number;
  bottom: number;
}

/**
 * Where a card let go at (x, y) lands, over the boxes of the OTHER cards in their displayed order:
 * the id of the card it goes before, or null for the end.
 *
 * The pointer's row is the row of cards whose vertical span holds y; above every row it is the
 * first row, below every row it is the end, and in the gap between two rows the nearer one. Within
 * the row, on a card's left half it goes before that card and on its right half after it (before
 * the next card in the displayed order, which may begin the next row); left of the row's first card
 * before it; in a gutter or past the row's last card, after the card to its left.
 */
export function dropBefore(boxes: readonly CardBox[], x: number, y: number): string | null {
  if (boxes.length === 0) return null;

  const rows: CardBox[][] = [];
  for (const box of boxes) {
    const row = rows[rows.length - 1];
    if (row && Math.abs(row[0]!.top - box.top) < 1) row.push(box);
    else rows.push([box]);
  }
  const bottomOf = (row: CardBox[]) => Math.max(...row.map((box) => box.bottom));
  const after = (box: CardBox): string | null => boxes[boxes.indexOf(box) + 1]?.id ?? null;

  let row = rows.find((candidates) => y >= candidates[0]!.top && y <= bottomOf(candidates));
  if (!row) {
    if (y > bottomOf(rows[rows.length - 1]!)) return null;
    if (y < rows[0]![0]!.top) return rows[0]![0]!.id;
    row = rows.reduce((nearest, candidates) => {
      const distance = (r: CardBox[]) => Math.min(Math.abs(y - r[0]!.top), Math.abs(y - bottomOf(r)));
      return distance(candidates) < distance(nearest) ? candidates : nearest;
    });
  }

  for (const box of row) {
    if (x >= box.left && x <= box.right) return x < (box.left + box.right) / 2 ? box.id : after(box);
  }
  if (x < row[0]!.left) return row[0]!.id;
  const leftOf = [...row].reverse().find((box) => box.right <= x) ?? row[row.length - 1]!;
  return after(leftOf);
}

/**
 * What the board hands each card's handle: a press begins a drag of that card, and a key moves it
 * a number of places. A card mounted with no board around it (nothing provided) shows no handle.
 */
export interface BoardArranger {
  start(event: PointerEvent, member: string): void;
  move(member: string, delta: number): void;
}

export const BoardArrangerKey: InjectionKey<BoardArranger> = Symbol('board-arranger');
