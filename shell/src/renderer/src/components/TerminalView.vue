<script setup lang="ts">
import { FitAddon } from '@xterm/addon-fit';
import { Terminal } from '@xterm/xterm';
import { onBeforeUnmount, onMounted, ref, watch } from 'vue';
import type { CoreEvent, YawbleApi, TerminalSessionInfo, TerminalSnapshot } from '../../../shared/api';
import { resolveApi } from '../api';
import { theme } from '../theme';
import { xtermTheme } from '../terminalPane';

// One terminal channel rendered with xterm.js: attach = replay the buffer, then stream pushes.
// Pushes carry "end" (bytes ever written), so bytes already in the replay are never written twice.
const props = defineProps<{ channel: string; api?: YawbleApi; rows?: number }>();
const emit = defineEmits<{ session: [TerminalSessionInfo | null]; text: [string] }>();
const api = resolveApi(props.api);

const host = ref<HTMLDivElement | null>(null);
const mirror = ref(''); // plain text of what was written: what the tests read, and the window's copy source
const MIRROR_MAX = 256 * 1024;
let term: Terminal | null = null;
let fit: FitAddon | null = null;
let end = -1; // -1: not attached yet; pushes are held until the replay is written
let held: CoreEvent[] = [];
let inputEnabled = false;
let lastStart: string | null = null;
const decoder = new TextDecoder();
let off: (() => void) | null = null;
let resizeObserver: ResizeObserver | null = null;

function bytes(b64: string): Uint8Array {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

function write(data: Uint8Array) {
  if (data.length === 0) return;
  term?.write(data);
  // eslint-disable-next-line no-control-regex
  const text = decoder.decode(data, { stream: true }).replace(/\x1b\[[0-9;?]*[A-Za-z]/g, '').replace(/\r/g, '');
  mirror.value = (mirror.value + text).slice(-MIRROR_MAX);
}

function output(data: { channel: string; data: string; end: number }) {
  if (end < 0) return;
  const chunk = bytes(data.data);
  const start = data.end - chunk.length;
  if (data.end <= end) return;
  write(start >= end ? chunk : chunk.subarray(end - start));
  end = data.end;
}

let sizedFor: string | null = null; // the live session the core last heard this terminal's size for

function session(s: TerminalSessionInfo | null) {
  inputEnabled = !!s && s.inputEnabled && !s.ended;
  if (term) term.options.cursorBlink = inputEnabled;
  // A session's PTY starts at a default size, and this terminal is already fitted (xterm raises no resize of its
  // own), so tell the core the real size once per live session; otherwise the CLI draws for the wrong size.
  if (s && !s.ended && term && s.startedAt !== sizedFor) {
    sizedFor = s.startedAt;
    void api.call('terminal.resize', { channel: props.channel, cols: term.cols, rows: term.rows }).catch(() => {});
  }
  emit('session', s);
}

async function attach() {
  end = -1;
  held = [];
  const snap = await api.call<TerminalSnapshot>('terminal.attach', { channel: props.channel });
  term?.reset();
  mirror.value = '';
  write(bytes(snap.replay));
  end = snap.end;
  lastStart = snap.session?.startedAt ?? null;
  session(snap.session);
  const pending = held;
  held = [];
  for (const e of pending) onEvent(e);
}

function onEvent(e: CoreEvent) {
  if (!e.data || e.data.channel !== props.channel) return;
  if (end < 0) {
    held.push(e);
    return;
  }
  if (e.event === 'terminal.output') output(e.data);
  else if (e.event === 'terminal.state') {
    const s = e.data.session as TerminalSessionInfo | null;
    // A host pane starts each command on a clean buffer: re-attach to drop the old output.
    if (s && s.kind === 'host' && !s.ended && s.startedAt !== lastStart) {
      void attach();
      return;
    }
    if (s) lastStart = s.startedAt;
    session(s);
  }
}

onMounted(async () => {
  term = new Terminal({
    convertEol: false,
    rows: props.rows ?? 24,
    fontFamily: 'ui-monospace, Menlo, Consolas, monospace',
    fontSize: 13,
    scrollback: 5000,
    theme: xtermTheme(theme.state.current),
  });
  fit = new FitAddon();
  term.loadAddon(fit);
  term.open(host.value!);
  try { fit.fit(); } catch { /* not laid out yet */ }
  term.onData((data) => {
    if (inputEnabled) void api.call('terminal.input', { channel: props.channel, data });
  });
  term.onResize(({ cols, rows }) => void api.call('terminal.resize', { channel: props.channel, cols, rows }).catch(() => {}));
  resizeObserver = new ResizeObserver(() => { try { fit?.fit(); } catch { /* ignore */ } });
  resizeObserver.observe(host.value!);
  off = api.onEvent(onEvent);
  await attach();
});

watch(() => props.channel, () => void attach());
// The theme switched (title-bar button, View menu, the OS): the terminal follows at once, its output kept.
watch(() => theme.state.current, (t) => { if (term) term.options.theme = xtermTheme(t); });
watch(mirror, (t) => emit('text', t));

onBeforeUnmount(() => {
  off?.();
  resizeObserver?.disconnect();
  term?.dispose();
});

defineExpose({
  focus: () => term?.focus(),
  /** The fitted size, for a session start (terminal.openInteractive). */
  size: () => (term ? { cols: term.cols, rows: term.rows } : null),
});
</script>

<template>
  <div class="terminal-view">
    <div ref="host" class="xterm-host" data-test="xterm"></div>
    <pre class="mirror" data-test="terminal-mirror" aria-hidden="true">{{ mirror }}</pre>
  </div>
</template>

<style scoped>
.terminal-view { position: relative; height: 100%; min-height: 120px; background: var(--term-bg); border-radius: 4px; padding: 4px; }
.xterm-host { height: 100%; }
.mirror { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); margin: 0; }
</style>
