<script setup lang="ts">
import { aDialogIsOpen, dismissesPanelOnEscape } from '../lib/escapeDismiss';
import { conciergeNewline } from '../lib/conciergeNewline';
import { conciergeKeyAction } from '../lib/conciergeKeystroke';
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import '@xterm/xterm/css/xterm.css';
import { connectConcierge, EvictedReason, type ConciergeSocket } from '../lib/concierge-socket';
import { firstPaintNudge } from '../lib/concierge-first-paint';
import { assignmentMoved } from '../lib/concierge-assignment';
import { conciergePreflight, type ConciergePreflight } from '../lib/conciergePreflight';
import {
  AttachableImageTypes,
  clipboardImage,
  dragCarriesFiles,
  droppedImages,
  imagePasteChordBytes,
  xtermSeesMac,
  isImagePasteChord,
  NotAnImage,
  pastedImage,
  refusedUploadSentence,
  type ImageClipboard,
} from '../lib/conciergeAttachment';
import ConciergeComposeBar from './ConciergeComposeBar.vue';
import ConciergeKeyBar from './ConciergeKeyBar.vue';
import TenantSettingsDialog from './TenantSettingsDialog.vue';
import {
  concierge as readConcierge,
  endConcierge,
  listCatalog,
  setConcierge,
  uploadConciergeAttachment,
} from '../api/client';
import { useConsoleStore } from '../stores/console';
import { useTerminalDisplayStore } from '../stores/terminalDisplay';
import { useWindowFrame } from '../lib/useWindowFrame';
import { agentsForMode, type ConciergeSettings, type TeamId } from '../api/types';

const props = defineProps<{
  team: TeamId | null;
  teamName?: string | undefined;
  activeTeam?: TeamId | null | undefined;
  activeTeamName?: string | undefined;
}>();
const open = defineModel<boolean>({ required: true });

/** The settings dialog, nested in this panel. Opening it does NOT touch the running session: what
 *  is saved reaches the next Concierge launched, and the child already attached keeps what
 *  it started with. */
const settingsOpen = ref(false);

/**
 * Ends the running agent and attaches a fresh one.
 *
 * DELETE then re-attach, in that order and with the socket torn down between: the server keys a
 * session on (team, person), so re-attaching before the old one has gone would find the SAME
 * session and change nothing - the reset would appear to do nothing at all, which is the failure
 * mode worth guarding rather than an error.
 *
 * Everything the agent knows dies with the process. That is the whole point of the control, and it
 * is why this asks first: a conversation is not recoverable, and the transcript replay that makes
 * re-attaching idempotent replays the OLD session, so there is nothing to go back to.
 */
const resetting = ref(false);

/**
 * THE INTERACTIVE PRESETS THIS CONCIERGE MAY BE POINTED AT.
 *
 * Through `agentsForMode(..., 'Interactive')`, the one picker chokepoint every other chooser
 * already uses - so a hidden preset stays filtered at RENDER and nothing here acquires its own
 * opinion about what is eligible. Loaded on OPEN rather than on mount, for the reason the terminal
 * is: a panel nobody has looked at should fetch nothing.
 *
 * Best effort. An empty picker is honest and leaves the rest of the header working.
 */
const conciergePresets = ref<string[]>([]);
const switchingAgent = ref(false);

async function loadConciergePresets() {
  try {
    const catalog = await listCatalog();
    conciergePresets.value = agentsForMode(catalog.agents, 'Interactive').map((a) => a.name);
  } catch {
    conciergePresets.value = [];
  }
}

/** What is chosen now, re-read at the two moments it can have changed from inside this panel. */
async function refreshChosen() {
  // A panel the pre-flight held back has nothing running to compare against: what it needs after
  // Settings closes is another try, which re-reads the setting itself.
  if (preflight.value.kind === 'blocked' && open.value) {
    await attach();
    return;
  }

  try {
    chosen.value = await readConcierge();
  } catch {
    // Left as it was. An unknown is not a change - see `assignmentMoved`, which answers false
    // rather than warning about a comparison it could not make.
  }
}

/**
 * POINT THE CONCIERGE AT ANOTHER PRESET AND RESTART IT ONTO THAT PRESET.
 *
 * ONLY `agent` IS SENT: it is the whole of the Concierge setting. What it is told is the built-in
 * Concierge prompt, chosen by role.
 *
 * SAVE FIRST, RESTART SECOND, AND NEVER RESTART ON A FAILED SAVE: restarting onto a setting that
 * did not store brings the session back on the OLD agent while the header names the NEW one - the
 * panel lying about what it is running, which is the one thing this header exists to prevent.
 */
async function onAgentPicked(agent: string) {
  if (!agent || agent === runningAgent.value) return;

  switchingAgent.value = true;

  try {
    await setConcierge({ agent });
  } catch (cause) {
    $q.notify({
      type: 'negative',
      message: cause instanceof Error ? cause.message : String(cause),
    });

    // The control is bound to what is RUNNING, so a failed save leaves the two in step by
    // construction rather than by putting the value back by hand here.
    switchingAgent.value = false;
    return;
  }

  try {
    // The setting takes effect on the next open, so a switch that did not restart would appear to
    // do nothing at all. `reset` re-reads the snapshot on its way back up.
    await reset();
  } finally {
    switchingAgent.value = false;
  }
}

async function reset() {
  resetting.value = true;

  try {
    // Torn down first so nothing is listening when the process dies - otherwise the socket's own
    // close handler paints a disconnection notice for a session we ended on purpose.
    detach();

    await endConcierge();

    await nextTick();
    await attach();

    $q.notify({ type: 'positive', message: 'A fresh Concierge is starting.', timeout: 3000 });
  } catch (cause) {
    $q.notify({
      type: 'negative',
      message: cause instanceof Error ? cause.message : String(cause),
    });

    // Re-attached even on failure: the old session may or may not still be there, and a panel with
    // no terminal in it is worse than one showing whatever is actually running.
    await attach();
  } finally {
    resetting.value = false;
  }
}

/**
 * A q-dialog in this template, NOT `$q.dialog()`.
 *
 * The plugin's `class` option is applied to the inner CARD rather than to the dialog root, so there
 * is no way to lift it above `.concierge-shell`'s z-index of 7000 - and the confirmation
 * for a DESTRUCTIVE action would render behind the terminal, invisible.
 */
const confirmOpen = ref(false);

const $q = useQuasar();
const board = useConsoleStore();
const display = useTerminalDisplayStore();

const host = ref<HTMLDivElement>();

