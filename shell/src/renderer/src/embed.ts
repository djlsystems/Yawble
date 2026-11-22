import { effectScope, watch } from 'vue';
import type { ConversationSummary, RunSlots, TeamSummary } from '../../shared/api';
import {
  EMBED_VERSION,
  type EmbedChatActionId, type EmbedIcon, type EmbedNav, type EmbedNavigateTarget, type EmbedPlaceId, type EmbedTheme,
} from '../../shared/embedContract';
import { confirmDialog, type ConfirmOptions } from './confirm';
import { teamState } from './teamState';

// B115 Part B: the remote view embedded in the cloud page (remote.html?embed=1&theme=light|dark, in a frame). The
// desktop's own sidebar and theme switch are not drawn; the sidebar is published to the parent window as yawble.nav and
// the parent's yawble.navigate / yawble.theme messages are followed (contract: shared/embedContract.ts and
// docs/cloud-link.md). This module never imports theme.ts and never touches the browser's storage: the viewer's own
// stored remote theme (theme.ts) is neither read nor written in embed mode.

/** The screens a place opens (Sidebar.vue's View, less Settings, which is not in the sidebar's places). */
export type EmbedView = 'concierge' | 'documents' | 'backlog' | 'workflows' | 'teams' | 'board' | 'monitor';

/** True when the page was opened with embed=1 (the cloud's frame). */
export function isEmbed(search: string = typeof location !== 'undefined' ? location.search : ''): boolean {
  return new URLSearchParams(search).get('embed') === '1';
}

/** The theme the cloud gave in the query (theme=light|dark), or null for none or another value. */
export function embedThemeFromQuery(search: string = typeof location !== 'undefined' ? location.search : ''): EmbedTheme | null {
  const t = new URLSearchParams(search).get('theme');
  return t === 'light' || t === 'dark' ? t : null;
}

/** Shows a theme: data-theme and color-scheme on <html>, as theme.ts does, without keeping it anywhere. */
export function applyEmbedTheme(theme: EmbedTheme, root: HTMLElement | undefined = typeof document !== 'undefined' ? document.documentElement : undefined): void {
  if (!root) return;
  root.setAttribute('data-theme', theme);
  root.style.colorScheme = theme;
}

/** What the sidebar is drawn from: Sidebar.vue's props. */
export interface EmbedNavState {
  /** The screen shown (Sidebar.vue's View, which also has 'settings'). */
  view: string;
  conversations: readonly ConversationSummary[];
  teams: readonly TeamSummary[];
  conversationId: string | null;
  teamId: number | null;
  slots: RunSlots | null;
  /** A Visitor's view (remoteAccess.ts readOnly). */
  readOnly: boolean;
  /** The chat being deleted. */
  removing?: string | null;
  /** How many workflows are open across the teams; null: not known. */
  openWorkflows?: number | null;
}

// The order of the sidebar's places. No Terminal and no Help (embedContract.ts EmbedPlaceId).
const PLACES: readonly { id: EmbedPlaceId; label: string; icon: EmbedIcon }[] = [
  { id: 'newChat', label: 'New chat', icon: 'newChat' },
  { id: 'concierge', label: 'Concierge', icon: 'concierge' },
  { id: 'teams', label: 'Teams', icon: 'teams' },
  { id: 'backlog', label: 'Backlog', icon: 'backlog' },
  { id: 'workflows', label: 'Workflows', icon: 'workflows' },
  { id: 'board', label: 'Kanban board', icon: 'kanban' },
  { id: 'documents', label: 'Documents', icon: 'documents' },
  { id: 'monitor', label: 'Monitor', icon: 'monitor' },
  { id: 'logs', label: 'Logs', icon: 'logs' },
];

export const EMBED_PLACE_IDS: readonly EmbedPlaceId[] = PLACES.map((p) => p.id);

/** The run-slots line under the sidebar, word for word; null before the core's first answer. */
export function runSlotsLine(slots: RunSlots | null): string | null {
  return slots ? `Run slots: ${slots.running} / ${slots.limit} in use · ${slots.queued} queued` : null;
}

