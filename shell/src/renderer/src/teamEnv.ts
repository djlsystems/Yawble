// A team's environment variables in Team settings > Environment (teams.env, teams.setEnv).
//
// In the window on the computer the values are shown and edited. Over the Cloud Link the computer sends every name with
// the fixed text ENV_HIDDEN in place of its value: the values stay on the computer. Such a row shows an empty box with
// "set on the computer" as its hint. Saved untouched, it goes back as ENV_HIDDEN, which the computer reads as "keep the
// value this variable has"; a value typed into the box replaces it.

/** What the computer sends a Cloud Link viewer in place of a value (the core's TeamEnv.HiddenValue). */
export const ENV_HIDDEN = '(set on the computer)';

/** The hint in a hidden value's box. */
export const ENV_HIDDEN_HINT = 'set on the computer';

export interface EnvRow { name: string; value: string; hidden: boolean }

/** The rows of a teams.env answer: a hidden value is an empty box marked hidden, never the marker text as a value. */
export function envRows(env: Record<string, string>): EnvRow[] {
  return Object.entries(env).map(([name, value]) => (value === ENV_HIDDEN ? { name, value: '', hidden: true } : { name, value, hidden: false }));
}

/** The entries teams.setEnv takes: rows with a name; a hidden row whose box is still empty keeps its value on the computer. */
export function envEntries(rows: EnvRow[]): { name: string; value: string }[] {
  return rows.filter((r) => r.name.trim()).map((r) => ({ name: r.name, value: r.hidden && r.value === '' ? ENV_HIDDEN : r.value }));
}
