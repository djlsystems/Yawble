// The core <-> shell contract of the Code browser (core/src/Yawble.CodeBrowser/CodeBrowserService.cs serves it). Paths are clone-relative,
// forward slashes, "" = the clone's root. Times are ISO-8601 UTC strings.

export type Marker = 'live' | 'recent' | 'branch';

/** code.open {team, member} */
export interface CodeOpen {
  session: string;
  root: string;
  latest: { path: string; line: number } | null;
  clonePresent: boolean;
}

export interface CodeEntry {
  name: string;
  path: string;
  kind: 'dir' | 'file' | 'link';
  size: number;
  mtime: string;
  marker: Marker | null;
  hidden: boolean;
  hiddenReason?: 'secrets';
}

/** code.list {session, path} */
export interface CodeList { path: string; entries: CodeEntry[] }

export type LineMarker = { from: number; to: number; kind: 'added' | 'changed'; at: string } | { from: number; to: number; kind: 'branch' };
export interface RemovedMarker { afterLine: number; text: string; at: string }

export type FileType = 'text' | 'markdown' | 'image' | 'json' | 'yaml' | 'xml' | 'notShown';

/** code.read {session, path} */
export interface CodeFile {
  path: string;
  type: FileType;
  language?: string;
  size: number;
  content?: string;
  base64?: string;
  notShownReason?: 'binary' | 'tooLarge' | 'secrets';
  lines: LineMarker[];
  removed: RemovedMarker[];
  linesUnknown: boolean;
}

/**
 * code.download {session, path, offset?, version?}: one chunk (at most 4 MiB) of the file, bytes offset..offset+length;
 * size is the whole file's. Ask again from offset + length with the first answer's version until done.
 */
export interface CodeDownload { name: string; base64: string; size: number; offset?: number; length?: number; version?: string; done?: boolean }

/** One path's change. A removal is {from: afterLine, to: afterLine, kind: 'removed'}; ranges is empty when the lines are not known. */
export interface CodeChange { path: string; ranges: { from: number; to: number; kind: 'added' | 'changed' | 'removed' | string }[]; at: string }

/**
 * The code.changed push: at most one per session every 250 ms. changes holds every path changed since the last one
 * (each once, oldest first); path, ranges and at repeat the newest. cloud: whether a cloud viewer's session.
 */
export interface CodeChanged extends CodeChange { session: string; changes?: CodeChange[]; cloud?: boolean }

/** What the browser needs of the app: the shell's window.yawble (or a remote view's) fits as it is. */
export interface CodeBrowserApi {
  call<T = any>(method: string, params?: Record<string, unknown>): Promise<T>;
  onEvent(listener: (event: { event: string; data: any }) => void): () => void;
}
