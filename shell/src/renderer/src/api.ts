import type { YawbleApi } from '../../shared/api';

/** window.yawble, or the one a test passes in. */
export function resolveApi(api?: YawbleApi): YawbleApi {
  return api ?? window.yawble;
}

/** An IPC error as the person should read it (Electron prefixes "Error invoking remote method ..."). */
export function errorText(e: unknown): string {
  const raw = e instanceof Error ? e.message : String(e);
  return raw.replace(/^Error invoking remote method '[^']+': /, '').replace(/^(Core)?RpcError: /, '').replace(/^Error: /, '');
}

export function when(iso: string | null | undefined): string {
  if (!iso) return '—';
  const d = new Date(iso);
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
}

export function lines(text: string): string[] {
  return text.split(/[\n,]/).map((s) => s.trim()).filter((s) => s.length > 0);
}
