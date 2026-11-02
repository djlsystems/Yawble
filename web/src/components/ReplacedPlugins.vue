<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { moveReplacedMembers } from '../api/client';
import type { ReplacedPlugin, ReplacementMoveResult } from '../api/types';
import { membersOnWords, pluginWords } from '../lib/plugins';

/**
 * WHAT A PLUGIN SAYS IT REPLACES, and who is still on the old one (PLG 3, rename). Only the newer
 * plugin's own manifest says it (`replaces`); the Host answers each replaced id that is installed, its
 * version and every member on it, team by team. Shown on the new plugin's tile in Admin -> Plugins and
 * in the install and upgrade answers.
 *
 * NOTHING MOVES WITHOUT THE PERSON: "Move members" asks first, naming the members, then moves them
 * through `POST /api/plugins/{id}/replaces/{old}/move` - each keeps its settings and own secret values;
 * one that cannot be moved is named with the Host's sentence and stays where it was. The old plugin is
 * never removed here: once nothing uses it, the person removes it from its own tile.
 */
const props = defineProps<{
  /** The plugin that replaces them. */
  plugin: { id: string; name: string; version: string };
  replaced: ReplacedPlugin[];
}>();

const emit = defineEmits<{ moved: [] }>();

/** The old id whose move is being asked about, or null. */
const asking = ref<string | null>(null);
const busy = ref(false);
const results = ref<Record<string, ReplacementMoveResult>>({});
const problems = ref<Record<string, string>>({});

watch(() => props.plugin.id, () => {
  asking.value = null;
  results.value = {};
  problems.value = {};
});

const askingEntry = computed(() => props.replaced.find((entry) => entry.id === asking.value) ?? null);

const question = computed(() => {
  const entry = askingEntry.value;
  if (!entry) return '';
  const count = entry.members.length === 1 ? 'its member' : `its ${entry.members.length} members`;
  return `Move ${count} (${membersOnWords(entry.members)}) from ${pluginWords(entry)} onto ${pluginWords(props.plugin)}? `
    + 'Each keeps its settings and its own secret values. The old plugin stays installed until you remove it.';
});

async function move(entry: ReplacedPlugin) {
  if (busy.value) return;
  busy.value = true;
  problems.value = { ...problems.value, [entry.id]: '' };
  try {
    results.value = { ...results.value, [entry.id]: await moveReplacedMembers(props.plugin.id, entry.id) };
    asking.value = null;
    emit('moved');
  } catch (cause) {
    problems.value = { ...problems.value, [entry.id]: cause instanceof Error ? cause.message : String(cause) };
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <div v-for="entry in replaced" :key="entry.id" class="replaced-plugin" :data-replaced="entry.id">
    <div class="os-body" data-replaced-line>
      Replaces <span class="text-weight-medium">{{ pluginWords(entry) }}</span>.
      <template v-if="entry.members.length > 0">Still on it: {{ membersOnWords(entry.members) }}.</template>
      <template v-else>No member is on it; remove it from its own tile when you are ready.</template>
    </div>

    <div v-if="results[entry.id]" class="os-body q-mt-xs" data-replaced-moves>
      <div
        v-for="moved in results[entry.id]!.moves"
        :key="`${moved.team}/${moved.member}`"
        :class="moved.outcome === 'moved' ? 'text-positive' : 'text-negative'"
        :data-replaced-move="`${moved.team}/${moved.member}`"
        :data-outcome="moved.outcome"
      >
        {{ moved.teamName }} / {{ moved.memberName }}:
        {{ moved.outcome === 'moved' ? `moved onto ${plugin.id}` : `not moved - ${moved.reason ?? 'the Host gave no reason.'}` }}
      </div>
    </div>

    <div v-if="problems[entry.id]" class="os-body text-negative q-mt-xs" role="alert" data-replaced-problem>{{ problems[entry.id] }}</div>

    <template v-if="entry.members.length > 0">
      <div v-if="asking === entry.id" class="q-mt-xs" data-replaced-confirm>
        <div class="os-body" data-replaced-question>{{ question }}</div>
        <div class="row justify-end q-gutter-xs q-mt-xs">
          <q-btn flat dense no-caps label="Cancel" :disable="busy" data-replaced-cancel @click="asking = null" />
          <q-btn
            unelevated
            dense
            no-caps
            color="primary"
            label="Move"
            :loading="busy"
            :disable="busy"
            data-replaced-move-confirm
            @click="move(entry)"
          />
        </div>
      </div>
      <q-btn
        v-else
        flat
        dense
        no-caps
        color="primary"
        icon="arrow_forward"
        :label="`Move members onto ${plugin.id}`"
        class="q-mt-xs"
        data-replaced-move-ask
        @click="asking = entry.id"
      />
    </template>
  </div>
</template>

<style scoped>
.replaced-plugin + .replaced-plugin {
  margin-top: 6px;
}
</style>