/** The sidebar as data (pure): what Sidebar.vue draws for the same state. */
export function buildNav(s: EmbedNavState): EmbedNav {
  const badgeOf = (id: EmbedPlaceId): number | null => {
    if (id === 'teams') return s.teams.length || null;
    if (id === 'workflows') return s.openWorkflows || null;
    return null;
  };
  // New chat opens something rather than show a place: never highlighted, as in the sidebar.
  const activeOf = (id: EmbedPlaceId) => id !== 'newChat' && s.view === id;
  return {
    places: PLACES.map((p) => ({ ...p, badge: badgeOf(p.id), active: activeOf(p.id), disabled: false })),
    teams: s.teams.map((t) => ({
      id: t.id,
      name: t.name,
      dot: teamState(t),
      badge: t.running > 0 ? t.running : null,
      active: s.view === 'teams' && s.teamId === t.id,
    })),
    canNewTeam: !s.readOnly,
    chats: s.conversations.map((c) => ({
      id: c.id,
      title: c.title,
      active: s.view === 'concierge' && s.conversationId === c.id,
      busy: s.removing === c.id ? 'deleting' : null,
      actions: s.readOnly ? [] : [
        { id: 'rename', label: 'Rename', danger: false },
        { id: 'delete', label: 'Delete', danger: true },
      ],
    })),
    runSlots: runSlotsLine(s.slots),
    readOnly: s.readOnly,
  };
}

/** The sidebar's own handlers (App.vue's, the same as Sidebar.vue's emits). */
export interface EmbedHandlers {
  newChat(): void;
  go(view: EmbedView): void;
  logs(): void;
  openTeam(id: number): void;
  openConversation(id: string): void;
  newTeam(): void;
  rename(id: string, title: string): void;
  remove(id: string): void;
}

export interface EmbedDialogs {
  /** Delete's "Delete this chat?" (default: the window's own confirm dialog, confirm.ts). */
  confirm?: (o: ConfirmOptions) => Promise<boolean>;
  /** Rename's new title: resolves the trimmed title, or null when cancelled. Without it Rename does nothing. */
  askTitle?: (current: string) => Promise<string | null>;
}

const PLACE_SET = new Set<string>(EMBED_PLACE_IDS);
const ACTIONS = new Set<string>(['rename', 'delete']);

/** A yawble.navigate message's target when it is well formed, else null. */
export function parseNavigate(data: unknown): EmbedNavigateTarget | null {
  if (!data || typeof data !== 'object' || (data as { type?: unknown }).type !== 'yawble.navigate') return null;
  const to = (data as { to?: unknown }).to as Record<string, unknown> | null | undefined;
  if (!to || typeof to !== 'object') return null;
  switch (to.kind) {
    case 'place': return typeof to.id === 'string' && PLACE_SET.has(to.id) ? { kind: 'place', id: to.id as EmbedPlaceId } : null;
    case 'team': {
      // A number, or its decimal string (a parent that keeps every id as text).
      const id = typeof to.id === 'string' && /^\d+$/.test(to.id) ? Number(to.id) : to.id;
      return typeof id === 'number' && Number.isInteger(id) ? { kind: 'team', id } : null;
    }
    case 'chat': return typeof to.id === 'string' && to.id ? { kind: 'chat', id: to.id } : null;
    case 'newTeam': return { kind: 'newTeam' };
    case 'chatAction':
      return typeof to.id === 'string' && to.id && typeof to.action === 'string' && ACTIONS.has(to.action)
        ? { kind: 'chatAction', id: to.id, action: to.action as EmbedChatActionId } : null;
    default: return null;
  }
}

/** A yawble.theme message's theme when it is well formed, else null. */
export function parseTheme(data: unknown): EmbedTheme | null {
  if (!data || typeof data !== 'object' || (data as { type?: unknown }).type !== 'yawble.theme') return null;
  const t = (data as { theme?: unknown }).theme;
  return t === 'light' || t === 'dark' ? t : null;
}

/**
 * Does what the same click in the sidebar does. Returns a promise for a chat action (its dialog), else undefined.
 * Ignored, as the sidebar's disabled or missing buttons are: a team or chat not in the state, New team, a chat's
 * actions for a read-only viewer, Delete while a chat is being deleted.
 */
