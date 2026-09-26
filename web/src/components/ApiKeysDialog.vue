<script setup lang="ts">
import { ref, watch } from 'vue';
import { listAllKeys, revokeKey } from '../api/client';
import type { TenantApiKey } from '../api/types';

/**
 * Every API key in the tenant, with its owner.
 *
 * A second READER over a relation with one writer — the same shape as the team Access view. The one
 * thing it may write is a revocation, because offboarding needs somebody who can revoke. There is
 * deliberately no mint here: nobody needs to create a credential that acts as somebody else, which
 * would be impersonation with no trace distinguishing it from that person's own key.
 *
 * API KEYS ONLY. Container and Concierge credentials are machinery, and a row somebody can
 * revoke is a row somebody will revoke — killing a running container's ability to call the CLI and
 * surfacing much later as an unrelated permission error. The server filters them out; this dialog
 * must never grow a switch that asks for them.
 */
const open = defineModel<boolean>({ required: true });

const rows = ref<TenantApiKey[]>([]);
const loading = ref(false);
const error = ref('');
const confirming = ref<string | null>(null);
const revoking = ref(false);

async function load() {
  loading.value = true;
  error.value = '';

  try {
    rows.value = await listAllKeys();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    loading.value = false;
  }
}

async function revoke(id: string) {
  revoking.value = true;

  try {
    await revokeKey(id);
    confirming.value = null;
    await load();
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    revoking.value = false;
  }
}

watch(open, (showing) => {
  // Cleared on every opening, same as ProfileDialog's equivalent flag: without this, arming
  // "Revoke" on a row, closing the dialog and reopening it left `confirming` set on a row that had
  // already reloaded — one click on a red "Confirm" button destroying whichever credential now
  // happened to render in that slot.
  confirming.value = null;

  if (showing) void load();
});
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="keys-card os-dialog-lg">
      <q-card-section class="q-pb-none">
        <div class="os-dialog-title">API keys</div>
        <div class="text-caption os-text-muted q-mt-xs">
          Every key on this instance. Keys are minted by their owners, in their own profile — this
          view can revoke, and nothing else.
        </div>
      </q-card-section>

      <q-card-section>
        <div v-if="error" class="text-negative os-body q-mb-sm">{{ error }}</div>

        <q-list v-if="rows.length" separator>
          <q-item v-for="row in rows" :key="row.id">
            <q-item-section>
              <q-item-label>{{ row.label ?? '(unnamed)' }}</q-item-label>
              <q-item-label caption>
                {{ row.owner.email ?? row.owner.id }} · {{ row.prefix }}… · last used
                {{ row.lastUsedAt ? new Date(row.lastUsedAt).toLocaleString() : 'never' }}
              </q-item-label>
              <!-- No team column: every key reaches every team its owner does, and every person
                   reaches every team, so a list here would be wrong the moment somebody creates a
                   team. -->
            </q-item-section>
            <q-item-section side>
              <q-btn
                v-if="confirming === row.id"
                dense
                color="negative"
                label="Confirm"
                :loading="revoking"
                :disable="revoking"
                @click="revoke(row.id)"
              />
              <q-btn
                v-else
                dense
                flat
                label="Revoke"
                :disable="revoking"
                @click="confirming = row.id"
              />
            </q-item-section>
          </q-item>
        </q-list>
        <div v-else-if="!loading" class="os-body os-text-muted">No keys.</div>
        <q-inner-loading :showing="loading" />
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat label="Close" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
</style>
