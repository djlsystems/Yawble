<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, shallowRef, type Component } from 'vue';
import type { CodeBrowserApi, CodeChange, CodeChanged, CodeDownload, CodeEntry, CodeFile, CodeList, CodeOpen } from './types';
import { PlaceHistory, type Place } from './history';
import { highlightLines, languageFor } from './highlight';
import {
  LINE_HEIGHT, changeStops, folderOf, formatSize, imageMime, isUnder, latestChangeLine, lineDecorations, nextStop, notShownText, previousStop,
  removedByLine,
} from './markers';
import FileList from './FileList.vue';
import CodeLines from './CodeLines.vue';
import { FallbackBusy, FallbackLoading } from './fallbacks';
import { withoutImages } from './safeHtml';

// The read-only Code browser of one member's clone: a breadcrumb, the folder's listing with markers of
// what the agent is working on, and the open file's preview with its recent changes. Everything comes from the core's
// code.* calls and the code.changed push (types.ts); nothing here writes anywhere. The host
// (the shell) puts this in its dialog, which owns Escape and closing, and passes its markdown renderer (markdown-it
// with html disabled), its LoadingNote and BusyLabel.
const props = withDefaults(defineProps<{
  api: CodeBrowserApi;
  team: string;
  member: string;
  /** Agent-written markdown to safe HTML (the app's markdown-it setup: raw HTML is never let through). */
  renderMarkdown: (text: string) => string;
  /** The clock (tests pass a fixed one). */
  now?: () => number;
  /** How often ages ("edited 2 min ago", fading highlights) are redrawn and the folder is quietly listed again; 0: never. */
  tickMs?: number;
  loadingNote?: Component;
  busyLabel?: Component;
  /** Saves a downloaded file (tests pass a fake; the default hands the bytes to the browser as a download). */
  save?: (name: string, bytes: Uint8Array) => void;
}>(), { now: () => Date.now(), tickMs: 30_000 });

const Loading = computed(() => props.loadingNote ?? FallbackLoading);
const Busy = computed(() => props.busyLabel ?? FallbackBusy);

type Phase = 'opening' | 'ready' | 'noClone' | 'failed';
const phase = ref<Phase>('opening');
const error = ref<string | null>(null);
const session = ref<string | null>(null);
const dir = ref('');
const entries = ref<CodeEntry[]>([]);
const listLoading = ref(false);
const listError = ref<string | null>(null);
const filePath = ref<string | null>(null);
const file = shallowRef<CodeFile | null>(null);
const fileLoading = ref(false);
const fileError = ref<string | null>(null);
const onlyChangedFiles = ref(true);
const follow = ref(true);
const showSource = ref(false);
/** The "Updated: jump to change" chip: the line of a change that landed out of view. */
const chip = ref<number | null>(null);
/** The line last scrolled to (a jump, Next / Previous): where Next and Previous step from. */
const current = ref(1);
const target = ref<number | null>(null);
const nowMs = ref(props.now());
const downloading = ref(false);
const history = new PlaceHistory();
const nav = reactive({ back: false, forward: false });
const lines = ref<InstanceType<typeof CodeLines> | null>(null);

function messageOf(e: unknown): string {
  const raw = e instanceof Error ? e.message : String(e);
  return raw.replace(/^Error invoking remote method '[^']+': /, '').replace(/^(Core)?RpcError: /, '').replace(/^Error: /, '');
}
const isNoClone = (e: unknown) => (e as { code?: string } | null)?.code === 'code.noClone' || messageOf(e).includes('code.noClone');
const isRefused = (e: unknown) => (e as { code?: string } | null)?.code === 'code.refused' || messageOf(e).includes('code.refused');
const RANGE_KINDS = new Set(['added', 'changed', 'removed']);