export function dispatchNavigate(to: EmbedNavigateTarget, s: EmbedNavState, h: EmbedHandlers, d: EmbedDialogs = {}): Promise<void> | undefined {
  switch (to.kind) {
    case 'place':
      if (to.id === 'newChat') h.newChat();
      else if (to.id === 'logs') h.logs();
      else h.go(to.id);
      return undefined;
    case 'team':
      if (s.teams.some((t) => t.id === to.id)) h.openTeam(to.id);
      return undefined;
    case 'chat':
      if (s.conversations.some((c) => c.id === to.id)) h.openConversation(to.id);
      return undefined;
    case 'newTeam':
      if (!s.readOnly) h.newTeam();
      return undefined;
    case 'chatAction': {
      const chat = s.conversations.find((c) => c.id === to.id);
      if (!chat || s.readOnly) return undefined;
      if (to.action === 'delete') {
        if (s.removing) return undefined;
        const ask = d.confirm ?? confirmDialog;
        return ask({ message: 'Delete this chat?', okLabel: 'Delete', danger: true }).then((ok) => { if (ok) h.remove(chat.id); });
      }
      if (!d.askTitle) return undefined;
      return d.askTitle(chat.title).then((title) => {
        const t = title?.trim();
        if (t) h.rename(chat.id, t);
      });
    }
  }
}

export interface EmbedBridgeOptions {
  /** The sidebar's state, read inside a watch (pass a getter over App.vue's refs). */
  state: () => EmbedNavState;
  handlers: EmbedHandlers;
  dialogs?: EmbedDialogs;
  /** The page's window (tests pass a fake). */
  win?: Pick<Window, 'addEventListener' | 'removeEventListener' | 'parent' | 'location'>;
  /** Where a theme is shown (default <html>). */
  root?: HTMLElement;
  /** Shows a theme the parent sent (App.vue: theme.ts setMode, which keeps nothing in embed mode); default applyEmbedTheme. */
  applyTheme?: (theme: EmbedTheme) => void;
}

export interface EmbedBridge {
  /** Stops listening and publishing. */
  dispose(): void;
}

/**
 * Starts embed mode's side of the contract: applies the query's theme, listens for the parent's messages, posts
 * yawble.ready, then yawble.nav once and again each time the nav changes. Messages are taken only when
 * event.origin === location.origin and event.source === window.parent; anything else is ignored. A page that is not in
 * a frame (its parent is itself) has nobody to talk to: nothing is listened to or posted.
 *
 * App.vue: `if (isEmbed()) onMounted(() => { const b = startEmbedBridge({ state: () => ({ ... }), handlers }); onUnmounted(b.dispose); });`
 */
export function startEmbedBridge(o: EmbedBridgeOptions): EmbedBridge {
  const win = o.win ?? window;
  const root = o.root ?? (typeof document !== 'undefined' ? document.documentElement : undefined);
  const queryTheme = embedThemeFromQuery(win.location.search);
  if (queryTheme) { if (o.applyTheme) o.applyTheme(queryTheme); else applyEmbedTheme(queryTheme, root); }
  const parent = win.parent;
  if (!parent || parent === (win as unknown)) return { dispose() { /* not framed */ } };
  const origin = win.location.origin;
  const post = (msg: unknown) => { try { parent.postMessage(msg, origin); } catch { /* parent gone */ } };

  const onMessage = (e: Event) => {
    const m = e as MessageEvent;
    if (m.origin !== origin || m.source !== parent) return;
    const theme = parseTheme(m.data);
    if (theme) { if (o.applyTheme) o.applyTheme(theme); else applyEmbedTheme(theme, root); return; }
    const to = parseNavigate(m.data);
    if (to) void dispatchNavigate(to, o.state(), o.handlers, o.dialogs);
  };
  win.addEventListener('message', onMessage);
  post({ type: 'yawble.ready', version: EMBED_VERSION });

  let last = '';
  const scope = effectScope(true);
  scope.run(() => watch(() => buildNav(o.state()), (nav) => {
    const text = JSON.stringify(nav);
    if (text === last) return;
    last = text;
    post({ type: 'yawble.nav', version: EMBED_VERSION, nav });
  }, { immediate: true }));

  return {
    dispose() {
      scope.stop();
      win.removeEventListener('message', onMessage);
    },
  };
}