/**
 * What the RUNNING session was launched with, captured when this panel attached.
 *
 * SNAPSHOT, NEVER A LIVE BINDING, and that is the point of it. The settings dialog is opened from
 * this very toolbar, and what it saves reaches the NEXT Concierge - the child already
 * attached keeps what it started with. A subtitle bound to the team's row would change the instant
 * Save was pressed, over a terminal still running the old Agent, and read as the
 * change having taken effect.
 *
 * Re-taken on every attach, so Reload - which really does relaunch - updates it.
 */
const running = ref<ConciergeSettings | null>(null);

/**
 * WHAT IS CHOSEN NOW, as opposed to what the attached child was launched with.
 *
 * Two refs rather than one, because they answer different questions and `moved` exists precisely
 * for the moments they disagree. Refreshed when the panel opens and whenever the settings dialog
 * CLOSES - the only two moments it can have changed from in here. Not polled: a value that drifts
 * on a timer would make the warning appear and vanish while nobody touched anything.
 */
const chosen = ref<ConciergeSettings | null>(null);

/**
 * WHAT THE PANEL SAYS BEFORE THE TERMINAL, from the same read as `running` - see
 * `lib/conciergePreflight.ts`. `blocked` means no socket was opened, so there is no close reason
 * for anybody to read: the sentence and its link are the whole of what the panel shows.
 */
const preflight = ref<ConciergePreflight>({ kind: 'ready' });

/** The header's Agent, from what the launcher resolved when the server says so, and from the
 *  stored choice otherwise. A default is still what is running, so it is what the header names. */
const runningAgent = computed(() =>
  running.value?.effective ? running.value.effective.agent : running.value?.agent ?? null);

// NOT named `team`: the prop is called that, and a computed shadowing it in the template is how
// a heading starts rendering [object Object]. The same class of mistake as `activeTeamName`
// holding an id - a name that says the wrong thing about its contents.
const teamRow = computed(() =>
  props.team == null ? undefined : board.teams.find((candidate) => candidate.id === props.team));
const activeTeamLabel = computed(() => props.activeTeamName || props.activeTeam || '');

// No "launched on team X" label or mismatch warning: the agent resolves its current team per
// command, so there is no spawn-time team for the header to disagree with.

/** The Concierge's Agent has been changed since this session launched, so the subtitle is
 *  describing a terminal that Reload would replace. */
const moved = computed(() => assignmentMoved(running.value, chosen.value));

/** Windowed at 600px and above (lt.sm breakpoint), full screen below. Maximised overrides to full. */
const isWindowed = computed(() => !display.maximised && !$q.screen.lt.sm);

let terminal: Terminal | null = null;
let fit: FitAddon | null = null;
let socket: ConciergeSocket | null = null;

/**
 * Deliberately NOT a QDialog.
 *
 * A maximized QDialog owns its own geometry, and on a phone that geometry is the problem: the card
 * has to be sized to the visual viewport (the keyboard shrinks only that) while the dialog's inner
 * container is still the full layout height, so a short card ends up floating inside it with the
 * page visible above and the keyboard covering below. Two attempts to correct that from the outside
 * failed, so the console positions itself instead.
 *
 * This is a considered exception to "use the framework component", not a shortcut: what is being
 * hand-rolled is one fixed-position box, and what it buys is the only geometry a phone terminal can
 * actually work in.
 */
const viewport = ref({ height: window.innerHeight, offsetTop: 0 });

let pendingFrame = 0;

/**
 * Coalesced to one measurement per frame, and a no-op when nothing moved.
 *
 * `visualViewport` emits `scroll` continuously while the keyboard animates, and each refit reflows
 * the terminal, which emits another event. Unthrottled that is a feedback loop, and it presents as
 * the panel jittering.
 */
function trackViewport() {
  if (pendingFrame) return;

  pendingFrame = requestAnimationFrame(() => {
    pendingFrame = 0;

    const visual = window.visualViewport;
    const height = visual ? visual.height : window.innerHeight;
    const offsetTop = visual ? visual.offsetTop : 0;

    if (height === viewport.value.height && offsetTop === viewport.value.offsetTop) return;

    viewport.value = { height, offsetTop };
    refit();
  });
}

/** Rotation reports its new dimensions late, so one deferred pass after the event settles. */
function onOrientationChange() {
  setTimeout(trackViewport, 200);
}

/**
 * Refit whenever the terminal's own box changes, whatever caused it.
 *
 * Listening to viewport events means guessing which ones move the box, and the box can grow after
 * the last fit - the key bar rendering, fonts settling - with nothing re-measuring, leaving the
 * terminal at a fraction of its space with a dead band underneath. An observer on the element
 * cannot miss a cause it was not told about.
 */
let boxObserver: ResizeObserver | null = null;
let pendingRefit = 0;

/**
 * Refit on the next frame at most once.
 *
 * `fit()` resizes the terminal from inside a ResizeObserver callback, and an unthrottled observer
 * that reacts to its own effect is how you get "ResizeObserver loop completed with undelivered
 * notifications" and a visibly stuttering panel.
 */
function scheduleRefit() {
  if (pendingRefit) return;

  pendingRefit = requestAnimationFrame(() => {
    pendingRefit = 0;
    refit();
  });
}

/**
 * Drag-to-scroll, because xterm does not get it for free on touch.
 *
 * The scrollable element is `.xterm-viewport`, but `.xterm-screen` renders on top of it and is what
 * a finger actually lands on - so the drag hits a layer that does not scroll. The scrollbar works
 * because it IS the viewport; dragging the text does not. Translating the gesture into scrollLines
 * is the fix.
 */
let touchY = 0;
let touchRemainder = 0;

function onTouchStart(event: TouchEvent) {
  touchY = event.touches[0]?.clientY ?? 0;
  touchRemainder = 0;
}

function onTouchMove(event: TouchEvent) {
  if (!terminal || !host.value) return;

  // UNCONDITIONALLY, and before any early return. iOS commits to its own scrolling on the first
  // unprevented touchmove of a gesture, so preventing only once the drag has travelled far enough
  // to move a whole row is already too late - which is exactly why dragging did nothing while the
  // scrollbar worked. `touch-action: none` on the host says the same thing declaratively; this is
  // the belt to that pair of braces.
  event.preventDefault();

  const y = event.touches[0]?.clientY ?? touchY;
  const moved = touchY - y;
  touchY = y;

  // Accumulate sub-row movement rather than discarding it, or a slow drag never scrolls at all.
  touchRemainder += moved;

  const rowHeight = host.value.clientHeight / Math.max(terminal.rows, 1);
  const rows = Math.trunc(touchRemainder / Math.max(rowHeight, 1));

  if (rows === 0) return;

  touchRemainder -= rows * rowHeight;
  terminal.scrollLines(rows);
}