// ---- the file's lines, highlighted once per content ----
const language = computed(() => (file.value ? languageFor(file.value.path, file.value.language, file.value.type) : 'plaintext'));
const htmlLines = computed(() => (file.value?.content !== undefined ? highlightLines(file.value.content, language.value) : []));
const decorations = computed(() => (file.value ? lineDecorations(file.value, htmlLines.value.length, nowMs.value) : []));
const removed = computed(() => (file.value ? removedByLine(file.value.removed, nowMs.value) : new Map()));
const stops = computed(() => (file.value ? changeStops(file.value, nowMs.value) : []));
const latestLine = computed(() => (file.value ? latestChangeLine(file.value) : null));
const isTextual = computed(() => !!file.value && ['text', 'json', 'yaml', 'xml'].includes(file.value.type));
const showsLines = computed(() => !!file.value && (isTextual.value || (file.value.type === 'markdown' && showSource.value)));
// The agent's markdown never loads an image (a local file or a UNC share would be fetched): each is its alt text.
const markdownHtml = computed(() => (file.value?.type === 'markdown' && !showSource.value ? withoutImages(props.renderMarkdown(file.value.content ?? '')) : ''));
const imageSrc = computed(() => (file.value?.type === 'image' && file.value.base64 ? `data:${imageMime(file.value.path)};base64,${file.value.base64}` : ''));
const crumbs = computed(() => {
  const parts = dir.value ? dir.value.split('/') : [];
  return parts.map((name, i) => ({ name, path: parts.slice(0, i + 1).join('/') }));
});
const fileName = computed(() => filePath.value?.split('/').pop() ?? null);

// ---- loading: each answer is applied only if it is still the latest asked for ----
let listSeq = 0;
async function loadList(path: string, quiet = false): Promise<void> {
  if (!session.value) return;
  const seq = ++listSeq;
  if (!quiet) { listLoading.value = true; listError.value = null; }
  try {
    const got = await props.api.call<CodeList>('code.list', { session: session.value, path });
    if (seq !== listSeq) return;
    dir.value = path;
    entries.value = got.entries;
    listError.value = null;
  } catch (e) {
    if (seq !== listSeq) return;
    if (isNoClone(e)) { phase.value = 'noClone'; return; } // the clone went away while the dialog was open
    if (!quiet) { dir.value = path; entries.value = []; listError.value = messageOf(e); }
  } finally {
    if (seq === listSeq) listLoading.value = false;
  }
}

let fileSeq = 0;
async function loadFile(path: string | null, quiet = false): Promise<boolean> {
  const seq = ++fileSeq;
  if (!path || !session.value) { filePath.value = null; file.value = null; fileError.value = null; return false; }
  if (!quiet) { fileLoading.value = true; fileError.value = null; filePath.value = path; }
  try {
    const got = await props.api.call<CodeFile>('code.read', { session: session.value, path });
    if (seq !== fileSeq) return false;
    filePath.value = path;
    file.value = got;
    fileError.value = null;
    return true;
  } catch (e) {
    if (seq !== fileSeq) return false;
    if (isNoClone(e)) { phase.value = 'noClone'; return false; }
    // Quiet (a live reload) keeps what is shown, unless the core refused the file: it was deleted or is hidden now.
    if (!quiet || isRefused(e)) { file.value = null; fileError.value = messageOf(e); }
    return false;
  } finally {
    if (seq === fileSeq) fileLoading.value = false;
  }
}

/** Goes to a place: lists its folder (when another), previews its file, and scrolls to `line` when given. */
async function go(place: Place, opts: { push?: boolean; line?: number | null } = {}): Promise<void> {
  if (opts.push !== false) history.push(place);
  nav.back = history.canBack; nav.forward = history.canForward;
  chip.value = null;
  const listing = place.dir !== dir.value || !entries.value.length ? loadList(place.dir) : Promise.resolve();
  if (place.file !== filePath.value || opts.line) {
    showSource.value = false;
    target.value = null;
    current.value = 1;
    const ok = await loadFile(place.file);
    if (ok && file.value?.type === 'markdown' && opts.line) showSource.value = true; // a change to look at: its lines
    if (ok) await scrollTo(opts.line ?? null, true);
  }
  await listing;
}

function openEntry(e: CodeEntry): void {
  if (e.kind === 'dir') void go({ dir: e.path, file: null });
  else void go({ dir: dir.value, file: e.path });
}
function openFolder(path: string): void { void go({ dir: path, file: null }); }
function back(): void { const p = history.back(); if (p) void go(p, { push: false }); }
function forward(): void { const p = history.forward(); if (p) void go(p, { push: false }); }

