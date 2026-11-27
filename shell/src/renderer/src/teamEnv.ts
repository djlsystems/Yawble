// A team's environment variables in Team settings > Environment (teams.env, teams.setEnv).
//
// In the window on the computer the values are shown and edited. Over the Cloud Link the computer sends every name with
// the fixed text ENV_HIDDEN in place of its value: the values stay on the computer. Such a row shows an empty box with
// "set on the computer" as its hint. Saved untouched, it goes back as ENV_HIDDEN, which the computer reads as "keep the
// value this variable has"; a value typed into the box replaces it.
//
// The computer keeps the values encrypted, under a key in its own credential store. A value it cannot read (the data
// came from another computer, which has no such key) arrives as ENV_UNREADABLE, in the window and over the Cloud Link
// alike. Such a row shows an empty box with "enter this value again" as its hint. Saved untouched, it goes back as
// ENV_UNREADABLE and stays as it is; a value typed into the box replaces it.

/** What the computer sends a Cloud Link viewer in place of a value (the core's TeamEnv.HiddenValue). */
export const ENV_HIDDEN = '(set on the computer)';

/** The hint in a hidden value's box. */
export const ENV_HIDDEN_HINT = 'set on the computer';

/** What the computer sends in place of a value it cannot read (the core's TeamEnv.UnreadableValue). */
export const ENV_UNREADABLE = '(enter this value again)';

/** The hint in an unreadable value's box. */
export const ENV_UNREADABLE_HINT = 'enter this value again';

export interface EnvRow { name: string; value: string; hidden: boolean; unreadable?: boolean }

/**
 * The rows of a teams.env answer: a hidden value is an empty box marked hidden, an unreadable one an empty box marked
 * unreadable, never the marker text as a value.
 */
export function envRows(env: Record<string, string>): EnvRow[] {
  return Object.entries(env).map(([name, value]) => {
    if (value === ENV_HIDDEN) return { name, value: '', hidden: true };
    if (value === ENV_UNREADABLE) return { name, value: '', hidden: false, unreadable: true };
    return { name, value, hidden: false };
  });
}

/**
 * The entries teams.setEnv takes: rows with a name. A hidden row whose box is still empty keeps its value on the
 * computer; an unreadable row whose box is still empty stays as it is.
 */
export function envEntries(rows: EnvRow[]): { name: string; value: string }[] {
  return rows.filter((r) => r.name.trim()).map((r) => {
    if (r.value === '' && r.hidden) return { name: r.name, value: ENV_HIDDEN };
    if (r.value === '' && r.unreadable) return { name: r.name, value: ENV_UNREADABLE };
    return { name: r.name, value: r.value };
  });
}

/** The hint in a row's value box. */
export function envHint(row: EnvRow): string {
  return row.hidden ? ENV_HIDDEN_HINT : row.unreadable ? ENV_UNREADABLE_HINT : 'value';
}
