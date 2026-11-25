// Where a link into the remote view asks it to open. The cloud site's org dashboard opens a row as
// remote.html#workflow={wf} (that workflow's thread in its team) and a "Needs attention" row as #team={id} (that team).
// Both may be given (#workflow=12&team=3): the team is then a hint, looked at first. #terminal/<memberId> is an agent's
// terminal window, picked by main.ts before anything here runs: this module leaves it alone. Pure: no window, no api.

export type HashTarget =
  | { kind: 'workflow'; workflow: number; team: number | null }
  | { kind: 'team'; team: number };

/** A positive whole id ("12"), or null for anything else ("0", "-1", "1.5", "12abc", "", "1e3"). */
function id(v: string | undefined): number | null {
  if (v === undefined || !/^[1-9]\d{0,15}$/.test(v)) return null;
  const n = Number(v);
  return Number.isSafeInteger(n) ? n : null;
}

/** The place a hash asks for, or null (no hash, #terminal/N, or garbage): the view then opens as it would anyway. */
export function parseRemoteHash(hash: string): HashTarget | null {
  const body = hash.startsWith('#') ? hash.slice(1) : hash;
  if (!body || body.startsWith('terminal/')) return null;
  const fields = new Map<string, string>();
  for (const part of body.split('&')) {
    const eq = part.indexOf('=');
    if (eq <= 0) continue;
    const key = part.slice(0, eq).trim().toLowerCase();
    if (!fields.has(key)) fields.set(key, part.slice(eq + 1).trim());
  }
  const workflow = id(fields.get('workflow'));
  const team = id(fields.get('team'));
  if (workflow !== null) return { kind: 'workflow', workflow, team };
  if (team !== null) return { kind: 'team', team };
  return null;
}

/**
 * The team a workflow belongs to: the hinted team first, then each other team, asking `listIds` for the ids of a team's
 * workflows (bus.workflows, a read a Visitor may call). A team whose list fails is skipped. Null when no team has it.
 */
export async function findWorkflowTeam(
  workflow: number, teamIds: readonly number[], listIds: (teamId: number) => Promise<readonly number[]>, hint: number | null = null,
): Promise<number | null> {
  const order = hint !== null && teamIds.includes(hint) ? [hint, ...teamIds.filter((t) => t !== hint)] : [...teamIds];
  for (const t of order) {
    try { if ((await listIds(t)).includes(workflow)) return t; } catch { /* that team is not readable now: try the next */ }
  }
  return null;
}
