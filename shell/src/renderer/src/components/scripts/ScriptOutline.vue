<script setup lang="ts">
import { computed } from 'vue';
import { buildOutline } from '../../scripts/api';
import type { ScriptDocument } from '../../scripts/types';

// The script's outline beside its JSON (B134): settings, requires, variables and the steps, each step with where it
// goes next (next, then / else, the cases, a loop's body). Read-only; a step's name jumps to it in the JSON.
const props = defineProps<{ doc: ScriptDocument | null; issueSteps?: readonly string[] }>();
const emit = defineEmits<{ step: [id: string] }>();
const o = computed(() => buildOutline(props.doc));
</script>

<template>
  <div class="outline" data-test="script-outline">
    <p v-if="!doc" class="muted small">The outline shows once the JSON parses.</p>
    <template v-else>
      <h4>Settings</h4>
      <ul class="list" data-test="outline-settings">
        <li v-for="s in o.settings" :key="s.name"><code>{{ s.name }}</code> <span class="type">{{ s.type }}</span><span v-if="s.detail" class="muted"> · {{ s.detail }}</span></li>
        <li v-if="!o.settings.length" class="muted">none</li>
      </ul>
      <h4>Requires</h4>
      <ul class="list" data-test="outline-requires">
        <li v-for="r in o.requires" :key="r.name"><code>{{ r.name }}</code> <span class="type">{{ r.kind }}</span><span v-if="r.detail" class="muted"> · {{ r.detail }}</span></li>
        <li v-if="!o.requires.length" class="muted">none</li>
      </ul>
      <h4>Variables</h4>
      <ul class="list" data-test="outline-variables">
        <li v-for="v in o.variables" :key="v.name"><code>{{ v.name }}</code> <span class="type">{{ v.type }}</span><span v-if="v.detail" class="muted"> · {{ v.detail }}</span></li>
        <li v-if="!o.variables.length" class="muted">none</li>
      </ul>
      <h4>Steps</h4>
      <ul class="list steps" data-test="outline-steps">
        <li v-for="s in o.steps" :key="s.id" data-test="outline-step" :data-step="s.id" :class="{ bad: issueSteps?.includes(s.id) }">
          <button type="button" class="link" :title="`Show ${s.id} in the JSON`" @click="emit('step', s.id)"><code>{{ s.id }}</code></button>
          <span class="type">{{ s.type }}</span><span v-if="s.start" class="chip">start</span>
          <ul v-if="s.links.length" class="links">
            <li v-for="l in s.links" :key="l.label" data-test="outline-link">{{ l.label }} → <code>{{ l.target ?? 'end of body' }}</code></li>
          </ul>
        </li>
        <li v-if="!o.steps.length" class="muted">none</li>
      </ul>
    </template>
  </div>
</template>

<style scoped>
.outline { font-size: 12.5px; overflow: auto; }
h4 { margin: 10px 0 2px; font-size: 12px; font-weight: 600; color: var(--muted); }
h4:first-of-type { margin-top: 0; }
.list { list-style: none; margin: 0; padding: 0 0 0 4px; }
.list > li { padding: 1px 0; }
.links { list-style: none; margin: 0; padding: 0 0 0 18px; color: var(--ink-soft); }
.type { color: var(--muted); margin-left: 4px; }
.bad > .link code { color: var(--bad); }
.link { border: 0; background: transparent; padding: 0; color: var(--accent); }
.link:hover { text-decoration: underline; }
.small { font-size: 12px; }
</style>
