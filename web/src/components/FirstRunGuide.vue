<script setup lang="ts">
import { onMounted, ref, watch } from 'vue';
import { getAgentAuth } from '../api/client';
import { agentSignIn, readGuideDismissed, writeGuideDismissed, type AgentSignIn } from '../lib/firstRunGuide';

/**
 * The first-run guide: what the Concierge is, choosing its agent, signing in, then a team.
 *
 * IT SHOWS ITSELF until this browser dismisses it, and only while the platform has not measured an
 * agent as signed in. Reopened from the account menu, it shows whatever the answer is.
 *
 * NOT A MODAL. It sits above the Concierge button it points at, so a person can press that button
 * with the guide still open and follow the steps beside it.
 */
const props = defineProps<{
  open: boolean;
  /** Whether the Concierge panel is open. Closing it is when a sign-in may have happened. */
  conciergeOpen: boolean;
}>();

const emit = defineEmits<{
  'update:open': [open: boolean];
}>();

/** Null until the platform has been asked. */
const signIn = ref<AgentSignIn | null>(null);

/** Shown because nobody had dismissed it, rather than reopened from the menu. Only this kind hides
 *  itself when an agent signs in: one a person asked for stays until they close it. */
let shownByItself = false;

async function refresh() {
  try {
    signIn.value = agentSignIn(await getAgentAuth());
  } catch {
    // NO ANSWER IS NOT AN ANSWER: never signed in or signed out, so the guide says neither.
    signIn.value = 'not-measured';
  }
}

onMounted(async () => {
  await refresh();

  if (!readGuideDismissed() && signIn.value !== 'signed-in') {
    shownByItself = true;
    emit('update:open', true);
  }
});

watch(
  () => props.open,
  (open, was) => {
    if (open && !was && !shownByItself) void refresh();
    if (!open) shownByItself = false;
  },
);

watch(
  () => props.conciergeOpen,
  async (isOpen) => {
    if (isOpen || !props.open) return;

    await refresh();

    if (shownByItself && signIn.value === 'signed-in') emit('update:open', false);
  },
);

function dismiss() {
  writeGuideDismissed();
  shownByItself = false;
  emit('update:open', false);
}
</script>

<template>
  <q-page-sticky v-if="open" position="bottom-right" :offset="[18, 88]" class="first-run-guide-dock">
    <q-card
      class="first-run-guide os-body"
      role="region"
      aria-labelledby="first-run-guide-title"
      data-test="first-run-guide"
    >
      <q-card-section class="row items-center no-wrap q-pb-none">
        <div id="first-run-guide-title" class="text-subtitle1 text-weight-medium col">Getting started</div>
        <q-btn flat dense round icon="close" aria-label="Dismiss the guide" data-test="first-run-guide-dismiss" @click="dismiss" />
      </q-card-section>

      <q-card-section class="q-pt-sm q-pb-none">
        <!-- NOTHING IS SAID when the platform could not answer: the steps below hold either way, and
             a guess would be wrong half the time. -->
        <div v-if="signIn === 'signed-in' || signIn === 'signed-out'" class="first-run-guide-status" data-test="first-run-guide-status">
          <template v-if="signIn === 'signed-in'">
            <q-icon name="verified_user" class="q-mr-xs" />
            An agent is signed in, so teams can work.
          </template>
          <template v-else>
            <q-icon name="no_accounts" class="q-mr-xs" />
            No agent is signed in yet. A team cannot work until one is.
          </template>
        </div>

        <ol class="first-run-guide-steps q-pl-md q-my-sm">
          <li>
            <strong>The Concierge</strong> is the assistant that sets things up for you. Open it with the
            <strong>Concierge</strong> button below this guide, in the bottom-right corner.
          </li>
          <li>
            <strong>Choose its agent</strong> from the list under the Concierge's title.
          </li>
          <li>
            <strong>Sign in there.</strong> The agent shows its own sign-in steps in the Concierge. Follow
            them until it is ready for your questions.
          </li>
          <li>
            <strong>Then ask the Concierge for a team</strong>, use <strong>New Team</strong>, or get a ready-made one from
            <strong>Marketplace</strong> on the ribbon.
          </li>
        </ol>
      </q-card-section>

      <div class="text-caption os-text-muted q-px-md q-pb-sm">
        You can open this guide again from the account menu, under Getting started.
      </div>
    </q-card>
  </q-page-sticky>
</template>

<style scoped>
.first-run-guide {
  width: min(360px, calc(100vw - 36px));
  border: 3px solid var(--os-primary);
  position: relative;
  overflow: visible;
}

/* The pointer: a notch on the card's lower edge, over the Concierge button beneath it. */
.first-run-guide::after {
  content: '';
  position: absolute;
  right: 48px;
  bottom: -8px;
  width: 16px;
  height: 16px;
  background: inherit;
  border-right: 3px solid var(--os-primary);
  border-bottom: 3px solid var(--os-primary);
  transform: rotate(45deg);
  box-shadow: 2px 2px 3px rgba(0, 0, 0, 0.12);
}

.first-run-guide-steps li + li {
  margin-top: 6px;
}
</style>