// ---- scrolling: lines are a fixed height, so a line's place is known without measuring ----
function scroller(): HTMLElement | null { return lines.value?.el ?? null; }
async function scrollTo(line: number | null, top = false): Promise<void> {
  await nextTick();
  const el = scroller();
  if (!el) return;
  if (!line) { if (top) el.scrollTop = 0; return; }
  current.value = line;
  target.value = line;
  el.scrollTop = Math.max(0, (line - 1) * LINE_HEIGHT - Math.floor(el.clientHeight / 3));
}
function visible(line: number): boolean {
  const el = scroller();
  if (!el || !el.clientHeight) return true; // not laid out: nothing is out of view
  const y = (line - 1) * LINE_HEIGHT;
  return y + LINE_HEIGHT > el.scrollTop && y < el.scrollTop + el.clientHeight;
}
function jumpLatest(): void { if (latestLine.value) void scrollTo(latestLine.value); chip.value = null; }
function jumpNext(): void { const n = nextStop(stops.value, current.value); if (n) void scrollTo(n); }
function jumpPrevious(): void { const n = previousStop(stops.value, current.value); if (n) void scrollTo(n); }
function jumpChip(): void { if (chip.value) void scrollTo(chip.value); chip.value = null; }
async function toggleSource(): Promise<void> {
  showSource.value = !showSource.value;
  if (showSource.value && current.value > 1) await scrollTo(current.value);
}

// ---- live: the core pushes code.changed while the dialog is open ----
let listAgain = false;
let listing = false;
async function relist(): Promise<void> {
  if (listing) { listAgain = true; return; }
  listing = true;
  try {
    do { listAgain = false; await loadList(dir.value, true); } while (listAgain);
  } finally { listing = false; }
}

/** The first changed line of a change: ranges are added/changed/removed (a removal is {from: afterLine, to: afterLine}; afterLine 0 = above line 1). */
function firstLineOf(ranges: CodeChange['ranges'] | undefined): number | null {
  const known = (ranges ?? []).filter((r) => RANGE_KINDS.has(r.kind));
  return known.length ? Math.max(1, Math.min(...known.map((r) => r.from))) : null;
}

async function onChanged(c: CodeChanged): Promise<void> {
  if (c.session !== session.value) return;
  nowMs.value = props.now();
  // The core batches the paths changed in a quarter second (changes, oldest first); an older core sends one path.
  const changes: CodeChange[] = Array.isArray(c.changes) && c.changes.length ? c.changes : [{ path: c.path, ranges: c.ranges, at: c.at }];
  const newest = changes[changes.length - 1];
  if (follow.value && newest.path !== filePath.value) {
    await go({ dir: folderOf(newest.path), file: newest.path }, { line: firstLineOf(newest.ranges) });
    return;
  }
  if (changes.some((ch) => isUnder(ch.path, dir.value))) void relist();
  const open = [...changes].reverse().find((ch) => ch.path === filePath.value);
  // Empty ranges (lines not known, a deleted or hidden file): read the file again and use its newest marker.
  if (open) await reread(open.path, firstLineOf(open.ranges));
}

// The open file is read again at most once at a time, with at most one more waiting: changes that come while a read
// is out are folded into the one waiting (the newest change's line wins), and intermediate ones are never read.
let rereading = false;
let rereadWaiting: { path: string; first: number | null } | null = null;
async function reread(path: string, first: number | null): Promise<void> {
  if (rereading) { rereadWaiting = { path, first: first ?? rereadWaiting?.first ?? null }; return; }
  rereading = true;
  try {
    let next: { path: string; first: number | null } | null = { path, first };
    while (next) {
      rereadWaiting = null;
      await rereadOnce(next.path, next.first);
      next = rereadWaiting;
    }
  } finally { rereading = false; }
}

async function rereadOnce(path: string, first: number | null): Promise<void> {
  if (path !== filePath.value) return; // another file was opened meanwhile
  // The open file: new content in place, the reader's scroll position kept; a change out of view gets the chip.
  const el = scroller();
  const keep = el ? { top: el.scrollTop, left: el.scrollLeft } : null;
  const ok = await loadFile(path, true);
  if (!ok) return;
  await nextTick();
  const after = scroller();
  if (after && keep) { after.scrollTop = keep.top; after.scrollLeft = keep.left; }
  const line = first ?? latestLine.value;
  chip.value = showsLines.value && line && !visible(line) ? line : null;
}

