<script setup lang="ts">
import { linkParts } from '../links';
import { installable } from '../agentClis';
import { computed } from 'vue';
import BusyLabel from './BusyLabel.vue';
import type { AgentTestSession, AgentVersionStatus, DetectedCli } from '../../../shared/api';

// One agent on Settings > Agents: a built-in CLI (as detected on this computer) or one of the person's own agents
// (shown right after the built-in it is based on). Everything the agent has is here; every action opens a dialog.
const props = defineProps<{
  title: string;
  cliId: string;
  custom: boolean;
  baseTitle?: string;
  detected: DetectedCli | null;
  version?: AgentVersionStatus | null;
  defaultModel?: string | null;
  /** A cloud viewer: detection only. */
  remote?: boolean;
  /** The default model / version status are still loading: their line is kept (a faint "…"), so nothing jumps. */
  loading?: boolean;
  /** This agent's Terminal / Test session, while it runs (agents.testSessions): a pulsing chip with Open and Stop. */
  running?: AgentTestSession | null;
  /** Terminal / Test was clicked and the session is starting (detection, sandbox): that button shows a spinner. */
  starting?: 'terminal' | 'test' | null;
  /** Update was clicked and the core is opening the CLI's update terminal: that button shows a spinner. */
  updating?: boolean;
}>();
const emit = defineEmits<{ terminal: []; test: []; commandLine: []; model: []; update: []; install: []; duplicate: []; edit: []; remove: []; open: []; stop: [] }>();

const kinds: Record<string, string> = { native: 'official installer', npm: 'npm', homebrew: 'Homebrew', winget: 'WinGet', other: 'other' };
const found = computed(() => !!props.detected?.found);
</script>

<template>
  <article class="tile" :class="{ custom }">
    <header class="bar">
      <h3>{{ title }}</h3>
      <span v-if="custom" class="chip mine" data-test="tile-chip">My agent · based on {{ baseTitle }}</span>
      <span v-else class="chip" :class="found ? 'ok' : 'missing'" data-test="cli-found">{{ found ? 'Found' : 'Not found' }}</span>
      <span v-if="version?.changed" class="warn small" data-test="version-changed">
        Version changed since the last run: {{ version.lastRun }} → {{ version.detected }}
      </span>
      <span v-if="running && !remote" class="running" data-test="tile-running">
        <span class="chip live" data-test="tile-running-chip"><span class="dot"></span>Running · {{ running.kind === 'test' ? 'Test' : 'Terminal' }}</span>
        <button class="small-btn" data-test="tile-running-open" title="Show this session in the terminal pane" @click="emit('open')">Open</button>
        <button class="small-btn" data-test="tile-running-stop" title="End this session" @click="emit('stop')">Stop</button>
      </span>
      <span class="grow"></span>
      <template v-if="!remote">
        <!-- Terminal: the agent's own TUI, no prompt; Test: the same with the diagnostic prompt (version, cwd, HOME, read/write checks). -->
        <template v-if="found">
          <button class="small-btn" :data-test="`terminal-${cliId}`" :disabled="!!starting" :aria-busy="starting === 'terminal'"
            title="Open this agent's interactive session in the terminal pane (no prompt)" @click="emit('terminal')">
            <BusyLabel :busy="starting === 'terminal'" text="Starting…">Terminal</BusyLabel></button>
          <button class="primary small-btn" :data-test="`test-${cliId}`" :disabled="!!starting" :aria-busy="starting === 'test'"
            title="Open it with a diagnostic prompt: version, cwd, HOME and read/write checks" @click="emit('test')">
            <BusyLabel :busy="starting === 'test'" text="Starting…">Test ▶</BusyLabel></button>
        </template>
        <button class="small-btn" data-test="tile-cmd" @click="emit('commandLine')">Command line</button>
        <button class="small-btn" data-test="tile-model" @click="emit('model')">Model</button>
        <button v-if="!custom && found" class="small-btn" data-test="tile-update" :disabled="updating" :aria-busy="updating" @click="emit('update')"><BusyLabel :busy="!!updating" text="Opening…">Update</BusyLabel></button>
        <button v-if="!custom && !found && !loading && installable.includes(cliId)" class="primary small-btn" data-test="tile-install-btn" :disabled="updating" :aria-busy="updating"
          title="Opens a terminal that runs the maker's own install command" @click="emit('install')"><BusyLabel :busy="!!updating" text="Opening…">Install</BusyLabel></button>
        <button class="small-btn" data-test="tile-duplicate" @click="emit('duplicate')">Duplicate</button>
        <button v-if="custom" class="small-btn" data-test="tile-edit" @click="emit('edit')">Edit</button>
        <button v-if="custom" class="small-btn danger" data-test="tile-delete" @click="emit('remove')">Delete</button>
      </template>
    </header>
    <div class="body small">
      <div v-if="found && detected" class="muted" data-test="tile-install">
        <span class="mono" data-test="cli-path" :title="detected.launcherPath ?? ''">{{ detected.executablePath }}</span>
        · version <strong data-test="cli-version">{{ detected.version ?? 'unknown' }}</strong>
        · installed with <span data-test="cli-kind">{{ kinds[detected.installKind] ?? detected.installKind }}</span>
        <template v-if="detected.launcherPath && detected.launcherPath !== detected.executablePath"> · started as <span class="mono">{{ detected.launcherPath }}</span></template>
      </div>
      <div v-else-if="detected" data-test="cli-install-help">{{ custom ? `${baseTitle} is not installed: ` : '' }}<template v-for="(part, i) in linkParts(detected.installHelp)" :key="i"><a v-if="part.href" :href="part.href" target="_blank" rel="noopener">{{ part.text }}</a><template v-else>{{ part.text }}</template></template></div>
      <div v-if="!remote && loading" class="muted faint" data-test="tile-default-model-loading" aria-label="Loading the default model">Default model: …</div>
      <div v-else-if="!remote" class="muted" data-test="tile-default-model">Default model: {{ defaultModel || "the CLI's default" }}</div>
      <ul v-if="detected?.problems.length" class="problems" data-test="cli-problems">
        <li v-for="p in detected.problems" :key="p" class="warn">{{ p }}</li>
      </ul>
    </div>
  </article>
</template>

<style scoped>
.tile { border: 1px solid var(--line); border-radius: 6px; padding: 6px 10px; }
.tile.custom { margin-left: 18px; border-style: dashed; }
.bar { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; }
h3 { margin: 0; font-size: 14px; }
.chip { margin-left: 0; }
.chip.ok { background: var(--pill-green-bg); color: var(--pill-green-ink); }
.chip.missing { background: var(--pill-red-bg); color: var(--pill-red-ink); }
.chip.mine { background: var(--chip); color: var(--ink-soft); }
.running { display: inline-flex; align-items: center; gap: 4px; }
.chip.live { background: var(--pill-blue-bg); color: var(--pill-blue-ink); display: inline-flex; align-items: center; gap: 5px; }
.dot { width: 7px; height: 7px; border-radius: 50%; background: currentColor; animation: pulse 1.4s ease-in-out infinite; }
@keyframes pulse { 0%, 100% { opacity: 1; } 50% { opacity: .25; } }
@media (prefers-reduced-motion: reduce) { .dot { animation: none; } }
.small-btn { padding: 2px 8px; font-size: 12px; }
.body { margin-top: 3px; display: flex; flex-direction: column; gap: 1px; }
.problems { margin: 2px 0 0; padding-left: 18px; }
.small { font-size: 12px; }
.faint { opacity: 0.6; }
.mono { font-family: ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; word-break: break-all; }
</style>
