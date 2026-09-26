<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { useSessionStore } from '../stores/session';
import PasswordField from './PasswordField.vue';
import { listKeys, mintKey, revokeKey } from '../api/client';
import type { ApiKey, MintedKey } from '../api/types';
import { MINIMUM_PASSWORD_LENGTH, email as emailRule, password as passwordRule, required } from '../lib/rules';
import { passes } from '../lib/forms';

/**
 * The signed-in account's own credentials.
 *
 * TWO forms, not one. An address and a password are confirmed by different things and fail for
 * different reasons, so each has its own fields, its own button and its own error line - and one
 * of them failing never discards what was typed into the other.
 *
 * There is no email verification loop anywhere in this product: no mail is sent, so a new address
 * is confirmed by typing it twice rather than by following a link. That is what the second field
 * is for, and it is the whole of what it is for.
 */
const open = defineModel<boolean>({ required: true });

const session = useSessionStore();
const $q = useQuasar();

/**
 * Which panel is showing. Reset when the dialog OPENS rather than remembered across visits, so it
 * always opens the same way — a dialog that reopens wherever you left it is one you have to read
 * before you can use it.
 *
 * Switching tabs deliberately does NOT clear anything. The three sections keep their own fields,
 * their own error line and their own busy flag, so looking at another tab mid-edit must not
 * discard what was typed.
 */
const DefaultTab = 'email';
const tab = ref(DefaultTab);

const currentEmail = ref('');
const newEmail = ref('');
const confirmEmail = ref('');
const emailError = ref('');
const emailBusy = ref(false);

const currentPassword = ref('');
const newPassword = ref('');
const confirmPassword = ref('');
const passwordError = ref('');
const passwordBusy = ref(false);

const MinimumPasswordLength = MINIMUM_PASSWORD_LENGTH;

const emailRules = [emailRule];

/** The confirmation is judged against the first box, so its rules are rebuilt when that changes. */
const confirmEmailRules = computed(() => [
  emailRule,
  (value: unknown) =>
    String(value ?? '').trim().toLowerCase() === newEmail.value.trim().toLowerCase() ||
    'The two addresses do not match.',
]);

const currentPasswordRules = [required('Enter your current password.')];
const newPasswordRules = [passwordRule];

const keys = ref<ApiKey[]>([]);
const keyLabel = ref('');
const keyError = ref('');
const keyBusy = ref(false);
const confirmingRevoke = ref<string | null>(null);
const revokeBusy = ref(false);

/**
 * The credential, held only until this dialog closes. There is no way to fetch it back — the
 * server keeps a SHA-256 hash — so this ref is the whole of its lifetime in the browser.
 */
const justMinted = ref<MintedKey | null>(null);

// Cleared every time it opens rather than once on mount: the dialog outlives its own closing, and
// a password left in a field by a cancelled edit is a password sitting in the DOM until reload.
// The same reasoning applies harder to a credential, which is why justMinted is cleared here too —
// AND on close, below, so the comment above it is actually true rather than describing a
// safeguard the code does not have.
watch(open, (showing) => {
  if (!showing) {
    justMinted.value = null;
    return;
  }

  tab.value = DefaultTab;

  currentEmail.value = '';
  newEmail.value = '';
  confirmEmail.value = '';
  emailError.value = '';

  currentPassword.value = '';
  newPassword.value = '';
  confirmPassword.value = '';
  passwordError.value = '';

  keyLabel.value = '';
  keyError.value = '';
  justMinted.value = null;
  confirmingRevoke.value = null;
  void loadKeys();
});

const canSubmitEmail = computed(
  () =>
    passes(currentEmail.value, emailRules) &&
    passes(newEmail.value, emailRules) &&
    passes(confirmEmail.value, confirmEmailRules.value),
);

const passwordsMatch = computed(() => newPassword.value === confirmPassword.value);

const canSubmitPassword = computed(
  () =>
    passes(currentPassword.value, currentPasswordRules) &&
    passes(newPassword.value, newPasswordRules) &&
    passwordsMatch.value,
);

/** A host that predates an endpoint answers 404, which as a message tells nobody anything. */
function reasonFor(failure: unknown) {
  const message = (failure as Error).message;

  return message.startsWith('404')
    ? 'This instance is running an older build. Restart Harness and try again.'
    : message;
}

async function submitEmail() {
  if (!canSubmitEmail.value) return;

  emailError.value = '';
  emailBusy.value = true;

  try {
    await session.changeEmail(currentEmail.value.trim(), newEmail.value.trim());

    $q.notify({ type: 'positive', message: 'Your email is updated.' });
    open.value = false;
  } catch (failure) {
    emailError.value = reasonFor(failure);
  } finally {
    emailBusy.value = false;
  }
}