/**
 * A key from the virtual bar, sent as ordinary input.
 *
 * A function rather than touching `socket` in the template, which narrows it to `never`. It also
 * keeps the framing rule in one place: these are keystrokes, so BINARY frames, never control JSON.
 */
function sendKey(sequence: string) {
  socket?.sendInput(sequence);
}

/**
 * AN IMAGE FOR THE CONCIERGE: UPLOADED, THEN ITS PATH TYPED AT THE PROMPT. See
 * `lib/conciergeAttachment.ts` for why the page does this rather than any CLI's own image key, and
 * which browsers reach which of the four ways in.
 *
 * `terminal` pastes the path through xterm's own `paste`, exactly as if the person had pasted it as
 * text: bracketed when the CLI turned bracketed-paste mode on, bare when it did not. Wrapping it here
 * regardless sent a CLI that never asked for the markers a literal `ESC[200~` as keystrokes;
 * `compose` puts it in the compose bar at its caret. Paste and Alt+V happen IN the terminal, so they
 * answer there; the button and a drop go to the compose bar where there is one, because on a
 * phone that is where a line is written.
 *
 * A FAILED UPLOAD INSERTS NOTHING and says why in the panel, in the server's own sentence - a 409
 * for no CLI running included (see `refusedUploadSentence`). Several
 * files go in order and stop at the first refusal, so what was inserted is what the line says
 * succeeded.
 */
const attaching = ref(false);
const attachError = ref<string | null>(null);
const composeBar = ref<InstanceType<typeof ConciergeComposeBar>>();
const picker = ref<HTMLInputElement>();

type AttachTarget = 'terminal' | 'compose';

function defaultAttachTarget(): AttachTarget {
  return composeBar.value ? 'compose' : 'terminal';
}

async function attachImages(files: File[], target: AttachTarget = defaultAttachTarget()) {
  if (files.length === 0) return;

  attaching.value = true;
  attachError.value = null;

  try {
    for (const [index, file] of files.entries()) {
      let path: string;

      try {
        path = (await uploadConciergeAttachment(file)).path;
      } catch (cause) {
        attachError.value = refusedUploadSentence(cause);
        return;
      }

      // A space between paths, so two files are two words at the prompt and not one.
      const inserted = index > 0 ? ` ${path}` : path;

      if (target === 'compose' && composeBar.value) {
        await composeBar.value.insert(inserted);
      } else if (socket && terminal) {
        terminal.paste(inserted);
        terminal.focus();
      } else {
        attachError.value = 'The image was stored, but no Concierge is running to receive its path.';
        return;
      }
    }
  } finally {
    attaching.value = false;
  }
}

/**
 * A paste into the terminal. CAPTURE PHASE on the host, so it runs before xterm's own listener on
 * its textarea - which would forward the text half and drop the image. A text paste is left alone.
 */
function onTerminalPaste(event: ClipboardEvent) {
  const image = pastedImage(event.clipboardData);
  if (!image) return;

  event.preventDefault();
  event.stopPropagation();
  void attachImages([image], 'terminal');
}

/**
 * Alt+V, after the key handler has already swallowed it. The clipboard is read once: an image is
 * uploaded, and anything else - text, nothing, no API, a refused permission - sends the key on
 * exactly as xterm would have, so a CLI that binds Alt+V itself still gets it.
 */
async function onImagePasteChord(chord: { key: string; shiftKey: boolean }) {
  const image = await clipboardImage(navigator.clipboard as ImageClipboard | undefined);

  if (image) {
    await attachImages([image], 'terminal');
    return;
  }

  const bytes = imagePasteChordBytes(chord, xtermSeesMac(navigator.platform));
  if (bytes) socket?.sendInput(bytes);
}

function onShellDragOver(event: DragEvent) {
  // Without this the browser refuses the drop - and opens the file in place of the app.
  if (dragCarriesFiles(event.dataTransfer)) event.preventDefault();
}

function onShellDrop(event: DragEvent) {
  if (!dragCarriesFiles(event.dataTransfer)) return;
  event.preventDefault();

  const images = droppedImages(event.dataTransfer);

  if (images.length === 0) {
    attachError.value = NotAnImage;
    return;
  }

  void attachImages(images);
}

function onPicked(event: Event) {
  const input = event.target as HTMLInputElement;
  const files = Array.from(input.files ?? []);

  // Cleared so picking the same file again is still a change.
  input.value = '';
  void attachImages(files);
}

function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape') return;

  // Both rules live in `lib/escapeDismiss.ts` so a spec can reach them - see that file for why a
  // dialog open above the panel has to be one of them.
  const dismisses = dismissesPanelOnEscape({
    focusInTerminal: host.value?.contains(document.activeElement) ?? false,
    dialogOpen: aDialogIsOpen(),
  });

  if (dismisses) open.value = false;
}

/**
 * ONCE PER ATTACH, MAKE THE CHILD REPAINT AT THE SIZE THE BROWSER IS ACTUALLY SHOWING.
 *
 * The child prints its banner the instant it starts, and a TUI repaints its whole screen only when
 * the size CHANGES - the size it already holds is a no-op to it. So where its idea of the width and
 * ours disagree, the first paint stays wrong until a person resizes the panel by hand. This does
 * that resize for them: one column narrower, then back, on the next frame.
 *
 * SEE `firstPaintNudge` FOR WHY THIS IS HANDLING AND NOT A DIAGNOSIS. The cause of the
 * disagreement is not known. Deliberately AFTER `refit()` in `onOpen`, so the size it restores is the one that refit has
 * just settled on rather than a stale measurement.
 *
 * Separated in time rather than sent in the same tick: back to back the child can coalesce the two
 * into no change at all, which is the no-op this exists to avoid.
 *
 * A TIMER AND NOT `requestAnimationFrame`, AND THAT IS THE WHOLE SAFETY OF IT. rAF does not fire in
 * a hidden tab - Chrome suspends it - so a person who opened the panel and switched tab would have
 * had the shrink delivered and the restore never, leaving the child permanently a column short:
 * a scrambled first paint traded for a wrong width that nothing would ever correct. Caught in the
 * browser on the first run of this fix, where the restore frame simply never appeared. A timeout is
 * throttled in a background tab, not cancelled.
 */
function nudgeFirstPaint() {
  if (!terminal || !socket) return;

  const nudge = firstPaintNudge({ cols: terminal.cols, rows: terminal.rows });

  if (!nudge) return;

  const [smaller, real] = nudge;

  socket.sendResize(smaller.cols, smaller.rows);
  window.setTimeout(() => socket?.sendResize(real.cols, real.rows), 50);
}

