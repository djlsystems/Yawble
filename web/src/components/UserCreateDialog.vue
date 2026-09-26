<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import type { QForm } from 'quasar';
import { MINIMUM_PASSWORD_LENGTH, email as emailRule, password as passwordRule } from '../lib/rules';
import { passes, submitOnEnter } from '../lib/forms';
import PasswordField from './PasswordField.vue';

/**
 * A new account: an email and a password, in its own dialog.
 *
 * CREATE-ONLY. This was `UserEditDialog`, which also edited an existing account's tier; the tier is
 * gone - every person is an administrator - and an existing account has nothing left to edit here.
 * Its password is reset from `UserPasswordDialog`, its email is changed by its owner from their own
 * profile, and there is no `PATCH /api/users/{id}` to send anything else to.
 *
 * PRESENTATIONAL, like the Agent and Prompt editors: it emits what was filled in and the list
 * saves. `busy` and `error` are props for the reason they are there, which is the whole point of
 * moving this out of an inline section: the parent saves, and on a REFUSAL it leaves this open with
 * the server's words in it. A duplicate email is the server's call, and reporting it behind the
 * list somebody was just typing into reads as nothing having happened.
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{
  busy: boolean;
  error: string;
}>();

const emit = defineEmits<{
  save: [{ email: string; password: string }];
}>();

const email = ref('');
const password = ref('');

watch(
  open,
  (showing) => {
    if (!showing) return;

    email.value = '';
    password.value = '';
  },
  { immediate: true },
);

const emailRules = [emailRule];
const passwordRules = [passwordRule];

const valid = computed(() => passes(email.value, emailRules) && passes(password.value, passwordRules));

const form = ref<QForm | null>(null);
const onEnter = submitOnEnter(() => form.value, () => valid.value && !props.busy);

function submit() {
  if (!valid.value || props.busy) return;

  emit('save', {
    email: email.value.trim(),
    password: password.value,
  });
}
</script>

<template>
  <q-dialog v-model="open" no-backdrop-dismiss>
    <q-card class="os-dialog-md">
      <q-form ref="form" @submit="submit" @keydown="onEnter">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">New user</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" :disable="props.busy" />
      </q-card-section>

      <q-card-section class="q-gutter-md">
        <q-input
          v-model="email"
          type="email"
          dense
          outlined
          autofocus
          label="Email"
          autocomplete="off"
          lazy-rules
          :rules="emailRules"
          :disable="props.busy"
        />

        <PasswordField
          v-model="password"
          label="Password"
          autocomplete="new-password"
          :hint="`At least ${MINIMUM_PASSWORD_LENGTH} characters`"
          :rules="passwordRules"
          :disable="props.busy"
        />

        <div class="text-caption os-text-muted">
          Every account reaches every team and can manage accounts and instance settings. There is
          no per-team access list.
        </div>

        <q-banner v-if="props.error" dense class="os-bg-tint-error text-negative">
          <template #avatar><q-icon name="error" /></template>
          {{ props.error }}
        </q-banner>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps label="Cancel" :disable="props.busy" />
        <q-btn
          type="submit"
          unelevated
          color="primary"
          no-caps
          label="Create"
          :loading="props.busy"
          :disable="!valid || props.busy"
        />
      </q-card-actions>
      </q-form>
    </q-card>
  </q-dialog>
</template>
