<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue';
import { useRoute } from 'vue-router';
import { connectSignIn, keyInput, mouseInput, pointAt, type SignInMessage, type SignInSocket } from '../lib/sign-in-socket';

/**
 * THE SIGN-IN PAGE, `#/teams/<team>/members/<member>/sign-in/<field>/<row>`: a table row's Sign in
 * link opens it. The platform runs a Google Chrome for that row, starting at the entry's Site
 * URL, and this page shows its screen and sends it the person's mouse and keys. Done keeps the
 * browser's session as the row's masked secret and closes the browser; Cancel or leaving the page
 * closes it and keeps nothing. The session is never shown here.
 */
const route = useRoute();
const param = (name: string) => String(route.params[name] ?? '');
const where = { team: param('team'), member: param('member'), field: param('field'), row: param('row') };

type State = 'connecting' | 'live' | 'saving' | 'over';
const state = ref<State>('connecting');
const size = ref({ width: 1280, height: 800 });
const shown = ref(false);
const address = ref('');
const title = ref('');
const notes = ref<string[]>([]);
const outcome = ref('');
const error = ref('');
const screen = ref<HTMLElement | null>(null);
const canvas = ref<HTMLCanvasElement | null>(null);

let socket: SignInSocket | null = null;

const live = computed(() => state.value === 'live');

function note(text: string) {
  notes.value = [...notes.value.slice(-4), text];
}

function onMessage(message: SignInMessage) {
  switch (message.kind) {
    case 'started':
      size.value = { width: message.width, height: message.height };
      address.value = message.site;
      state.value = 'live';
      screen.value?.focus();
      break;
    case 'page':
      address.value = message.url ?? '';
      title.value = message.title ?? '';
      break;
    case 'refused':
      note(`${message.host} is not this entry's site or a sign-in provider it passed through, so it was not opened.`);
      break;
    case 'provider':
      note(`Signing in through ${message.host}.`);
      break;
    case 'saved':
      outcome.value = `Saved the session for ${message.hosts.join(', ')}.`;
      break;
    case 'error':
      error.value = message.error;
      state.value = 'live';
      break;
    case 'ended':
      if (!outcome.value) outcome.value = message.reason;
      state.value = 'over';
      break;
  }
}

/**
 * Each frame is decoded and DRAWN ON A CANVAS, never shown through a `blob:` URL: the page's
 * Content-Security-Policy allows images from 'self' only, and needs no exception for this. A frame
 * decoded after a newer one is dropped.
 */
let framesIn = 0;
let framesDrawn = 0;
async function onFrame(jpeg: Blob) {
  const n = ++framesIn;
  let bitmap: ImageBitmap;
  try {
    bitmap = await createImageBitmap(jpeg);
  } catch {
    return;
  }
  const target = canvas.value;
  if (n < framesDrawn || !target) {
    bitmap.close?.();
    return;
  }
  framesDrawn = n;
  if (target.width !== bitmap.width || target.height !== bitmap.height) {
    target.width = bitmap.width;
    target.height = bitmap.height;
  }
  target.getContext('2d')?.drawImage(bitmap, 0, 0);
  bitmap.close?.();
  shown.value = true;
}

function rect() {
  return screen.value?.getBoundingClientRect() ?? { left: 0, top: 0, width: 0, height: 0 };
}

function mouse(kind: 'mousePressed' | 'mouseReleased' | 'mouseMoved', event: MouseEvent) {
  if (!live.value) return;
  if (kind === 'mousePressed') screen.value?.focus();
  socket?.send(mouseInput(kind, event, rect(), size.value));
}

function wheel(event: WheelEvent) {
  if (!live.value) return;
  socket?.send({ type: 'wheel', ...pointAt(event.clientX, event.clientY, rect(), size.value), deltaX: event.deltaX, deltaY: event.deltaY });
}

function key(kind: 'keyDown' | 'keyUp', event: KeyboardEvent) {
  if (!live.value) return;
  // A paste is its own input; the keys that make it are not sent.
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'v') return;
  event.preventDefault();
  socket?.send(keyInput(kind, event));
}

function paste(event: ClipboardEvent) {
  const text = event.clipboardData?.getData('text/plain');
  if (!live.value || !text) return;
  event.preventDefault();
  socket?.send({ type: 'text', text });
}

function done() {
  error.value = '';
  state.value = 'saving';
  socket?.done();
}

function cancel() {
  socket?.cancel();
}

onMounted(() => {
  socket = connectSignIn(where, {
    onMessage,
    onFrame,
    onClose: () => {
      if (state.value !== 'over') {
        if (!outcome.value) outcome.value = 'The browser closed. Nothing was saved.';
        state.value = 'over';
      }
    },
  });
});

onBeforeUnmount(() => {
  socket?.dispose();
});
</script>

<template>
  <div class="sign-in-page q-pa-md column no-wrap">
    <div class="row items-center q-gutter-sm q-mb-sm">
      <div class="col">
        <div class="text-subtitle1">Sign in: {{ where.row }}</div>
        <div class="text-caption ellipsis sign-in-address" :title="address">{{ title || address || 'Starting the browser…' }}</div>
      </div>
      <q-btn
        v-if="state !== 'over'"
        class="sign-in-cancel"
        flat
        no-caps
        label="Cancel"
        :disable="state === 'saving'"
        @click="cancel"
      />
      <q-btn
        v-if="state !== 'over'"
        class="sign-in-done"
        color="primary"
        unelevated
        no-caps
        label="Done"
        :loading="state === 'saving'"
        :disable="!live"
        @click="done"
      />
    </div>

    <div v-if="error" class="sign-in-error text-negative q-mb-sm">{{ error }}</div>
    <div v-for="(text, i) in notes" :key="i" class="sign-in-note text-caption q-mb-xs">{{ text }}</div>

    <div v-if="state === 'over'" class="sign-in-outcome q-pa-lg text-center">
      <div class="text-body1">{{ outcome }}</div>
      <div class="text-caption q-mt-sm">You can close this tab.</div>
    </div>

    <div
      v-else
      ref="screen"
      class="sign-in-screen"
      tabindex="0"
      :style="{ aspectRatio: `${size.width} / ${size.height}` }"
      @mousedown.prevent="mouse('mousePressed', $event)"
      @mouseup.prevent="mouse('mouseReleased', $event)"
      @mousemove="mouse('mouseMoved', $event)"
      @wheel.prevent="wheel"
      @keydown="key('keyDown', $event)"
      @keyup="key('keyUp', $event)"
      @paste="paste"
      @contextmenu.prevent
    >
      <canvas v-show="shown" ref="canvas" class="sign-in-frame" role="img" aria-label="The sign-in browser" />
      <div v-if="!shown" class="sign-in-waiting flex flex-center">Starting the browser…</div>
    </div>
  </div>
</template>

<style scoped>
.sign-in-page {
  min-height: 100vh;
  background: var(--os-paper, var(--os-chrome));
  color: var(--os-ink);
}

.sign-in-screen {
  width: 100%;
  max-width: 1280px;
  margin: 0 auto;
  outline: 1px solid var(--os-rule, rgba(0, 0, 0, 0.2));
  background: #fff;
  cursor: default;
}

.sign-in-screen:focus {
  outline: 2px solid var(--q-primary);
}

.sign-in-screen canvas {
  display: block;
  width: 100%;
  height: 100%;
  user-select: none;
}

.sign-in-waiting {
  height: 100%;
  color: #666;
}
</style>
