<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useRoute, useRouter } from 'vue-router';
import { useQuasar } from 'quasar';
import { useSessionStore } from '../stores/session';
import { doorFor } from '../lib/door';
import { goOnward, siteEntry } from '../router/guard';
import AuthForm from '../components/AuthForm.vue';
import VersionTag from '../components/VersionTag.vue';
import { productLogoFor, productTagline, productTitle } from '../presentation/product';

/**
 * The front door, and nothing else: the product name and a box to log in with, or to create the
 * first account when there is none. Selling the product is the marketing site's job; this
 * instance is for the people who already run it.
 *
 * Public: the router lets anyone reach this path, because a gated instance with nowhere to knock
 * is the failure this page exists to prevent. A signed-in session goes straight to the console.
 */
const $q = useQuasar();
const session = useSessionStore();
const router = useRouter();
const route = useRoute();

const door = computed(() => doorFor(session));

/** Where signing in continues to: the route or site entry a signed-out visitor asked for, else the Console. */
const left = { done: false };
const refusal = ref('');
const onward = () => {
  refusal.value = '';
  return goOnward(route.query, (path) => router.replace(path), left, (why) => { refusal.value = why; });
};

/** Signed in with a site entry in `next`: the door is on its way to the site, not asking to sign in. */
const opening = computed(() => door.value === 'console' && siteEntry(route.query) !== null);
const toConsole = () => void router.replace('/console');

watch(
  door,
  (value) => {
    if (value === 'console') void onward();
  },
  { immediate: true },
);

const mode = computed<'create' | 'login'>(() => (door.value === 'create' ? 'create' : 'login'));
</script>

<template>
  <div class="door row items-center justify-center">
    <div class="door-inner">
      <h1 class="wordmark">
        <img :src="productLogoFor($q.dark.isActive)" :alt="productTitle" width="1066" height="365" />
      </h1>
      <!-- Which build this is, below the logo: the same text as the top bar. -->
      <div class="door-version"><VersionTag /></div>
      <p class="tagline">{{ productTagline }}</p>

      <q-card v-if="opening" flat bordered class="door-card q-pa-md" data-door-opening-site>
        <q-card-section>
          <div class="text-h6">Opening the site</div>
          <div v-if="refusal" class="q-mt-sm" role="alert" data-door-site-refusal>{{ refusal }}</div>
          <div v-else class="row items-center q-gutter-sm q-mt-sm" role="status">
            <q-spinner size="1.25em" aria-hidden="true" />
            <span>One moment…</span>
          </div>
        </q-card-section>
        <q-card-actions v-if="refusal" align="right">
          <q-btn flat no-caps label="Try again" data-door-site-retry @click="onward" />
          <q-btn unelevated no-caps color="primary" label="Go to the Console" data-door-site-console @click="toConsole" />
        </q-card-actions>
      </q-card>

      <q-card v-else flat bordered class="door-card q-pa-md">
        <q-card-section class="q-pb-none">
          <div class="text-h6">
            {{ mode === 'create' ? 'Create the first account' : 'Log in' }}
          </div>
          <div v-if="mode === 'create'" class="door-hint text-caption q-mt-xs">
            This instance has no accounts yet. The first account closes registration; every account
            after it is added from inside.
          </div>
        </q-card-section>

        <q-card-section>
          <AuthForm :mode="mode" @done="onward" />
        </q-card-section>
      </q-card>
    </div>
  </div>
</template>

<style scoped>
.door {
  min-height: 100vh;
  background: var(--os-paper);
  color: var(--os-ink);
  padding: 1rem;
}

.door-inner {
  width: 100%;
  max-width: 26rem;
  text-align: center;
}

.wordmark {
  margin: 0 0 0.35rem;
  line-height: 0;
}

.wordmark img {
  width: min(100%, 18rem);
  height: auto;
}

.door-version {
  margin: 0 0 0.35rem;
}

.tagline {
  color: var(--os-ink-faint);
  font-size: 0.9rem;
  margin: 0 0 1.5rem;
}

.door-card {
  background: var(--os-surface);
  color: inherit;
  text-align: left;
}
.door-hint {
  color: var(--os-ink-muted);
}
</style>
