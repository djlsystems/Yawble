// B115: the contract between the remote view opened by the cloud (remote.html?embed=1&theme=light|dark, in a frame)
// and the cloud page around it. window.postMessage both ways; both sides check event.origin === location.origin and
// event.source (the frame's parent window, or the frame's window), and ignore anything else. The text of the contract is
// in docs/cloud-link.md ("Embedded in the cloud"); the cloud team has the same text in B116. No backward compatibility:
// a change here is a new version.

export const EMBED_VERSION = 1;

/** The short icon keys: the cloud draws its own icon for a key it knows. */
export type EmbedIcon = 'newChat' | 'concierge' | 'teams' | 'backlog' | 'workflows' | 'kanban' | 'documents' | 'monitor' | 'logs' | 'terminal' | 'help';

/**
 * The places, in Sidebar.vue's order: New chat, Concierge, Teams, Backlog, Workflows, Kanban board, Documents, Monitor, Logs;
 * then the two a phone's drawer has at its bottom (B117): Terminal (the drawer's Terminal: shows or hides the terminal pane;
 * active while it is shown) and Help (the drawer's About Yawble: Settings at System, with the version and the help links).
 * Terminal is not in the list for a read-only viewer (Visitor): the core refuses every terminal call to one.
 */
export type EmbedPlaceId = 'newChat' | 'concierge' | 'teams' | 'backlog' | 'workflows' | 'board' | 'documents' | 'monitor' | 'logs' | 'terminal' | 'help';

export interface EmbedPlace {
  id: EmbedPlaceId;
  label: string;
  icon: EmbedIcon;
  /** A count next to the label (Teams: how many teams; Workflows: how many are open), or null for none. */
  badge: number | null;
  /** The screen shown now (the sidebar's highlighted place). */
  active: boolean;
  disabled: boolean;
}

/** A team's dot, as the sidebar draws it. The cloud draws a value it does not know as idle. */
export type EmbedTeamDot = 'idle' | 'running' | 'queued' | 'paused' | 'attention';

export interface EmbedTeam {
  id: number;
  name: string;
  dot: EmbedTeamDot;
  /** How many of its agents are running, or null when none is. */
  badge: number | null;
  /** This team's page is open. */
  active: boolean;
}

/** A chat's actions, as the sidebar's buttons: Rename, and Delete (danger, asks first). */
export type EmbedChatActionId = 'rename' | 'delete';

export interface EmbedChatAction {
  id: EmbedChatActionId;
  label: string;
  danger: boolean;
}

export interface EmbedChat {
  id: string;
  title: string;
  /** This chat is open. */
  active: boolean;
  /** 'deleting' while the core deletes it, else null. */
  busy: 'deleting' | null;
  /** Empty for a read-only viewer. */
  actions: EmbedChatAction[];
}

export interface EmbedNav {
  places: EmbedPlace[];
  teams: EmbedTeam[];
  canNewTeam: boolean;
  chats: EmbedChat[];
  /** The line under the sidebar today ("Run slots: 1 / 4 in use · 0 queued"), or null before the first answer. */
  runSlots: string | null;
  /** A Visitor's view. */
  readOnly: boolean;
}

export type EmbedTheme = 'light' | 'dark';

/** Desktop to cloud: the sidebar, once on load and each time anything in it changes. */
export interface EmbedNavMessage { type: 'yawble.nav'; version: 1; nav: EmbedNav }

/** Desktop to cloud: once it listens. */
export interface EmbedReadyMessage { type: 'yawble.ready'; version: 1 }

/** Where the cloud asks the desktop to go: exactly what the same click in the desktop sidebar does. */
export type EmbedNavigateTarget =
  | { kind: 'place'; id: EmbedPlaceId }
  | { kind: 'team'; id: number }
  | { kind: 'chat'; id: string }
  | { kind: 'newTeam' }
  | { kind: 'chatAction'; id: string; action: EmbedChatActionId };

/** Cloud to desktop. */
export interface EmbedNavigateMessage { type: 'yawble.navigate'; to: EmbedNavigateTarget }

/** Cloud to desktop: the page's theme. */
export interface EmbedThemeMessage { type: 'yawble.theme'; theme: EmbedTheme }

export type EmbedToCloud = EmbedNavMessage | EmbedReadyMessage;
export type CloudToEmbed = EmbedNavigateMessage | EmbedThemeMessage;