function refit() {
  if (!terminal || !fit) return;

  const before = `${terminal.cols}x${terminal.rows}`;

  // fit() cannot throw: proposeDimensions() early-returns undefined when the element has no usable
  // box, leaving cols/rows at whatever they were. So a refit while hidden is a no-op.
  fit.fit();

  if (`${terminal.cols}x${terminal.rows}` !== before) {
    socket?.sendResize(terminal.cols, terminal.rows);

    // Force a full repaint after a size change. A keyboard animation resizes the terminal through
    // many intermediate sizes, and the renderer leaves fragments of the old layout behind - which
    // reads as output repeating itself. Minimising and reopening "fixed" it only because a fresh
    // terminal paints from scratch.
    terminal.refresh(0, terminal.rows - 1);
  }
}

/**
 * Window drag/resize: the shared pointer half (`lib/useWindowFrame`), over the display store's
 * geometry. Every step is clamped to the viewport and remembered by the store.
 */
const frame = useWindowFrame({
  geometry: () => ({ left: display.left, top: display.top, width: display.width, height: display.height }),
  apply: (next) => {
    display.set(next);
    display.clampToViewport(
      window.visualViewport?.width ?? window.innerWidth,
      window.visualViewport?.height ?? window.innerHeight,
    );
  },
  isWindowed: () => isWindowed.value,
  shellSelector: '.concierge-shell',
});

async function refresh() {
  if (!terminal || !fit || !socket) return;

  // A: Measure before, fit, then refresh unconditionally
  const before = `${terminal.cols}x${terminal.rows}`;
  fit.fit();
  terminal.refresh(0, terminal.rows - 1);

  // Only send resize if dimensions actually changed
  if (`${terminal.cols}x${terminal.rows}` !== before) {
    socket.sendResize(terminal.cols, terminal.rows);
  }

  // B: Detach and re-attach same session
  detach();
  await nextTick();
  await attach();
}

async function attach() {
  if (terminal) return;

  if (!host.value) {
    console.error('[console] opened but the terminal host element is not in the DOM');
    return;
  }

  // Check if host has usable dimensions. If not, set up observer to retry.
  if (host.value.clientWidth < 1 || host.value.clientHeight < 1) {
    if (!boxObserver) {
      boxObserver = new ResizeObserver(() => {
        if (host.value && host.value.clientWidth >= 1 && host.value.clientHeight >= 1) {
          // Host now has usable box, try attaching again
          if (!terminal) {
            // Disconnect this observer and retry attach
            boxObserver?.disconnect();
            boxObserver = null;
            void attach();
          }
        }
      });
      boxObserver.observe(host.value);
    }
    return;
  }

  // Taken HERE rather than watched, because this is the moment a child is launched with it. Below
  // the early returns above for the same reason: a panel that failed to attach has nothing running
  // to describe.
  //
  // FROM THE TENANT SETTING, NEVER A TEAM ROW. The Concierge is chosen for the instance and names
  // no team; a team's `concierge` column records what was chosen when that team was made, which is
  // a different fact and is absent entirely when the panel has no team.
  //
  // Best effort, and it must not stop the terminal attaching: having the Concierge matters more
  // than being able to name it, so a failed read leaves the subtitle absent and the session up.
  try {
    running.value = await readConcierge();
    chosen.value = running.value;
  } catch {
    running.value = null;
  }

  // PRE-FLIGHT, BEFORE ANY TERMINAL OR SOCKET EXISTS. A launch the server already knows cannot
  // start is said here in words, rather than opened and left to print its close reason.
  preflight.value = conciergePreflight(running.value);

  if (preflight.value.kind === 'blocked') return;

  startSignInRecheck();

  // Re-checked after the await: the panel may have attached, or been closed, while the read was out.
  if (terminal || !open.value || !host.value) return;

  // Read font settings from store (clamped)
  const fontFamily = display.fontFamily;
  const fontSize = display.fontSize;

  // NO THEME, and no repaint when the console's theme changes. The agent's CLI draws for a
  // terminal's default palette, so xterm's own colours are the ones it was designed against.
  terminal = new Terminal({
    fontFamily,
    fontSize,
  });

  fit = new FitAddon();
  terminal.loadAddon(fit);
  terminal.open(host.value);

  // Load font if Plex is chosen, and await it before first fit
  if (fontFamily.includes('Plex')) {
    await document.fonts.load(`${fontSize}px "${fontFamily.split('"')[1]}"`);
    await document.fonts.ready;
  }

  fit.fit();

  terminal.write('\x1b[90mattaching...\x1b[0m\r\n');

  // Measured BEFORE connecting, and sent with the connect: the server spawns the child at this size
  // rather than at a default it would then have to be resized away from. See connectConcierge.
  socket = connectConcierge({ cols: terminal.cols, rows: terminal.rows }, {
    onOpen: () => {
      // The server replays the transcript before the live tail, so keeping what is already on screen
      // would double it. This is what makes reattach idempotent.
      terminal?.clear();
      refit();
      nudgeFirstPaint();
    },

    onData: (bytes) => terminal?.write(bytes),

    onClose: (reason) => {
      // Losing the terminal to another device is not the child exiting, and the difference matters:
      // the session is still there and reopening this panel takes it back. Saying "ended" here would
      // send someone looking for a crash that did not happen.
      //
      // The panel closes ITSELF rather than sitting there looking live. A detached terminal shows
      // nothing new no matter what is typed on the other device, and a frozen-looking window is a
      // worse explanation than no window: otherwise the only sign it had gone stale is having to
      // minimise and maximise to get anything. The notification carries the
      // reason, because a panel that vanishes silently is its own puzzle.
      if (reason === EvictedReason) {
        $q.notify({
          type: 'info',
          message: 'Your Concierge moved to another device.',
          caption: 'Reopen it here to take it back.',
          timeout: 6000,
        });

        open.value = false;
        return;
      }

      // A close carrying a reason is the child exiting, and saying so out loud matters: an empty
      // terminal looks identical to one that never started.
      terminal?.write(`\r\n\x1b[33m-- Concierge ended${reason ? `: ${reason}` : ''} --\x1b[0m\r\n`);

      // And the board behind it is now suspect. A session ending is usually the child exiting on its
      // own, but it is ALSO what a person on another device sees when the team is DELETED out from
      // under them - and then every card, the team switcher and the ribbon are describing something
      // that is gone. Refreshing is what turns that into "no teams yet" without a manual reload.
      //
      // Here rather than on close, because closing the panel is the ORDINARY case: it detaches, the
      // session keeps running, and re-fetching the board every time somebody minimises a terminal is
      // noise. A session that ENDED is the unusual one, and the only one where the state behind the
      // panel may have moved without this client hearing about it.
      void board.refresh();
    },
  });

  terminal.onData((data) => socket?.sendInput(data));

  /**
   * SHIFT+ENTER INSERTS A NEWLINE INSTEAD OF SUBMITTING, FOR WHATEVER AGENT THIS CONCIERGE RUNS.
   *
   * There is no distinct VT encoding for that chord, so xterm emits the same CR for it as for a
   * bare Enter and the modifier never survives the trip to the PTY. That is not a defect here — it
   * is the reason Claude Code ships `/terminal-setup` at all, which exists to teach iTerm2 and
   * VS Code to send ESC CR instead. This is the same teaching, done once for the panel rather than
   * once per person per terminal.
   *
   * THERE ARE TWO LOAD-BEARING HALVES. Returning false stops xterm HANDLING the event — and in
   * doing so stops xterm calling `preventDefault()` on it, so the browser's default action still
   * delivers the key to xterm's hidden textarea, which emits a CR of its own. Without the explicit
   * cancel, one press puts the escape sequence AND a bare CR on the socket, and the CR submits the
   * line before the CLI can treat the sequence as a newline.
   *
   * So the handler cancels the default ITSELF. `preventDefault` stops the textarea; the false return
   * stops xterm. Dropping either reinstates the bug.
   *
   * WHICH sequence is `conciergeNewline`'s decision, and WHAT TO DO ABOUT THE KEYSTROKE is
   * `conciergeKeyAction`'s — both live outside this closure so a spec can pin each rule without
   * mounting the whole panel.
   *
   * NOTHING ABOUT THE TERMINAL'S MODE IS READ HERE. The sequence does not depend on
   * `bracketedPasteMode`: all four presets read `ESC CR`, and the bracketed form SUBMITS in two of
   * them. See that file.
   */
  const attached = terminal;

  attached.attachCustomKeyEventHandler((event) => {
    // Alt+V is the page's, never xterm's: cancelled and swallowed here, and sent on later by
    // `onImagePasteChord` when the clipboard turns out to hold no image.
    if (isImagePasteChord(event)) {
      event.preventDefault();
      void onImagePasteChord({ key: event.key, shiftKey: event.shiftKey });
      return false;
    }

    const action = conciergeKeyAction(event, conciergeNewline());

    // BEFORE the send and before the return, because it is the only one of the three that has a
    // deadline: once this handler returns, the browser's default action is already on its way.
    if (action.preventDefault) event.preventDefault();

    if (action.send !== null) socket?.sendInput(action.send);

    return !action.swallow;
  });

  window.addEventListener('resize', trackViewport);
  window.addEventListener('orientationchange', onOrientationChange);
  window.visualViewport?.addEventListener('resize', trackViewport);

  // Not redundant with resize: some browsers report a keyboard opening as a viewport SCROLL.
  window.visualViewport?.addEventListener('scroll', trackViewport);

  // The authority on when to refit. Everything above only changes the viewport; this fires whenever
  // the terminal's actual box changes, for any reason, including ones not anticipated here.
  boxObserver = new ResizeObserver(scheduleRefit);
  boxObserver.observe(host.value);

  host.value.addEventListener('touchstart', onTouchStart, { passive: true });
  host.value.addEventListener('paste', onTerminalPaste, true);

  // passive: false, or preventDefault is ignored and the page scrolls instead of the scrollback.
  host.value.addEventListener('touchmove', onTouchMove, { passive: false });

  trackViewport();
  refit();
}

