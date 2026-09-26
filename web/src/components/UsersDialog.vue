<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { useSessionStore } from '../stores/session';
import * as api from '../api/client';
import type { AdminUser } from '../api/client';
import UserCreateDialog from './UserCreateDialog.vue';
import UserPasswordDialog from './UserPasswordDialog.vue';

/**
 * Accounts. Every account reaches every team and every person is an administrator, so there is no
 * tier to set on a row: a row can have its password reset or be deleted, and that is all. Every
 * /api/users route refuses a machine principal on its own, so this screen is not the boundary.
 *
 * A LIST AND NOTHING ELSE. Adding, changing a password and deleting each open their own dialog,
 * the same shape the Agents screen took: this component owns the loading and the writing, the
 * editors own the fields, and a refusal lands in the editor the person is still looking at rather
 * than behind the list they have already stopped reading.
 *
 * The inline forms this replaces carried a comment saying no nested `q-dialog` existed anywhere in
 * this app - true when it was written and false by the time it mattered. It also conflated two
 * different things: `$q.dialog()` is the PLUGIN, which is deliberately not registered
 * (quasar.config.ts lists Notify alone) and would fail at runtime; a `<q-dialog>` in a template is
 * ordinary markup and always worked. Every dialog here is the second kind.
 *
 * `await load()` after every mutation rather than patching what is held: the list is the server's
 * answer, and a refetch is one rule for every mutation rather than a patch per route.
 */
const open = defineModel<boolean>({ required: true });

const session = useSessionStore();
const $q = useQuasar();

const users = ref<AdminUser[]>([]);
const busy = ref(false);

/** The list-level failure: a load that could not happen, or a DELETE, which has no editor of its
 *  own to hold a refusal. Every other failure goes to the dialog that caused it. */
const error = ref('');

/** In flight for whichever editor is open, and the refusal it met. Shared because only one editor
 *  is ever open at a time - two of these would be two states that can disagree. */
const formBusy = ref(false);
const formError = ref('');

const createOpen = ref(false);

const resetting = ref<AdminUser | null>(null);
const resetOpen = ref(false);

/** The id currently mid-DELETE, not a plain boolean: a boolean would disable every row's bin for
 *  the duration of one row's request, which reads as the whole list having frozen. */
const removingId = ref<string | null>(null);

/** The account a delete has been asked for but not yet confirmed. */
const confirming = ref<AdminUser | null>(null);

const rowBusy = computed(() => busy.value || formBusy.value || removingId.value !== null);

/**
 * The server refuses deleting the LAST account (see UserEndpoints.MapDelete's own guard) but has no
 * self-check at all. Deleting the row you are signed in as SUCCEEDS: the dialog's refetch then
 * resolves the caller against a database row that no longer exists, and the 14-day cookie left
 * behind names nobody. There is no confirm step server-side and no refusal to fall back on, so this
 * is the one place in this dialog where the client has to be the guard.
 */
const isSelf = (user: AdminUser) => user.id === session.user?.id;

async function load() {
  busy.value = true;
  error.value = '';

  try {
    users.value = await api.listUsers();
  } catch (failure) {
    error.value = (failure as Error).message;
    users.value = [];
  } finally {
    busy.value = false;
  }
}

// Reset on every OPENING rather than at mount: this is constructed once at layout mount and reused
// for the life of the tab, so state left behind is state the next opening starts in.
watch(open, (showing) => {
  if (!showing) return;

  createOpen.value = false;
  resetOpen.value = false;
  confirming.value = null;
  error.value = '';
  formError.value = '';
  void load();
});

function openCreate() {
  formError.value = '';
  createOpen.value = true;
}

async function createUser(form: { email: string; password: string }) {
  formBusy.value = true;
  formError.value = '';

  try {
    await api.createUser(form.email, form.password);
    $q.notify({ type: 'positive', message: `${form.email} created.` });

    createOpen.value = false;
    await load();
  } catch (failure) {
    // Left in the dialog rather than pre-empted here: a duplicate email is the server's call, not
    // a count this list can trust the freshness of.
    formError.value = (failure as Error).message;
  } finally {
    formBusy.value = false;
  }
}

function openReset(user: AdminUser) {
  formError.value = '';
  resetting.value = user;
  resetOpen.value = true;
}

async function savePassword(password: string) {
  if (!resetting.value) return;

  formBusy.value = true;
  formError.value = '';

  try {
    await api.resetPassword(resetting.value.id, password);
    $q.notify({ type: 'positive', message: `Password changed for ${resetting.value.email}.` });
    resetOpen.value = false;

    // Nothing in the list actually changes here - a password is not displayed - but every other
    // mutation refetches, and a reset that quietly skipped it would be the one exception somebody
    // has to remember.
    await load();
  } catch (failure) {
    formError.value = (failure as Error).message;
  } finally {
    formBusy.value = false;
  }
}