async function submitPassword() {
  if (!canSubmitPassword.value) return;

  passwordError.value = '';
  passwordBusy.value = true;

  try {
    await session.changePassword(currentPassword.value, newPassword.value);

    // Said out loud because nothing on screen changes. An address appears in the corner a second
    // later; a password leaves no trace at all, so silence would read as failure.
    $q.notify({ type: 'positive', message: 'Your password is updated.' });
    open.value = false;
  } catch (failure) {
    passwordError.value = reasonFor(failure);
  } finally {
    passwordBusy.value = false;
  }
}

async function loadKeys() {
  try {
    keys.value = await listKeys();
  } catch (cause) {
    keyError.value = cause instanceof Error ? cause.message : String(cause);
  }
}

async function submitKey() {
  keyError.value = '';
  keyBusy.value = true;

  try {
    justMinted.value = await mintKey(keyLabel.value.trim());
    keyLabel.value = '';
    await loadKeys();
  } catch (cause) {
    keyError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    keyBusy.value = false;
  }
}

async function confirmRevoke(id: string) {
  keyError.value = '';
  revokeBusy.value = true;

  try {
    await revokeKey(id);
    confirmingRevoke.value = null;

    // The key just destroyed may be the one whose credential is on screen. Clearing it stops the
    // dialog showing a value that no longer authenticates anything.
    if (justMinted.value?.id === id) justMinted.value = null;

    await loadKeys();
  } catch (cause) {
    keyError.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    revokeBusy.value = false;
  }
}

