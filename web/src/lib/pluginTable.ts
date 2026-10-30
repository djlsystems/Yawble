import type {
  PluginTableCellState,
  PluginTableColumn,
  PluginTableRow,
  PluginTableRowAction,
  PluginTableShape,
} from '../api/types';
import { boundsProblem } from './numberBounds';
import { fieldLabel } from './pluginSettings';

/**
 * A PLUGIN'S `table` SETTING, AS A FORM: rows a person adds, edits, duplicates and deletes on Member
 * settings, each of typed columns, keyed by one string column unique per member ignoring case. Kept
 * out of the component so the rules are one place and testable without a mount.
 *
 * WHAT IS SAVED is `tableBody`: every row's cells a person writes. A READ-ONLY column is the plugin's
 * (its `row.status` record) and is never sent - the Host keeps the stored one whatever a body says. A
 * SECRET column is never in a row at all: what is typed waits in a `TableSecretsDraft` and goes as
 * `tableSecretValues`, and a saved one is never read back - the read says only that it is set, by
 * whom and when. THE HOST IS STILL THE CHECK; `rowProblems` says the same things first.
 */

/** Table -> row (its key) -> secret column -> the value typed, or null to clear the saved one. */
export type TableSecretsDraft = Record<string, Record<string, Record<string, string | null>>>;

/** Table -> row -> secret column that holds a value, as the settings read answers it. */
export type TableSecretStates = Record<string, Record<string, Record<string, PluginTableCellState>>>;

/** The longest text a cell holds - the Host's `PluginTable.MaxTextLength`. */
export const MaxTextLength = 2048;

/** A row's key: a letter or digit, then letters, digits, `-` and `_`, at most 64 characters. */
export const RowNamePattern = /^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$/;

/** A host as a bare address names it: dotted labels ending in letters, `localhost` or IPv4, an optional port. */
const BareHostPattern =
  /^(?:(?:[\p{L}\p{N}](?:[\p{L}\p{N}-]{0,61}[\p{L}\p{N}])?\.)+(?:\p{L}{2,63}|xn--[A-Za-z0-9-]{1,59})|localhost|(?:[0-9]{1,3}\.){3}[0-9]{1,3})\.?(?::[0-9]{1,5})?$/iu;

/**
 * A `format: "url"` cell as it is saved: a bare address (`mysite.com`, `www.mysite.com/login`) with
 * `https://` before it; anything else exactly as written - a value with any scheme (`http://` stays
 * http, `javascript:` stays and still fails the plugin's check), a path (`/login`, `dashboard`) and
 * text that is not an address (`not a url`, `foo bar.com`). The Host's `PluginTableColumn.WithScheme`
 * holds the same rule, so a save from anywhere stores the same.
 */