async function remove(user: AdminUser) {
  // Guards a double-click, not a re-entrant call: without it a second DELETE lands after the row is
  // already gone, the server answers 404 "No such user.", and a delete that fully succeeded paints
  // a red error under a row that is correctly no longer there.
  if (removingId.value !== null) return;

  removingId.value = user.id;
  error.value = '';
  confirming.value = null;

  try {
    await api.deleteUser(user.id);
    await load();
  } catch (failure) {
    // The server refuses deleting the last account (409, with a sentence). Not pre-empted here:
    // the count this list holds is a moment old, and the database is the one that knows.
    error.value = (failure as Error).message;
  } finally {
    removingId.value = null;
  }
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="users-card os-dialog-md">
      <q-card-section class="q-pb-none row items-center">
        <div>
          <div class="os-dialog-title">Users</div>
          <div class="text-caption os-text-muted">
            Every account reaches every team. There is no per-team access list.
          </div>
        </div>
        <q-space />
        <span class="row-btn-wrap">
          <q-btn flat dense round icon="refresh" aria-label="Refresh" :disable="rowBusy" @click="load" />
          <q-tooltip>Reload the list.</q-tooltip>
        </span>
      </q-card-section>

      <q-card-section class="users-body q-pt-sm">
        <q-inner-loading :showing="busy" />

        <q-list v-if="users.length > 0" bordered separator>
          <q-item v-for="user in users" :key="user.id">
            <q-item-section avatar>
              <q-icon name="account_circle" />
            </q-item-section>

            <q-item-section>
              <q-item-label>{{ user.email }}</q-item-label>
              <!-- No caption: nothing besides the email varies between rows. -->
            </q-item-section>

            <q-item-section side>
              <!-- Every icon carries its tooltip on a WRAPPER rather than inside the button. A
                   disabled control swallows pointer events, so a nested tooltip fires only while the
                   button is live - which is exactly backwards, since the disabled state is when
                   somebody most needs telling why. -->
              <div class="row items-center no-wrap">
                <span class="row-btn-wrap">
                  <q-btn
                    flat
                    dense
                    round
                    icon="vpn_key"
                    aria-label="Change password"
                    :disable="rowBusy"
                    @click="openReset(user)"
                  />
                  <q-tooltip>Change this account's password.</q-tooltip>
                </span>

                <span class="row-btn-wrap">
                  <q-btn
                    flat
                    dense
                    round
                    color="negative"
                    icon="delete"
                    aria-label="Delete user"
                    :disable="rowBusy || isSelf(user)"
                    :loading="removingId === user.id"
                    @click="confirming = user"
                  />
                  <!-- The server refuses the LAST account (shown in place on refusal) but has no
                       self-check at all - see isSelf's own comment. This is the one place this
                       dialog pre-empts a server call rather than showing its refusal, because there
                       is no refusal to show: the request would succeed and leave the signed-in
                       account deleted. -->
                  <q-tooltip v-if="isSelf(user)">
                    You cannot delete the account you are signed in as. Sign out and have another
                    person remove it instead.
                  </q-tooltip>
                  <q-tooltip v-else>Delete this account.</q-tooltip>
                </span>
              </div>
            </q-item-section>
          </q-item>
        </q-list>

        <div v-else-if="!busy" class="os-text-muted q-pa-lg text-center">
          No users yet. Add one below.
        </div>
      </q-card-section>

      <q-card-section v-if="error" class="q-py-sm text-negative">{{ error }}</q-card-section>

      <q-separator />

      <q-card-actions align="right">
        <q-btn
          flat
          no-caps
          icon="person_add"
          label="Add a user"
          :disable="rowBusy"
          @click="openCreate"
        />
        <q-btn v-close-popup flat no-caps label="Close" />
      </q-card-actions>
    </q-card>
  </q-dialog>

  <UserCreateDialog
    v-model="createOpen"
    :busy="formBusy"
    :error="formError"
    @save="createUser"
  />

  <UserPasswordDialog
    v-model="resetOpen"
    :user="resetting"
    :busy="formBusy"
    :error="formError"
    @save="savePassword"
  />

  <!-- Its own `q-dialog` in this template rather than `$q.dialog()`: the Dialog PLUGIN is
       deliberately not registered, so that call would compile and fail at runtime. Deleting an
       account cannot be undone, and its button sits beside a harmless one - so it asks. -->
  <q-dialog :model-value="confirming !== null" @update:model-value="confirming = null">
    <q-card class="os-dialog-sm">
      <q-card-section class="os-dialog-title">Delete {{ confirming?.email }}?</q-card-section>

      <q-card-section class="q-pt-none os-text-muted">
        The account goes. Anything it created stays. This cannot be undone.
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat no-caps label="Cancel" />
        <q-btn
          unelevated
          color="negative"
          no-caps
          label="Delete"
          @click="confirming && remove(confirming)"
        />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* A CAP with its own scroll: a tenant with thirty accounts would otherwise get a dialog taller than
   the viewport, with the Add button below the fold. */
.users-body {
  position: relative;
  min-height: 8rem;
  max-height: 22rem;
  overflow-y: auto;
}

/* The tooltip lives on this wrapper because a disabled q-btn swallows pointer events, so one nested
   inside would never fire - and the disabled state is when it is most worth reading. */
.row-btn-wrap {
  display: inline-flex;
}
</style>