/**
 * WHILE THE AGENT IS NOT SIGNED IN, ASK AGAIN. The person signs in inside this very terminal, so
 * the notice above it must not outlive the sign-in until the panel is reopened. The server caches
 * its sign-in probe for 30 seconds, so a sign-in shows within that and one interval. It stops at
 * the first signed-in answer, and whenever the panel detaches (closed, reset, unmounted).
 */
const SIGN_IN_RECHECK_MS = 15_000;
let signInRecheck: ReturnType<typeof setInterval> | null = null;

function stopSignInRecheck() {
  if (signInRecheck !== null) clearInterval(signInRecheck);
  signInRecheck = null;
}

function startSignInRecheck() {
  stopSignInRecheck();
  const agent = running.value?.effective?.agent;
  if (preflight.value.kind !== 'notice' || !agent || running.value?.effective?.auth.signedIn !== false) return;

  signInRecheck = setInterval(async () => {
    let fresh: ConciergeSettings;
    try {
      fresh = await readConcierge();
    } catch {
      return; // unknown is not signed in; the next tick asks again
    }
    // Only the agent this session runs: another choice takes effect through a restart, not here.
    if (signInRecheck === null || fresh.effective?.agent !== agent || !fresh.effective.auth.signedIn) return;
    stopSignInRecheck();
    running.value = fresh;
    preflight.value = conciergePreflight(fresh);
  }, SIGN_IN_RECHECK_MS);
}

function detach() {
  stopSignInRecheck();
  window.removeEventListener('resize', trackViewport);
  window.removeEventListener('orientationchange', onOrientationChange);
  window.visualViewport?.removeEventListener('resize', trackViewport);
  window.visualViewport?.removeEventListener('scroll', trackViewport);

  boxObserver?.disconnect();
  boxObserver = null;

  host.value?.removeEventListener('touchstart', onTouchStart);
  host.value?.removeEventListener('touchmove', onTouchMove);
  host.value?.removeEventListener('paste', onTerminalPaste, true);

  socket?.dispose();
  socket = null;

  terminal?.dispose();
  terminal = null;
  fit = null;
}

/**
 * Attached on first open rather than on mount: a console is a child process, and one should not be
 * spawned for a team nobody has looked at.
 *
 * Body scroll lock is only for full screen (maximised, mobile, or full viewport): windowed panels
 * must not lock, so the board behind can scroll.
 */
function updateBodyOverflow() {
  const shouldLock = !isWindowed.value;
  document.body.style.overflow = shouldLock ? 'hidden' : '';
}

watch(open, async (isOpen) => {
  if (isOpen) {
    updateBodyOverflow();
    // Initialize window geometry from viewport if needed
    const viewportW = window.visualViewport?.width ?? window.innerWidth;
    const viewportH = window.visualViewport?.height ?? window.innerHeight;
    display.initializeGeometryIfNeeded(viewportW, viewportH);
    document.addEventListener('keydown', onKeydown);
    void loadConciergePresets();
    await nextTick();
    await attach();
  } else {
    document.body.style.overflow = '';
    document.removeEventListener('keydown', onKeydown);
    detach();
  }
});

