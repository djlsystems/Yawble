// Where a link into the remote view asks it to open. The cloud site's org dashboard opens a row as
// remote.html#workflow={wf} (the Workflows page at that workflow) and a "Needs attention" row as #team={id} (that team).
// Both may be given (#workflow=12&team=3): the team is then a hint, the page's team filter when the core does not name
// the workflow's team. #terminal/<memberId> is an agent's terminal window, picked by main.ts before anything here runs: this module leaves it alone. Pure: no window, no api.

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
