<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue';
import type { AppVersion, YawbleApi } from '../../../shared/api';
import { shellBuild } from '../buildVersion';

/**
 * Settings > System > About: "Yawble Desktop", this build's version (core app.version: yyyy.mm.dd.N for a release,
 * <last release>+dev.<sha> between releases), its commit, release or development build,
 * the cloud's version while the Cloud Link is connected, and the project's links. Links are plain target=_blank
 * anchors: the window opens them in the person's browser (main's window-open handler), a remote view in a new tab.
 */
const props = defineProps<{ api: YawbleApi }>();

const v = ref<AppVersion | null>(null);
const failed = ref(false);
const copied = ref(false);

const links = [
  { id: 'website', label: 'Website', href: 'https://yawble.ai/' },
  { id: 'github', label: 'GitHub', href: 'https://github.com/djlsystems/Yawble' },
  { id: 'youtube', label: 'YouTube', href: 'https://www.youtube.com/@Yawble' },
  { id: 'x', label: 'X', href: 'https://x.com/_Yawble' },
] as const;

async function load() {
  try {
    v.value = await props.api.call<AppVersion>('app.version');
    failed.value = false;
  } catch {
    // An older core has no app.version: the window falls back on its own build; a remote view cannot tell.
    if (!v.value) {
      failed.value = true;
      if (!props.api.remote) v.value = { ...shellBuild, cloudVersion: null };
    }
  }
}

async function copy() {
  if (!v.value) return;
  try {
    await navigator.clipboard.writeText(v.value.version);
    copied.value = true;
    setTimeout(() => { copied.value = false; }, 1500);
  } catch { /* the version is on screen, selectable */ }
}

let off: (() => void) | null = null;
onMounted(() => {
  void load();
  // The cloud's version comes and goes with the Cloud Link.
  off = props.api.onEvent?.((e) => { if (e.event === 'link.state') void load(); }) ?? null;
});
onBeforeUnmount(() => off?.());
</script>

<template>
  <section class="about" data-test="about">
    <h2>About</h2>
    <div class="product">
      <span class="name">Yawble Desktop</span>
      <span class="version" data-test="about-version">{{ v ? v.version : (failed ? 'unknown' : '…') }}</span>
      <button v-if="v" class="copy" data-test="about-copy" title="Copy the version" @click="copy">{{ copied ? 'Copied' : 'Copy' }}</button>
    </div>
    <dl v-if="v">
      <dt>Build</dt>
      <dd data-test="about-kind">{{ v.released ? 'release' : 'development build' }}<template v-if="v.dirty"> (uncommitted changes)</template></dd>
      <dt>Commit</dt>
      <dd class="mono-sel" data-test="about-commit" :title="v.commit">{{ v.commit === 'unknown' ? 'unknown' : v.commit.slice(0, 12) }}</dd>
    </dl>
    <p v-if="v?.cloudVersion" class="cloud" data-test="about-cloud">Cloud: <span class="mono-sel">{{ v.cloudVersion }}</span></p>
    <ul class="links" data-test="about-links">
      <li v-for="l in links" :key="l.id">
        <a :href="l.href" target="_blank" rel="noopener noreferrer" :data-test="`about-link-${l.id}`" :title="l.href">
          <svg v-if="l.id === 'website'" viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round">
            <circle cx="12" cy="12" r="9" /><path d="M3 12h18M12 3c2.5 2.6 3.8 5.6 3.8 9s-1.3 6.4-3.8 9c-2.5-2.6-3.8-5.6-3.8-9S9.5 5.6 12 3z" />
          </svg>
          <svg v-else-if="l.id === 'github'" viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" fill="currentColor">
            <path d="M12 2a10 10 0 0 0-3.16 19.49c.5.09.68-.22.68-.48v-1.7c-2.78.6-3.37-1.34-3.37-1.34-.45-1.16-1.11-1.47-1.11-1.47-.91-.62.07-.6.07-.6 1 .07 1.53 1.03 1.53 1.03.9 1.52 2.34 1.08 2.91.83.09-.65.35-1.08.63-1.33-2.22-.25-4.55-1.11-4.55-4.94 0-1.09.39-1.98 1.03-2.68-.1-.25-.45-1.27.1-2.65 0 0 .84-.27 2.75 1.02a9.5 9.5 0 0 1 5 0c1.91-1.29 2.75-1.02 2.75-1.02.55 1.38.2 2.4.1 2.65.64.7 1.03 1.59 1.03 2.68 0 3.84-2.34 4.69-4.57 4.93.36.31.68.92.68 1.85v2.75c0 .27.18.58.69.48A10 10 0 0 0 12 2z" />
          </svg>
          <svg v-else-if="l.id === 'youtube'" viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" fill="currentColor">
            <path d="M21.6 7.2a2.5 2.5 0 0 0-1.77-1.77C18.27 5 12 5 12 5s-6.27 0-7.83.43A2.5 2.5 0 0 0 2.4 7.2 26 26 0 0 0 2 12a26 26 0 0 0 .4 4.8 2.5 2.5 0 0 0 1.77 1.77C5.73 19 12 19 12 19s6.27 0 7.83-.43a2.5 2.5 0 0 0 1.77-1.77A26 26 0 0 0 22 12a26 26 0 0 0-.4-4.8zM10 15V9l5.2 3z" />
          </svg>
          <svg v-else viewBox="0 0 24 24" width="16" height="16" aria-hidden="true" fill="currentColor">
            <path d="M17.75 3h3.07l-6.7 7.66L22 21h-6.17l-4.83-6.32L5.47 21H2.4l7.17-8.2L2 3h6.33l4.37 5.78L17.75 3zm-1.08 16.17h1.7L7.4 4.74H5.58l11.09 14.43z" />
          </svg>
          <span>{{ l.label }}</span>
        </a>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.about { background: var(--surface); border: 1px solid var(--line); border-radius: 6px; padding: 8px 14px 12px; margin-top: 12px; }
h2 { font-size: 15px; margin: 6px 0 6px; }
.product { display: flex; align-items: baseline; gap: 10px; flex-wrap: wrap; margin-bottom: 6px; }
.name { font-weight: 600; color: var(--ink); }
.version, .mono-sel { font-family: ui-monospace, monospace; user-select: text; -webkit-user-select: text; cursor: text; }
.version { color: var(--ink); }
.copy { font-size: 12px; padding: 1px 8px; }
dl { display: grid; grid-template-columns: max-content 1fr; gap: 4px 16px; margin: 0 0 8px; }
dt { color: var(--muted); }
dd { margin: 0; color: var(--ink-soft); }
.cloud { margin: 0 0 8px; color: var(--ink-soft); }
.links { list-style: none; display: flex; flex-wrap: wrap; gap: 6px 18px; margin: 4px 0 0; padding: 0; }
.links a { display: inline-flex; align-items: center; gap: 6px; color: var(--accent); text-decoration: none; }
.links a:hover span { text-decoration: underline; }
.links svg { flex: none; color: var(--muted); }
.links a:hover svg { color: var(--accent); }
</style>