// Watch for breakpoint/maximised changes while panel is open - update overflow and geometry
watch([() => $q.screen.lt.sm, () => display.maximised], () => {
  if (!open.value) return;
  updateBodyOverflow();
  trackViewport();
});

// Watch display store for font changes - apply live
watch([() => display.fontFamily, () => display.fontSize], () => {
  if (!terminal) return;
  terminal.options.fontFamily = display.fontFamily;
  terminal.options.fontSize = display.fontSize;
  scheduleRefit();
});

onBeforeUnmount(() => {
  document.body.style.overflow = '';
  document.removeEventListener('keydown', onKeydown);
  detach();
});
</script>

<template>
  <!-- Teleported to body so no ancestor's overflow, transform or stacking context can clip or
       re-anchor a fixed element. A transform on an ancestor silently makes `fixed` behave like
       `absolute`, which is the sort of thing that costs an afternoon. -->
  <Teleport to="body">
    <div
      v-if="open"
      class="concierge-shell column no-wrap bg-dark text-white"
      :class="{ 'is-windowed': isWindowed }"
      @dragover="onShellDragOver"
      @drop="onShellDrop"
      :style="
        isWindowed
          ? {
              left: display.left + 'px',
              top: display.top + 'px',
              width: display.width + 'px',
              height: display.height + 'px',
            }
          : {
              left: '0',
              right: '0',
              top: viewport.offsetTop + 'px',
              height: viewport.height + 'px',
            }
      "
    >
      <q-toolbar class="concierge-bar bg-dark" @pointerdown="frame.onHandlePointerDown">
        <q-toolbar-title class="concierge-heading">
          <div class="concierge-title ellipsis">
            Concierge
          </div>

          <!-- What this terminal IS, under what it is. The panel is otherwise anonymous: a person
               who opens two teams' Concierges, or comes back to one an hour later, has no
               way to tell what is behind the prompt without opening Settings - which is a dialog
               about CHANGING it, so reading is done through the control that edits. The same
               vernacular as a member's card: a lowercase label, the value in mono. -->
          <div v-if="running" class="concierge-subtitle ellipsis">
            <!-- THE AGENT IS A CONTROL RATHER THAN A LABEL, and it sits where a label would so the
                 line still reads left to right as what this terminal IS. Bound to what is RUNNING,
                 never to what is chosen - see `running`'s own note on why that is a snapshot.

                 `@pointerdown.stop` IS LOAD-BEARING: this toolbar is the window's drag handle, so
                 without it every attempt to open the picker drags the panel instead.

                 `popup-content-class` LIKEWISE: the shell sits at z-index 7000 and a QSelect's menu
                 teleports to <body> separately from the panel it lives in, so an unlifted dropdown
                 paints BEHIND - which renders as a select that expands with nothing beneath it, and
                 reads as "there is only one option" rather than as a layering fault. -->
            <q-select
              :model-value="runningAgent"
              :options="conciergePresets"
              :disable="switchingAgent || resetting"
              dense
              dark
              borderless
              hide-bottom-space
              class="concierge-agent-pick mono"
              popup-content-class="concierge-agent-menu"
              aria-label="Which Agent this Concierge runs"
              @pointerdown.stop
              @update:model-value="onAgentPicked"
            />

          </div>

          <!-- THE ACTIVE TEAM, WITH NO "this session stays on" CLAUSE, because a session is not
               pinned to a team. The agent resolves its current team per command, so what this line
               says and what the agent acts on are the same fact rather than two that could
               disagree. -->
          <div v-if="activeTeamLabel" class="concierge-active-team ellipsis">
            active team <span class="mono">{{ activeTeamLabel }}</span>
          </div>
          <div v-else class="concierge-active-team ellipsis">
            <!-- A REAL STATE, given words rather than left blank: on the Teams overview no team is
                 active, the agent is told so, and it asks which team rather than guessing. -->
            no active team
          </div>
        </q-toolbar-title>

        <!-- Only while the two actually disagree, and it names the control that closes the gap
             rather than the state. Reload is the word on the button beside it and in its tooltip;
             a second word for one action is how somebody ends up looking for a Restart. -->
        <q-badge
          v-if="moved"
          class="concierge-moved q-mr-sm"
          color="warning"
          text-color="black"
          label="reload to apply"
        >
          <q-tooltip>
            The Concierge's Agent changed after this session started. The running agent keeps
            what it was launched with until it is reloaded.
          </q-tooltip>
        </q-badge>

        <!-- A file picker, because it is the one way in every browser and every phone has. The
             input is never shown; what it picks goes to the upload and is never rendered. -->
        <q-btn
          flat
          dense
          round
          icon="add_photo_alternate"
          aria-label="Attach image"
          :loading="attaching"
          @click="picker?.click()"
        >
          <q-tooltip>Attach image — its path is pasted at the prompt</q-tooltip>
        </q-btn>
        <input
          ref="picker"
          type="file"
          :accept="AttachableImageTypes"
          multiple
          hidden
          aria-hidden="true"
          tabindex="-1"
          @change="onPicked"
        />

        <!-- Refresh: paint then detach+attach without DELETE, does not restart the agent. A SCREEN
             for an icon, not a circular arrow: Reload beside it is the arrow, and two arrows side
             by side read as the same button. -->
        <q-btn
          flat
          dense
          round
          icon="desktop_windows"
          aria-label="Redraw the screen — the agent keeps running"
          @click="refresh"
        >
          <q-tooltip>Redraw the screen — the agent keeps running</q-tooltip>
        </q-btn>

        <!-- Reset is DESTRUCTIVE and asks first - see confirmReset. It sits left of Settings so
             the two irreversible-looking controls are not adjacent to Detach, which is the safe
             one. -->
        <q-btn
          flat
          dense
          round
          icon="restart_alt"
          aria-label="Reload the Concierge"
          :loading="resetting"
          @click="confirmOpen = true"
        >
          <q-tooltip>Reload the Concierge — starts a new agent, which keeps its notes on your teams</q-tooltip>
        </q-btn>

        <!-- Maximise toggle: visible whenever not lt.sm (≥600px), even when maximised. Shows as 'fullscreen' when not maximised, 'fullscreen_exit' when maximised. -->
        <q-btn
          v-if="!$q.screen.lt.sm"
          flat
          dense
          round
          :icon="display.maximised ? 'fullscreen_exit' : 'fullscreen'"
          :aria-label="display.maximised ? 'Restore' : 'Maximize'"
          @click="display.set({ maximised: !display.maximised })"
        >
          <q-tooltip>{{ display.maximised ? 'Restore' : 'Maximize' }}</q-tooltip>
        </q-btn>

        <!-- Settings BESIDE the detach button, and deliberately not inside the terminal: every
             keystroke in there belongs to the child process, so a control that lives in the grid
             would be one the agent could not tell from input. -->
        <q-btn flat dense round icon="settings" aria-label="Concierge settings" @click="settingsOpen = true">
          <q-tooltip>Concierge settings: Agent and this browser's display</q-tooltip>
        </q-btn>

        <!-- Detach, never kill. Closing must not end the session behind it. -->
        <q-btn flat dense round icon="remove" aria-label="Detach" @click="open = false">
          <q-tooltip>Detach — the session keeps running</q-tooltip>
        </q-btn>
      </q-toolbar>

      <!-- Nested here rather than in MainLayout beside the ribbon's copy: this one is scoped to the
           panel that is open, and the two never show at once. A change made here reaches the NEXT
           session opened - the running child keeps what it was launched with, which is why the
           dialog says so rather than implying a restart. -->
      <!-- `chosen` is re-read on CLOSE, not on save: the dialog owns its own saving, and this only
           needs to know afterwards so `moved` can warn that Reload would replace the session. -->
      <TenantSettingsDialog
        v-model="settingsOpen"
        initial-tab="concierge"
        @update:model-value="(v: boolean) => { if (!v) void refreshChosen(); }"
      />

      <!-- Classed to sit above the panel, same as the settings dialog - see app.scss.
           `no-backdrop-dismiss`, NOT `persistent`: a stray click outside must not dismiss a question
           about losing a conversation, but Escape must still cancel it. `persistent` blocks both,
           and only the first is the intention. See `dismissesPanelOnEscape` in
           `lib/escapeDismiss.ts` for why Escape here needs a guard the other dialogs do not. -->
      <q-dialog v-model="confirmOpen" no-backdrop-dismiss class="concierge-settings">
        <q-card class="reset-confirm-card os-dialog-sm">
          <q-card-section class="os-dialog-title">Reload the Concierge?</q-card-section>

          <q-card-section class="q-pt-none">
            This ends the running agent and starts a new one. The new one keeps its notes on your
            teams; this conversation, and anything it was part-way through, is lost.
          </q-card-section>

          <q-card-actions align="right">
            <q-btn v-close-popup flat label="Cancel" :disable="resetting" />
            <q-btn
              color="negative"
              unelevated
              label="Reload"
              :loading="resetting"
              @click="confirmOpen = false; void reset()"
            />
          </q-card-actions>
        </q-card>
      </q-dialog>

      <!-- ONE LINE, AND A LINK TO WHERE IT IS FIXED. `blocked` is the panel's whole content: no
           socket was opened, so the terminal below stays empty rather than printing a close reason. -->
      <div
        v-if="preflight.kind !== 'ready'"
        class="concierge-preflight row no-wrap items-start q-px-md q-py-xs"
        :class="{ 'concierge-preflight-blocked': preflight.kind === 'blocked' }"
        role="status"
      >
        <q-icon :name="preflight.kind === 'blocked' ? 'error' : 'info'" size="16px" class="q-mr-sm q-mt-xs" aria-hidden="true" />
        <span class="concierge-preflight-text">
          {{ preflight.lead }}
          <a href="#" class="concierge-preflight-link" @click.prevent="settingsOpen = true">Tenant Settings</a>.
          <q-tooltip v-if="preflight.detail">{{ preflight.detail }}</q-tooltip>
        </span>
      </div>
      <!-- Why an image did not go in, in the server's own words. Text only: the image itself is
           never shown anywhere in the app. -->
      <div
        v-if="attachError"
        class="concierge-attach-error row no-wrap items-start q-px-md q-py-xs"
        role="alert"
      >
        <q-icon name="error" size="16px" class="q-mr-sm q-mt-xs" aria-hidden="true" />
        <span class="col">{{ attachError }}</span>
        <q-btn flat dense round size="sm" icon="close" aria-label="Dismiss" @click="attachError = null" />
      </div>
      <div ref="host" class="col concierge-host" />

      <!-- Resize handles: only when windowed. Edges and corner grips. -->
      <div
        v-if="isWindowed"
        class="concierge-resize-e"
        @pointerdown="frame.onEdgePointerDown($event, 'resize-e')"
      />
      <div
        v-if="isWindowed"
        class="concierge-resize-s"
        @pointerdown="frame.onEdgePointerDown($event, 'resize-s')"
      />
      <div
        v-if="isWindowed"
        class="concierge-resize-se"
        @pointerdown="frame.onEdgePointerDown($event, 'resize-se')"
      />

      <!-- Touch, not width. These bars exist for devices whose keyboard is on the screen: no Esc or
           Ctrl key, and dictation that revises text a PTY cannot revise. A breakpoint answers a
           different question and gets it wrong - `lt-md` hid both bars on an iPad, because Quasar's
           md starts at 1024px and that is exactly where an iPad sits.

           A touch laptop gets them too, which is the harmless direction to be wrong in.

           Compose ABOVE the keys. It is where a line is written, so it belongs next to the terminal
           it will appear in, and the key bar stays closest to the thumb that reaches for ^C. -->
      <template v-if="$q.platform.has.touch">
        <ConciergeComposeBar ref="composeBar" @send="sendKey" @images="attachImages" @refused="attachError = $event" />
        <ConciergeKeyBar @key="sendKey" />
      </template>
    </div>
  </Teleport>
