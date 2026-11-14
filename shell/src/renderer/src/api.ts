import type { TeamHosts, YawbleApi } from '../../shared/api';

/** window.yawble, or the one a test passes in. */
export function resolveApi(api?: YawbleApi): YawbleApi {
  return api ?? window.yawble;
}

/**
 * The team's network allow list, oldest first (policy.teamHosts). Both windows send it through call: the desktop's
 * preload bridge and a remote view's socket (the Cloud Link allows policy.*).
 */
export function teamHosts(api: YawbleApi, teamId: number): Promise<TeamHosts> {
  return api.call<TeamHosts>('policy.teamHosts', { teamId });
}

/** Takes a host off the team's list (policy.removeTeamHost; the core audits it) and answers the list as it is now. A host not on it: not_found. */
export function removeTeamHost(api: YawbleApi, teamId: number, host: string): Promise<TeamHosts> {
  return api.call<TeamHosts>('policy.removeTeamHost', { teamId, host });
}

/** An IPC error as the person should read it (Electron prefixes "Error invoking remote method ..."). */
export function errorText(e: unknown): string {
  const raw = e instanceof Error ? e.message : String(e);
  return raw.replace(/^Error invoking remote method '[^']+': /, '').replace(/^(Core)?RpcError(\[[\w.-]*\])?: /, '').replace(/^Error: /, '');
}

/** A core error's code (stale, not_found, ...): on the Error itself (a remote view, a test) or in the desktop's
 * "CoreRpcError[<code>]: ..." text (Electron passes only the text). Null when there is none. */
export function errorCode(e: unknown): string | null {
  const own = typeof e === 'object' && e !== null ? (e as { code?: unknown }).code : undefined;
  if (typeof own === 'string' && own) return own;
  const raw = e instanceof Error ? e.message : String(e);
  return /(?:^|: )CoreRpcError\[([\w.-]+)\]: /.exec(raw)?.[1] ?? null;
}

/** Whether a remote view's call was refused by the Cloud Link itself (error code remote_refused), not failed in the core. */
export function isRemoteRefusal(api: { remote?: boolean }, e: unknown): boolean {
  return !!api.remote && typeof e === 'object' && e !== null && (e as { code?: unknown }).code === 'remote_refused';
}

export function when(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

export function lines(text: string): string[] {
  return text.split(/[\n,]/).map((s) => s.trim()).filter((s) => s.length > 0);
}