function copyCredential() {
  if (!justMinted.value) return;

  // `clipboard` is undefined outside a secure context, which includes plain http on a LAN address —
  // exactly how this instance is reached. That has to be its own branch: optional chaining on
  // `navigator.clipboard?.writeText(...)` would short-circuit the whole `.then`/`.catch` chain to
  // `undefined` when clipboard is absent, leaving the click silently do nothing.
  if (!navigator.clipboard) {
    $q.notify({ message: 'Could not copy — select it and copy by hand.' });
    return;
  }

  navigator.clipboard
    .writeText(justMinted.value.credential)
    .then(() => $q.notify({ message: 'Copied.', color: 'positive' }))
    .catch(() => $q.notify({ message: 'Could not copy — select it and copy by hand.' }));
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="profile-card os-dialog-sm">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">User profile</div>
        <div class="text-caption os-text-muted q-mt-xs">
          Signed in as <strong>{{ session.user?.email }}</strong>
        </div>
      </q-card-section>

      <q-tabs v-model="tab" dense no-caps align="justify" class="text-primary">
        <q-tab name="email" label="Email" />
        <q-tab name="password" label="Password" />
        <q-tab name="keys" label="API keys" />
      </q-tabs>

      <q-separator />

      <!--
        `keep-alive` is deliberate: without it Quasar unmounts the panel you leave, so a
        half-typed address or an armed Revoke would be thrown away by glancing at another tab.
        The min-height stops the card resizing under the pointer as you switch.
      -->
      <q-tab-panels v-model="tab" animated keep-alive class="os-tab-panels profile-panels">
        <!-- Change email ----------------------------------------------------------------- -->
        <q-tab-panel name="email">
          <q-form @submit="submitEmail">
          <q-input
            v-model="currentEmail"
            type="email"
            label="Current email"
            autocomplete="username"
            outlined
            dense
            lazy-rules
            :rules="emailRules"
            :disable="emailBusy"
            class="q-mb-md"
          />

          <q-input
            v-model="newEmail"
            type="email"
            label="New email"
            autocomplete="email"
            outlined
            dense
            lazy-rules
            :rules="emailRules"
            :disable="emailBusy"
            class="q-mb-md"
          />

          <q-input
            v-model="confirmEmail"
            type="email"
            label="Confirm new email"
            autocomplete="email"
            outlined
            dense
            lazy-rules
            :rules="confirmEmailRules"
            :disable="emailBusy"
          />

          <div v-if="emailError" class="text-negative q-mt-sm">{{ emailError }}</div>

          <div class="row justify-end q-mt-md">
            <q-btn
              type="submit"
              unelevated
              no-caps
              color="primary"
              label="Update email"
              :loading="emailBusy"
              :disable="!canSubmitEmail || emailBusy"
            />
          </div>
          </q-form>
        </q-tab-panel>

        <!-- Change password -------------------------------------------------------------- -->
        <q-tab-panel name="password">
          <q-form @submit="submitPassword">
          <PasswordField
            v-model="currentPassword"
            label="Current password"
            autocomplete="current-password"
            :rules="currentPasswordRules"
            :disable="passwordBusy"
            class="q-mb-md"
          />

          <PasswordField
            v-model="newPassword"
            label="New password"
            autocomplete="new-password"
            :hint="`At least ${MinimumPasswordLength} characters`"
            :rules="newPasswordRules"
            :disable="passwordBusy"
            class="q-mb-md"
          />

          <PasswordField
            v-model="confirmPassword"
            label="Confirm new password"
            autocomplete="new-password"
            :disable="passwordBusy"
          />

          <div v-if="confirmPassword.length > 0 && !passwordsMatch" class="text-negative q-mt-sm">
            The two passwords do not match.
          </div>

          <div v-if="passwordError" class="text-negative q-mt-sm">{{ passwordError }}</div>

          <div class="row justify-end q-mt-md">
            <q-btn
              type="submit"
              unelevated
              no-caps
              color="primary"
              label="Update password"
              :loading="passwordBusy"
              :disable="!canSubmitPassword || passwordBusy"
            />
          </div>
          </q-form>

          <!--
            Lives here rather than under the Close button, where it sat on every view. It is about
            passwords and nothing else, and this is the one panel whose reader is about to need it.
          -->
          <div class="text-caption os-text-muted q-mt-lg">
            There is no self-service password recovery. If you lose this password, getting back in
            needs an operator at the machine running <code>--reset-password</code> on the host — it
            cannot be done from here, over the network, or from the CLI.
          </div>
        </q-tab-panel>

        <!-- API keys --------------------------------------------------------------------- -->
        <q-tab-panel name="keys">
          <div class="text-caption os-text-muted q-mb-sm">
          A key acts as you, reaching whatever you reach at the time it is used. It carries no
          account administration.
        </div>

        <!--
          NO q-col-gutter HERE, and that is the alignment fix rather than a tidy-up. That class
          spaces children by giving them `padding-top: 8px`, and `.q-btn`'s own padding rule beats
          it on source order at equal specificity - so the input took the 8px and the button did
          not, and the button rode 8px above the field it belongs beside. Margin does not lose that
          fight, hence q-ml-sm on the button. The input also hides its bottom space: it reserves
          20px for a hint or an error it never shows, since keyError renders on its own line below.
        -->
        <q-form class="row items-center no-wrap" @submit="submitKey">
          <q-input
            v-model="keyLabel"
            class="col"
            dense
            outlined
            hide-bottom-space
            label="What is this key for?"
            maxlength="60"
            :disable="keyBusy"
          />
          <q-btn
            class="col-auto q-ml-sm"
            color="primary"
            label="Mint"
            type="submit"
            :disable="!keyLabel.trim()"
            :loading="keyBusy"
          />
        </q-form>

        <q-banner v-if="justMinted" class="os-bg-tint-warn q-mt-sm" dense>
          <div class="text-weight-medium">Copy this now. It is not shown again.</div>
          <div class="row items-center no-wrap q-gutter-sm q-mt-xs">
            <code class="col ellipsis">{{ justMinted.credential }}</code>
            <q-btn dense flat icon="content_copy" @click="copyCredential" />
          </div>
        </q-banner>

        <div v-if="keyError" class="text-negative os-body q-mt-sm">{{ keyError }}</div>

        <q-list v-if="keys.length" dense class="q-mt-sm">
          <q-item v-for="key in keys" :key="key.id">
            <q-item-section>
              <q-item-label>{{ key.label ?? '(unnamed)' }}</q-item-label>
              <q-item-label caption>
                {{ key.prefix }}… · last used
                {{ key.lastUsedAt ? new Date(key.lastUsedAt).toLocaleString() : 'never' }}
              </q-item-label>
            </q-item-section>
            <q-item-section side>
              <q-btn
                v-if="confirmingRevoke === key.id"
                dense
                color="negative"
                label="Confirm"
                :loading="revokeBusy"
                :disable="revokeBusy"
                @click="confirmRevoke(key.id)"
              />
              <q-btn
                v-else
                dense
                flat
                label="Revoke"
                :disable="revokeBusy"
                @click="confirmingRevoke = key.id"
              />
            </q-item-section>
          </q-item>
        </q-list>
          <div v-else class="os-body os-text-muted q-mt-sm">No keys.</div>
        </q-tab-panel>
      </q-tab-panels>

      <q-separator />

      <q-card-actions align="right">
        <q-btn flat no-caps label="Close" @click="open = false" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* THE HEIGHT, NOT A FLOOR. A `min-height` would let the keys panel grow past it and the card never
   come back down. See `.os-tab-panels` in `css/app.scss` for the rule; this names only the size.
   The three panels are different heights — email and password are three fields each, keys grows
   with however many you hold. Without a floor the card jumps as you move between tabs, which
   moves the tab bar out from under the pointer. */
.profile-panels {
  --os-tab-panels-height: 21rem;
}
</style>