</template>

<style scoped>
.concierge-shell {
  position: fixed;

  /* Above Quasar's own layers, so nothing renders through it. */
  z-index: 7000;

  /* Full screen by default */
  left: 0;
  right: 0;
  overflow: hidden;

  /* THE BOTTOM WAS TRUNCATED ON iOS, AND `visualViewport` IS NOT WHAT MISSES IT. The height above
     is measured from `visualViewport`, which is correct and accounts for the keyboard - but the
     HOME INDICATOR is not a viewport inset. It is drawn OVER the page, so the last ~20pt of a
     correctly-sized element sits underneath it. What lands there is whatever is last in the
     column: the key bar on a touch device, and the terminal's final row otherwise.

     `env()` RESOLVES TO ZERO WHERE THERE IS NO INSET, so this costs nothing on a desktop and
     nothing on an Android without gesture navigation - which is why it is unconditional rather
     than behind a platform check.

     PADDING RATHER THAN A SMALLER HEIGHT, so the shell's background still reaches the bottom of
     the screen. Shrinking it would leave a strip of the console showing through beneath a panel
     that is meant to be covering it. */
  padding-bottom: env(safe-area-inset-bottom, 0px);
}

/* Windowed variant: inset positioning, rounded corners, border, shadow */
.concierge-shell.is-windowed {
  border: 1px solid rgba(255, 255, 255, 0.12);
  border-radius: 4px;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
}