// ---- download: the file's bytes exactly as on disk ----
function saveBytes(name: string, bytes: Uint8Array): void {
  const url = URL.createObjectURL(new Blob([bytes as BlobPart]));
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.rel = 'noopener';
  document.body.appendChild(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}
async function download(): Promise<void> {
  if (!filePath.value || !session.value || downloading.value) return;
  downloading.value = true;
  fileError.value = null;
  try {
    // In chunks (an IPC frame cannot carry 50 MB): from offset 0, with the first answer's version, until done.
    const path = filePath.value;
    let got = await props.api.call<CodeDownload>('code.download', { session: session.value, path, offset: 0 });
    const name = got.name;
    const bytes = new Uint8Array(got.size);
    let filled = 0;
    const version = got.version;
    for (let guard = 0; ; guard++) {
      // Never mix chunks of two versions of the file, and never leave a gap: start again instead.
      if (guard > 0 && got.version !== version) throw new Error('The file changed while it was downloading; download it again.');
      const bin = atob(got.base64);
      const offset = got.offset ?? filled;
      if (offset !== filled) throw new Error('The download came back out of order; download it again.');
      if (offset + bin.length > bytes.length) throw new Error('The download came back longer than the file; download it again.');
      for (let i = 0; i < bin.length; i++) bytes[offset + i] = bin.charCodeAt(i);
      filled = offset + bin.length;
      if (got.done !== false) break;
      if (!bin.length || guard > 1000) throw new Error('The download stopped part way; download it again.');
      got = await props.api.call<CodeDownload>('code.download', { session: session.value, path, offset: filled, version });
    }
    (props.save ?? saveBytes)(name, filled === bytes.length ? bytes : bytes.subarray(0, filled));
  } catch (e) {
    fileError.value = messageOf(e);
  } finally {
    downloading.value = false;
  }
}

// ---- open and close ----
let off: (() => void) | null = null;
let timer: ReturnType<typeof setInterval> | null = null;
let closed = false;
onMounted(async () => {
  off = props.api.onEvent((e) => { if (e.event === 'code.changed') void onChanged(e.data as CodeChanged); });
  try {
    const opened = await props.api.call<CodeOpen>('code.open', { team: props.team, member: props.member });
    if (closed) { void props.api.call('code.close', { session: opened.session }).catch(() => {}); return; }
    session.value = opened.session;
    if (!opened.clonePresent) { phase.value = 'noClone'; return; }
    phase.value = 'ready';
    // It opens on the action: the newest edit inside its folder, scrolled to its line; else the clone's root.
    if (opened.latest) await go({ dir: folderOf(opened.latest.path), file: opened.latest.path }, { line: opened.latest.line });
    else await go({ dir: '', file: null });
  } catch (e) {
    if (isNoClone(e)) phase.value = 'noClone';
    else { phase.value = 'failed'; error.value = messageOf(e); }
    return;
  }
  if (props.tickMs > 0) timer = setInterval(() => { nowMs.value = props.now(); void relist(); }, props.tickMs);
});
onBeforeUnmount(() => {
  closed = true;
  off?.();
  if (timer) clearInterval(timer);
  if (session.value) void props.api.call('code.close', { session: session.value }).catch(() => {});
});

defineExpose({ go, session });
</script>

<template>
  <div class="cb" data-test="code-browser">
    <component :is="Loading" v-if="phase === 'opening'" :text="`Opening ${member}'s code…`" />
    <p v-else-if="phase === 'noClone'" class="cb-plain" data-test="code-no-clone">{{ member }} has no clone on this computer yet, so there is no code to show.</p>
    <p v-else-if="phase === 'failed'" class="error" data-test="code-error">{{ error }}</p>

    <template v-else>
      <div class="cb-bar">
        <button type="button" class="icon" title="Back" data-test="code-back" :disabled="!nav.back" @click="back">←</button>
        <button type="button" class="icon" title="Forward" data-test="code-forward" :disabled="!nav.forward" @click="forward">→</button>
        <nav class="cb-crumbs" aria-label="Path" data-test="code-breadcrumb">
          <button type="button" class="cb-crumb" data-test="code-crumb-root" @click="openFolder('')">{{ member }}</button>
          <template v-for="c in crumbs" :key="c.path">
            <span class="cb-sep">/</span>
            <button type="button" class="cb-crumb" :data-test="`code-crumb-${c.path}`" @click="openFolder(c.path)">{{ c.name }}</button>
          </template>
          <template v-if="fileName"><span class="cb-sep">/</span><span class="cb-crumb file" data-test="code-crumb-file">{{ fileName }}</span></template>
        </nav>
        <span class="cb-grow" />
        <label class="cb-check"><input v-model="onlyChangedFiles" type="checkbox" data-test="code-only-changed" /> Only changed files</label>
        <label class="cb-check" title="Open whatever file the agent edits next"><input v-model="follow" type="checkbox" data-test="code-follow" /> Follow edits</label>
      </div>

      <div class="cb-panes">
        <div class="cb-side">
          <component :is="Loading" v-if="listLoading && !entries.length" text="Loading the folder…" />
          <p v-else-if="listError" class="error small" data-test="code-list-error">{{ listError }}</p>
          <FileList v-else :entries="entries" :now="nowMs" :only-changed="onlyChangedFiles" :selected="filePath" @open="openEntry" />
        </div>

        <div class="cb-main">
          <p v-if="!filePath" class="muted cb-plain" data-test="code-pick">Choose a file to preview it.</p>
          <template v-else>
            <div class="cb-filebar">
              <strong class="cb-filename">{{ fileName }}</strong>
              <span v-if="file" class="muted small">{{ formatSize(file.size) }}<template v-if="showsLines"> · {{ htmlLines.length }} lines</template></span>
              <span v-if="file?.linesUnknown" class="muted small" data-test="code-lines-unknown" title="Too many recent edits to keep the earlier text; the file's marker still shows">· changed lines not known</span>
              <span class="cb-grow" />
              <template v-if="showsLines && stops.length">
                <button type="button" data-test="code-jump-latest" :disabled="!latestLine" @click="jumpLatest">Jump to latest change</button>
                <button type="button" class="icon" title="Previous change" data-test="code-prev" @click="jumpPrevious">↑</button>
                <button type="button" class="icon" title="Next change" data-test="code-next" @click="jumpNext">↓</button>
              </template>
              <button v-if="file?.type === 'markdown'" type="button" data-test="code-source" :aria-pressed="showSource" @click="toggleSource">{{ showSource ? 'Formatted' : 'Source' }}</button>
              <button v-if="file && file.notShownReason !== 'secrets'" type="button" data-test="code-download" :disabled="downloading" :aria-busy="downloading" @click="download">
                <component :is="Busy" :busy="downloading" text="Downloading…">Download</component></button>
            </div>
            <p v-if="fileError" class="error small" data-test="code-file-error">{{ fileError }}</p>
            <component :is="Loading" v-if="fileLoading && !file" text="Loading the file…" />
            <template v-else-if="file">
              <button v-if="chip" type="button" class="cb-chip" data-test="code-updated-chip" @click="jumpChip">Updated: jump to change</button>
              <div v-if="file.type === 'notShown'" class="cb-notshown" data-test="code-not-shown">
                <p>{{ notShownText(file) }}</p>
                <button v-if="file.notShownReason !== 'secrets'" type="button" class="primary" data-test="code-not-shown-download" :disabled="downloading" @click="download">
                  <component :is="Busy" :busy="downloading" text="Downloading…">Download</component></button>
              </div>
              <div v-else-if="file.type === 'image'" class="cb-image" data-test="code-image">
                <!-- An <img>, never inline markup: an SVG's scripts never run. -->
                <img :src="imageSrc" :alt="fileName ?? ''" />
              </div>
              <!-- Rendered by the app's markdown-it with html disabled: raw HTML in the file is shown as text. -->
              <div v-else-if="file.type === 'markdown' && !showSource" class="cb-md md" data-test="code-markdown" v-html="markdownHtml" />
              <CodeLines v-else-if="showsLines" ref="lines" :lines="htmlLines" :decorations="decorations" :removed="removed" :target="target" />
            </template>
          </template>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.cb { display: flex; flex-direction: column; gap: 8px; height: min(72vh, 820px); min-height: 320px; }
.cb-plain { margin: 12px 0; }
.cb-bar, .cb-filebar { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
.cb-grow { flex: 1; }
.cb-crumbs { display: flex; align-items: center; gap: 2px; flex-wrap: wrap; min-width: 0; font-size: 13px; }
.cb-crumb { all: unset; cursor: pointer; color: var(--accent, #2457c5); padding: 1px 3px; border-radius: 3px; }
.cb-crumb:hover { text-decoration: underline; }
.cb-crumb.file { color: var(--ink, #1d2330); cursor: default; font-weight: 600; }
.cb-crumb.file:hover { text-decoration: none; }
.cb-sep { color: var(--muted, #6b7384); }
.cb-check { display: inline-flex; align-items: center; gap: 4px; font-size: 12.5px; white-space: nowrap; }
.cb-panes { display: grid; grid-template-columns: minmax(220px, 34%) minmax(0, 1fr); gap: 10px; flex: 1 1 0; min-height: 0; }
.cb-side { overflow: auto; border: 1px solid var(--line, #e2e5eb); border-radius: 6px; padding: 4px; min-height: 0; }
.cb-main { display: flex; flex-direction: column; gap: 6px; min-height: 0; min-width: 0; border: 1px solid var(--line, #e2e5eb); border-radius: 6px; padding: 6px 8px; position: relative; }
.cb-filename { font-size: 13px; }
.cb-chip { position: absolute; right: 18px; top: 44px; z-index: 3; font-size: 12px; border-radius: 999px; padding: 2px 10px; background: #fff;
  border: 1px solid color-mix(in srgb, var(--state-running, #2e7d4f) 50%, var(--line, #e2e5eb)); color: var(--state-running, #2e7d4f); box-shadow: 0 2px 8px rgba(0,0,0,.08); }
.cb-notshown { padding: 18px 8px; color: var(--ink-soft, #3d4555); }
.cb-image { overflow: auto; flex: 1 1 0; min-height: 0; }
.cb-image img { max-width: 100%; height: auto; background: repeating-conic-gradient(#f3f4f6 0 25%, #fff 0 50%) 0 0 / 16px 16px; }
.cb-md { overflow: auto; flex: 1 1 0; min-height: 0; font-size: 13.5px; line-height: 1.55; }
.cb-md :deep(.cb-img-alt) { color: var(--muted, #6b7280); font-style: italic; }
.small { font-size: 12px; }
/* highlight.js classes, in the app's quiet palette (no theme stylesheet is loaded). */
.cb :deep(.hljs-comment), .cb :deep(.hljs-quote) { color: #6a737d; font-style: italic; }
.cb :deep(.hljs-keyword), .cb :deep(.hljs-selector-tag), .cb :deep(.hljs-literal), .cb :deep(.hljs-doctag) { color: #a626a4; }
.cb :deep(.hljs-string), .cb :deep(.hljs-regexp), .cb :deep(.hljs-addition), .cb :deep(.hljs-meta .hljs-string) { color: #22863a; }
.cb :deep(.hljs-number), .cb :deep(.hljs-symbol), .cb :deep(.hljs-bullet), .cb :deep(.hljs-variable), .cb :deep(.hljs-template-variable) { color: #b76b01; }
.cb :deep(.hljs-title), .cb :deep(.hljs-section), .cb :deep(.hljs-name), .cb :deep(.hljs-selector-id), .cb :deep(.hljs-selector-class) { color: #2457c5; }
.cb :deep(.hljs-attr), .cb :deep(.hljs-attribute), .cb :deep(.hljs-property), .cb :deep(.hljs-type), .cb :deep(.hljs-built_in) { color: #986801; }
.cb :deep(.hljs-meta), .cb :deep(.hljs-tag) { color: #4078f2; }
.cb :deep(.hljs-deletion) { color: #b31d28; }
.cb :deep(.hljs-emphasis) { font-style: italic; }
.cb :deep(.hljs-strong) { font-weight: 600; }
</style>
