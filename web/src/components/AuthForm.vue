<script setup lang="ts">
import { computed, ref } from 'vue';
import type { QForm } from 'quasar';
import { useSessionStore } from '../stores/session';
import { email as emailRule, password as passwordRule, required } from '../lib/rules';
import { passes, submitOnEnter } from '../lib/forms';
import PasswordField from './PasswordField.vue';

/**
 * The two credential forms, which are one form. Creating the first account and logging in differ
 * only in which endpoint they call and what the button says - keeping them as one component is
 * what stops the landing dialog and the /login page drifting into two behaviours.
 */
const props = defineProps<{ mode: 'create' | 'login' }>();
const emit = defineEmits<{ done: [] }>();

const session = useSessionStore();

const email = ref('');
const password = ref('');
const error = ref('');
const busy = ref(false);

const emailRules = [required('Enter your email address.'), emailRule];

/** Logging in checks only that something was typed: the length rule is for CHOOSING a password,
 *  and an account made before it existed must still be able to sign in. */
const passwordRules = computed(() =>
  props.mode === 'create' ? [required('Enter a password.'), passwordRule] : [required('Enter your password.')],
);

const valid = computed(() => passes(email.value, emailRules) && passes(password.value, passwordRules.value));

const form = ref<QForm | null>(null);
const onEnter = submitOnEnter(() => form.value, () => valid.value && !busy.value);

async function submit() {
  if (!valid.value || busy.value) return;

  error.value = '';
  busy.value = true;

  try {
    const address = email.value.trim();

    if (props.mode === 'create') {
      await session.createFirstAccount(address, password.value);
    } else {
      await session.signIn(address, password.value);
    }

    emit('done');
  } catch (failure) {
    // A 401 here means the credentials were wrong, not that the session expired - so it gets a
    // sentence a person can act on rather than the client's "Not signed in." Anything else is the
    // server's own `error` - which is how the 429 after too many wrong passwords says how long to
    // wait.
    error.value =
      failure instanceof Error && failure.name === 'Unauthorized'
        ? 'That email and password do not match.'
        : (failure as Error).message;
  } finally {
    busy.value = false;
  }
}
</script>

<template>
  <q-form ref="form" @submit="submit" @keydown="onEnter">
    <!-- No `dark` prop. The front door follows the viewer's theme, and an unset `dark` is Quasar
         following the Dark plugin - so the field ink matches whichever ground it is on. -->
    <q-input
      v-model="email"
      type="email"
      label="Email"
      autocomplete="username"
      outlined
      dense
      lazy-rules
      :rules="emailRules"
      :disable="busy"
      class="q-mb-md"
    />

    <PasswordField
      v-model="password"
      label="Password"
      :disable="busy"
      :rules="passwordRules"
      :autocomplete="mode === 'create' ? 'new-password' : 'current-password'"
    />

    <!-- Stated once, where the password is chosen. There is no self-service reset - recovery needs
         somebody at the machine - so the consequence of losing it belongs next to the field rather
         than in a page nobody reads twice. -->
    <div v-if="mode === 'create'" class="auth-hint text-caption q-mt-sm">
      At least 8 characters. There is no self-service reset — getting back in needs an operator at
      the machine running <code>--reset-password</code> on the host.
    </div>

    <div v-if="error" class="auth-error q-mt-md" role="alert">{{ error }}</div>

    <!-- The brand orange, from the tokens - the same `--os-primary` every other primary control
         in the console wears, rather than a private copy of it. -->
    <q-btn
      type="submit"
      unelevated
      no-caps
      class="full-width q-mt-lg brand-btn"
      :loading="busy"
      :disable="!valid || busy"
      :label="mode === 'create' ? 'Create account' : 'Log in'"
    />
  </q-form>
</template>

<style scoped>
/* Chrome paints its own pale fill over an autofilled field and IGNORES `background`, so a saved
   credential comes back as dark ink on near-white inside a dark dialog, with the floating label
   lost against it. The returning visitor is the only person who ever sees this form twice, which
   makes autofill the common case rather than the edge one.
   The inset shadow is the one thing that beats the fill; -webkit-text-fill-color is likewise the
   only thing that beats its forced ink colour. Deep, because the native input belongs to QInput
   rather than to this template. The absurd transition delay stops the pale fill flashing in on
   focus, which the shadow alone does not cover. */
:deep(input:-webkit-autofill),
:deep(input:-webkit-autofill:hover),
:deep(input:-webkit-autofill:focus),
:deep(input:-webkit-autofill:active) {
  -webkit-box-shadow: 0 0 0 100px var(--os-surface) inset;
  -webkit-text-fill-color: var(--os-ink);
  caret-color: var(--os-ink);
  transition: background-color 100000s ease-in-out 0s;
}

.brand-btn {
  background: var(--os-primary);
  color: var(--os-paper);
  font-weight: 700;
}

.brand-btn:hover {
  background: var(--os-primary-hover);
}

/* A refusal is the one thing in this form that MUST be read, so it does not take the panel's
   ink: the error tokens, which are a deep red on a pale tint in light and a lifted red on a dark
   tint in dark - legible on either ground rather than pastel on pastel. */
.auth-error {
  padding: 0.55rem 0.7rem;
  border-radius: 7px;
  background: var(--os-tint-error);
  border: 1px solid var(--os-error);
  color: var(--os-error);
  font-size: 0.82rem;
}

.auth-hint {
  color: var(--os-ink-muted);
}
</style>