.concierge-bar {
  min-height: 44px;
  cursor: move;

  /* The move gesture, and the same reason as the resize handles below: without this the browser
     pans the page on the first touchmove and the drag is gone before `frame.onHandlePointerDown`'s
     listeners see it. The buttons inside are excluded in the handler, not here. */
  touch-action: none;
}

/* The title block carries two lines, so it sets its own type rather than inheriting
   q-toolbar-title's single 21px one. `min-width: 0` is load-bearing rather than tidy: a flex item
   defaults to `min-width: auto` and refuses to shrink below its content, so without it the
   `ellipsis` on both children never engages and a long team name pushes the buttons off the bar. */
.concierge-heading {
  min-width: 0;
  line-height: 1.25;
}

.concierge-title {
  font-size: 15px;
  font-weight: 500;
}

/* Quiet against the dark bar - and deliberately NOT one of the --os-ink-* neutrals, which are ink
   for the light chrome. This panel is the app's one dark surface, so its muted tone is stated here
   rather than pretending to be part of a scale it is not on. */
.concierge-subtitle {
  color: rgba(255, 255, 255, 0.6);
  font-size: 11.5px;
  letter-spacing: 0.02em;
}

/* The two values lift out of the label words around them, which is the whole job of the line:
   the label words are read once, the name beside them is what is being looked up. */
.concierge-subtitle .mono {
  color: rgba(255, 255, 255, 0.87);
}

/* THE PICKER HAS TO READ AS THE LABEL IT REPLACED, not as a form field in a title bar. Inline so
   it stays on the same line as the label, and sized to the subtitle rather than to Quasar's
   control default, which is built for a form row and is twice this tall. */
.concierge-preflight {
  font-size: 12.5px;
  line-height: 1.4;
  color: rgba(255, 255, 255, 0.87);
  background: rgba(255, 255, 255, 0.06);
}

.concierge-preflight-blocked {
  background: rgba(255, 170, 0, 0.16);
}

.concierge-attach-error {
  font-size: 12.5px;
  line-height: 1.4;
  color: rgba(255, 255, 255, 0.87);
  background: rgba(255, 80, 80, 0.18);
}

.concierge-preflight-link {
  color: inherit;
  text-decoration: underline;
}

.concierge-agent-pick {
  display: inline-flex;
  vertical-align: baseline;
  min-width: 4rem;
  max-width: 14rem;
  font-size: 11.5px;
}

/* Quasar reserves vertical space inside a dense control for a field's furniture. There is no label
   and no hint here, so that space is a gap that pushes this line off the one beside it. */
.concierge-agent-pick :deep(.q-field__control),
.concierge-agent-pick :deep(.q-field__native) {
  min-height: 0;
  padding: 0;
}

.concierge-agent-pick :deep(.q-field__native) {
  color: rgba(255, 255, 255, 0.87);
  font-size: 11.5px;
}

/* Pulled in tight and smaller than Quasar's default, so the arrow reads as an affordance on a word
   rather than as a control the width of the header. */
.concierge-agent-pick :deep(.q-field__append) {
  padding-left: 2px;
  height: auto;
}

.concierge-agent-pick :deep(.q-field__append .q-icon) {
  font-size: 16px;
}

.concierge-active-team {
  color: rgba(255, 255, 255, 0.73);
  font-size: 11.5px;
  letter-spacing: 0.02em;
}

/* Held at its natural width. It is a flex item on the toolbar beside a title that is allowed to
   shrink, and a warning that ellipsises to "reload to ap…" is worse than one that pushes. */
.concierge-moved {
  flex: 0 0 auto;
}

.concierge-host {
  min-height: 0; /* load-bearing: a flex child defaults to min-height auto and refuses to shrink */
  padding: 8px;

  /* xterm's own default background, so the 8px of padding around the canvas matches it. */
  background: #000;
  overflow: hidden;

  /* The terminal owns every touch gesture in here.
     `none` rather than `pan-y`: with anything else the browser starts its own scroll on the first
     move and the drag is lost to the page before the scrollback handler ever sees enough travel to
     act. This is the declarative half of the fix; the unconditional preventDefault is the other. */
  touch-action: none;
  overscroll-behavior: contain;
}

/* Resize handles: only visible on windowed shells.
 *
 * `touch-action: none` IS WHAT MAKES THEM WORK WITH A FINGER. These are Pointer Events, which iOS
 * supports - but with the default `touch-action` the gesture never reaches the handlers: the
 * browser claims the first move for panning the page and fires `pointercancel` instead of
 * `pointermove`. NOTHING LOGS, the handle just does not drag. It is the same rule
 * `.concierge-host` above carries for scrollback,
 * written out again here rather than shared, because the two boxes want it for different gestures
 * and a single rule over both would be a coincidence waiting to be separated.
 *
 * AND `pointercancel` IS HANDLED IN THE SCRIPT REGARDLESS. A cancel can still arrive - a second
 * finger, a system gesture, the panel being torn down mid-drag - and without it the move listener
 * outlives the drag and the next stray pointermove resizes the window. */
.concierge-resize-e {
  position: absolute;
  right: 0;
  top: 44px;
  bottom: 0;
  width: 6px;
  cursor: ew-resize;
  touch-action: none;
}

.concierge-resize-s {
  position: absolute;
  bottom: 0;
  left: 0;
  right: 0;
  height: 6px;
  cursor: ns-resize;
  touch-action: none;
}

.concierge-resize-se {
  position: absolute;
  right: 0;
  bottom: 0;
  width: 12px;
  height: 12px;
  cursor: nwse-resize;
  touch-action: none;
}

/* A SIX-PIXEL TARGET IS A MOUSE TARGET. A fingertip covers roughly forty, so even with the gesture
   fixed above these are not reachable by hand - the drag that lands is the one that misses the
   handle and hits the terminal.
   `pointer: coarse` rather than a width breakpoint, for the reason the compose and key bars give
   above: an iPad sits at 1024px, exactly where Quasar's `md` begins, so a width question answers
   this one wrongly. The handles have no paint of their own, so this changes the hit area and
   nothing visible. */
@media (pointer: coarse) {
  .concierge-resize-e {
    width: 16px;
  }

  .concierge-resize-s {
    height: 16px;
  }

  .concierge-resize-se {
    width: 28px;
    height: 28px;
  }
}
</style>
