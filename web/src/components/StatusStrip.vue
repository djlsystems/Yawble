<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue';
import type { AgentAuthReport, WipHold } from '../api/types';
import { authProblems as problemsIn } from '../lib/useAgentAuth';
import { useWipStore } from '../stores/wip';

/** The ledger comes from the one store the board and the Teams table read too - no fetch here. */
const wip = useWipStore();
const view = computed(() => wip.view);
const authProblems = ref<string[]>([]);

async function refreshAuth() {
  try {
    const response = await fetch('/api/agents/auth', { credentials: 'same-origin' });
    if (!response.ok) return;
    authProblems.value = problemsIn((await response.json()) as AgentAuthReport[]);
  } catch {
    authProblems.value = [];
  }
}

function names(holds: WipHold[]) {
  return holds.map((hold) => `${hold.team}/${hold.member}`).join(', ');
}

onMounted(() => {
  wip.watch();
  void refreshAuth();
});

onUnmounted(() => wip.unwatch());
</script>

<template>
  <div v-if="view && view.waiting.length > 0" class="bg-warning text-dark q-px-md q-py-xs os-body">
    Work is held. {{ view.running.length }} of {{ view.max }} slots are taken
    <span v-if="view.running.length"> by {{ names(view.running) }}</span>.
    Waiting: {{ names(view.waiting) }}.
  </div>
  <div v-if="authProblems.length" class="bg-negative text-white q-px-md q-py-xs os-body">
    An agent CLI a team uses is installed but not authenticated. {{ authProblems.join(' ') }}
    Sign in inside the container, or set that CLI's API key in the container environment.
  </div>
</template>