export function withScheme(value: string): string {
  const text = value.trim();
  if (text === '') return value;
  if (/^[A-Za-z][A-Za-z0-9+.-]*:/.test(text) && !/^[^:/?#]+:[0-9]+(?:[/?#]|$)/.test(text)) return value;
  const host = text.split(/[/?#]/)[0]!;
  return BareHostPattern.test(host) ? `https://${text}` : value;
}

/** `row` with each `format: "url"` cell a person writes given `https://` when it is a bare address. */
export function withSchemes(shape: PluginTableShape, row: PluginTableRow): PluginTableRow {
  const out = { ...row };
  for (const column of writableColumns(shape)) {
    const value = out[column.name];
    if (column.format === 'url' && typeof value === 'string') out[column.name] = withScheme(value);
  }
  return out;
}

/** The rows of a value, or none for a value that is not a list of rows. */
export function tableRows(value: unknown): PluginTableRow[] {
  return Array.isArray(value) ? value.filter((row): row is PluginTableRow => typeof row === 'object' && row !== null && !Array.isArray(row)) : [];
}

/** What a column is called: the manifest's `label`, else its name in words. */
export const columnLabel = (column: PluginTableColumn) => fieldLabel(column.name, column);

/** The columns a person types into on the row editor: neither read-only nor secret. */
export const writableColumns = (shape: PluginTableShape) => shape.columns.filter((c) => !c.readOnly && c.type !== 'secret');

/** The secret columns a person types into: secret and not read-only. A read-only secret is the platform's. */
export const typedSecretColumns = (shape: PluginTableShape) => shape.columns.filter((c) => c.type === 'secret' && !c.readOnly);

/** The row's key, or '' when it has none yet. */
export const rowName = (shape: PluginTableShape, row: PluginTableRow) => {
  const key = row[shape.key];
  return typeof key === 'string' ? key : '';
};

/** A new row: each writable column's default, a bool with none off. */
export function emptyRow(shape: PluginTableShape): PluginTableRow {
  const row: PluginTableRow = {};
  for (const column of writableColumns(shape)) {
    if (column.default !== null && column.default !== undefined) row[column.name] = column.default;
    else if (column.type === 'bool') row[column.name] = false;
  }
  return row;
}

/** Whether a cell is empty: absent, null or blank text. */
const blank = (value: unknown) => value === undefined || value === null || (typeof value === 'string' && value.trim() === '');

/** A number cell as the number it names, or null. */
function asNumber(value: unknown): number | null {
  if (typeof value === 'number') return Number.isFinite(value) ? value : null;
  if (typeof value !== 'string' || value.trim() === '') return null;
  const number = Number(value);
  return Number.isFinite(number) ? number : null;
}

/**
 * Why each cell of `row` cannot be saved, by column - empty when it can. `others` are the table's
 * other rows, for the key's uniqueness. The same rules the Host holds a row to: the key's shape and
 * uniqueness ignoring case, required cells, an enum's choices, a number's type and bounds, one line
 * of at most `MaxTextLength` characters.
 */
export function rowProblems(shape: PluginTableShape, row: PluginTableRow, others: PluginTableRow[]): Record<string, string> {
  const problems: Record<string, string> = {};
  const name = rowName(shape, row);
  const keyColumn = shape.columns.find((c) => c.name === shape.key);
  const keyLabel = keyColumn ? columnLabel(keyColumn) : shape.key;

  if (!RowNamePattern.test(name)) {
    problems[shape.key] = `${keyLabel} must be 1 to 64 letters, digits, - and _, starting with a letter or digit.`;
  } else if (others.some((other) => rowName(shape, other).toLowerCase() === name.toLowerCase())) {
    problems[shape.key] = `Another row is already named '${name}'.`;
  }

  for (const column of writableColumns(shape)) {
    if (column.name === shape.key) continue;
    const value = row[column.name];
    const label = columnLabel(column);

    if (blank(value)) {
      if (column.required && column.default === null && column.type !== 'bool') problems[column.name] = `${label} is required.`;
      continue;
    }

    if (column.type === 'number') {
      const number = asNumber(value);
      const problem = number === null ? `${label} must be a number.` : boundsProblem(label, column, number);
      if (problem) problems[column.name] = problem;
    } else if (column.type === 'enum') {
      if (!column.enum?.includes(String(value))) problems[column.name] = `${label} must be one of: ${(column.enum ?? []).join(', ')}.`;
    } else if (column.type === 'string') {
      const text = String(value);
      if (text.length > MaxTextLength) problems[column.name] = `${label} is longer than ${MaxTextLength} characters.`;
      else if (/[\r\n\0]/.test(text)) problems[column.name] = `${label} must be one line.`;
    }
  }

  return problems;
}

/** The first sentence that holds the table's save, or null: too many rows, or a row with a problem. */
export function tableProblem(shape: PluginTableShape, rows: PluginTableRow[]): string | null {
  if (rows.length > shape.maxRows) return `It holds ${rows.length} rows; it takes at most ${shape.maxRows}.`;

  for (const [index, row] of rows.entries()) {
    const problems = Object.values(rowProblems(shape, row, rows.filter((_, other) => other !== index)));
    if (problems.length > 0) return `${rowName(shape, row) || `Row ${index + 1}`}: ${problems[0]}`;
  }

  return null;
}

/**
 * A row as it is saved: the cells a person writes, a number as a number, a blank text cell left out
 * (its default), and never a read-only or a secret cell.
 */
export function rowForSave(shape: PluginTableShape, row: PluginTableRow): PluginTableRow {
  const saved: PluginTableRow = {};

  for (const column of writableColumns(shape)) {
    const value = row[column.name];
    if (column.type === 'bool') {
      if (typeof value === 'boolean') saved[column.name] = value;
      continue;
    }

    if (blank(value)) continue;
    saved[column.name] = column.type === 'number' ? asNumber(value) ?? String(value) : String(value);
  }

  return saved;
}

/** The table's rows as the save sends them. */
export const tableBody = (shape: PluginTableShape, rows: PluginTableRow[]) => rows.map((row) => rowForSave(shape, row));

/** A name not yet taken for a copy of `name`: `name-copy`, then `name-copy-2`, and so on. */
export function duplicateName(name: string, taken: string[]): string {
  const used = new Set(taken.map((entry) => entry.toLowerCase()));
  const stem = `${name}-copy`.slice(0, 58);
  if (!used.has(stem.toLowerCase())) return stem;

  for (let n = 2; ; n++) {
    const candidate = `${stem}-${n}`;
    if (!used.has(candidate.toLowerCase())) return candidate;
  }
}

/** A copy of a row under a new name: its writable cells only. A secret and a status are never copied. */
export function duplicateRow(shape: PluginTableShape, row: PluginTableRow, rows: PluginTableRow[]): PluginTableRow {
  return { ...rowForSave(shape, row), [shape.key]: duplicateName(rowName(shape, row), rows.map((r) => rowName(shape, r))) };
}

/** The row actions for a SAVED row: each whose column holds its value. A row not saved yet has none. */
export function rowActionsFor(shape: PluginTableShape, row: PluginTableRow, saved: boolean): PluginTableRowAction[] {
  if (!saved) return [];
  return shape.rowActions.filter((action) => row[action.column] === action.equals);
}

/** Where a row action's link leads, for one member's row: with its cells and table, when known. */
export interface RowActionContext {
  team: string;
  member: string;
  table: string;
  row: string;
  cells?: PluginTableRow;
  shape?: PluginTableShape;
}

/**
 * THE ROW ACTION HOOK: action id -> the link for a row, or null when that row has none. The settings
 * screen shows a row action's link only when its action is registered here, so an action whose page
 * is not built yet shows nothing. The interactive sign-in page registers `sign-in`.
 */
export const rowActionLinks: Record<string, (context: RowActionContext) => string | null> = {};

/**
 * Whether a row is one the sign-in page takes, as the Host's `PluginSignInRows` decides: not switched
 * off in a bool `active` column (its default when the cell is empty), with an http(s) `siteUrl`. The
 * `method` match is the row action's own `when`.
 */
export function isSignInEntry(shape: PluginTableShape, cells: PluginTableRow): boolean {
  const active = shape.columns.find((c) => c.name === 'active' && c.type === 'bool');
  if (active && (cells.active ?? active.default) === false) return false;
  const site = cells.siteUrl;
  if (typeof site !== 'string') return false;
  try {
    return ['http:', 'https:'].includes(new URL(site).protocol);
  } catch {
    return false;
  }
}

/** The interactive sign-in page for a row (SignInPage.vue): a live browser the person signs in with. None for a row it does not take. */
export function signInHref({ team, member, table, row, cells, shape }: RowActionContext): string | null {
  if (cells && shape && !isSignInEntry(shape, cells)) return null;
  const part = encodeURIComponent;
  return `#/teams/${part(team)}/members/${part(member)}/sign-in/${part(table)}/${part(row)}`;
}

rowActionLinks['sign-in'] = signInHref;

/** The link for a row action, or null while no page is registered for it or the row has none. */
export function rowActionHref(action: string, context: RowActionContext): string | null {
  const link = rowActionLinks[action];
  return link ? link(context) : null;
}

/** `tableSecretValues` to send: every cell typed or cleared, never an empty box. Undefined when none was. */
export function tableSecretValuesBody(draft: TableSecretsDraft): TableSecretsDraft | undefined {
  const body: TableSecretsDraft = {};

  for (const [table, rows] of Object.entries(draft)) {
    for (const [row, cells] of Object.entries(rows)) {
      const kept = Object.fromEntries(Object.entries(cells).filter(([, value]) => value === null || value !== ''));
      if (Object.keys(kept).length === 0) continue;
      (body[table] ??= {})[row] = kept;
    }
  }

  return Object.keys(body).length > 0 ? body : undefined;
}

/** The draft with one row's typed cells replaced (an empty `cells` drops the row's entry). */
export function withRowSecrets(draft: TableSecretsDraft, table: string, row: string, cells: Record<string, string | null>): TableSecretsDraft {
  const { [row]: _, ...rest } = draft[table] ?? {};
  const kept = Object.fromEntries(Object.entries(cells).filter(([, value]) => value === null || value !== ''));
  return { ...draft, [table]: Object.keys(kept).length > 0 ? { ...rest, [row]: kept } : rest };
}

/** Whether the saved row holds a value for the secret column, by the read's states. */
export function cellState(states: TableSecretStates | undefined, table: string, row: string, column: string): PluginTableCellState | null {
  const rows = states?.[table] ?? {};
  const found = Object.entries(rows).find(([name]) => name.toLowerCase() === row.toLowerCase());
  return found?.[1][column] ?? null;
}

/** A read-only cell in words, for the table: text as it is, a bool as Yes/No, nothing as a dash. */
export function cellText(value: unknown): string {
  if (value === undefined || value === null || value === '') return '—';
  if (typeof value === 'boolean') return value ? 'Yes' : 'No';
  return String(value);
}
