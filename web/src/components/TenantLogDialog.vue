<script setup lang="ts">
import { ref, watch } from 'vue';
import { readTenantLog } from '../api/client';
import type { TenantEvent } from '../api/types';
import { useCursorList } from '../lib/useCursorList';
import CursorSentinel from './CursorSentinel.vue';

/**
 * The tenant log: who did what, administratively.
 *
 * NOT a member's activity feed. That comes from the message log — the causal stream Agent Containers
 * publish to and read from, every type in it `agentContainer.*` — and shows what a member DID. This
 * shows what a person did TO the instance, and the two never carry the same row.
 *
 * SERVER-SIDE, BY CURSOR. The list reads `?before=<seq>` pages as the sentinel at its bottom
 * scrolls into view, and renders through a virtual scroll so ten thousand rows cost what forty do. A
 * grid that fetched everything would look identical on a small instance and stop working on a large
 * one — and an audit log only ever grows.
 *
 * ROWS ARE ONLY APPENDED AT THE BOTTOM, and every one is older than what is above it, so a row that
 * arrives while this is open never moves the row under the pointer. A row written after opening is
 * not shown until the next opening; that is the trade for a list that holds still.
 *
 * Read-only by construction: there is no write here and no delete on the server either. An audit
 * trail with an edit button is not one.
 */
const open = defineModel<boolean>({ required: true });

/** The server's count of every row, for the caption. Never `rows.length` - that is what is loaded. */
const total = ref(0);

const list = useCursorList<TenantEvent>(
  async (before, take) => {
    const page = await readTenantLog(before, take);
    total.value = page.total;
    return page.events;
  },
  (row) => row.seq,
);
const { rows, loading, exhausted, error } = list;

/** Local time, because the reader is a person deciding whether this was them ten minutes ago. The
 *  wire carries a round-trip UTC string, so nothing here depends on the server's timezone. */
function when(value: string) {
  return new Date(value).toLocaleString();
}

const wording: Record<string, string> = {
  'user.signed-in': 'signed in',
  'user.signed-out': 'signed out',
  'user.created': 'created account',
  'user.changed': 'changed account',
  'user.deleted': 'deleted account',
  'user.password-reset': 'reset password',
  'team.created': 'created team',
  'team.renamed': 'relabelled team',
  'team.cloned': 'cloned team',
  'team.deleted': 'deleted team',
  'team.concierge-changed': 'changed Concierge',
  'member.added': 'added member',
  'member.changed': 'changed member',
  'agents.saved': 'saved Agent catalog',
  'key.minted': 'minted an API key',
  'key.revoked': 'revoked an API key',
};

/** Destructive acts read differently, because they are the ones somebody scans for. */
function destructive(action: string) {
  return action === 'team.deleted' || action === 'user.deleted' || action === 'key.revoked';
}

watch(open, (showing) => {
  if (!showing) return;

  // Back to the newest rows on every opening. Somebody reopening this is asking "what just
  // happened", not resuming where they left off.
  void list.reset();
});
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="tenant-log-card os-dialog-xl">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Log</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" />
      </q-card-section>

      <q-card-section class="os-body os-text-muted q-pt-xs">
        Administrative acts, newest first — teams, accounts and access. A member's own activity is on
        its card and comes from somewhere else.
      </q-card-section>

      <q-card-section class="q-pt-none">
        <q-banner v-if="error" dense class="os-bg-tint-error text-negative q-mb-md">
          <template #avatar><q-icon name="error" /></template>
          {{ error }}
        </q-banner>

        <div class="os-body os-text-muted q-mb-xs">
          {{ rows.length }} of {{ total }} shown
        </div>

        <!-- A VIRTUAL SCROLL, NOT A PAGED TABLE. The header row is sticky so a column can
             still be named forty rows down, and the sentinel in the footer asks for older rows as
             it comes into view. -->
        <q-virtual-scroll
          type="table"
          class="tenant-log-scroll"
          dense
          flat
          bordered
          :items="rows"
          :virtual-scroll-item-size="33"
          :virtual-scroll-sticky-size-start="33"
        >
          <template #before>
            <thead class="tenant-log-head">
              <tr>
                <th class="text-left">When</th>
                <th class="text-left">Who</th>
                <th class="text-left">Did</th>
                <th class="text-left">To</th>
                <th class="text-left">Detail</th>
              </tr>
            </thead>
          </template>

          <template #default="{ item: row }">
            <tr :key="row.seq">
              <td class="tenant-log-when">{{ when(row.occurredAt) }}</td>
              <td>{{ row.actorEmail }}</td>
              <td :class="destructive(row.action) ? 'text-negative' : ''">
                {{ wording[row.action] ?? row.action }}
              </td>
              <td>{{ row.subjectName }}</td>
              <!-- A deletion's detail is JSON and can be long. Truncated in the cell with the whole
                   of it in a tooltip: the counts are why the row is worth keeping, but a column that
                   wraps to six lines makes every other row unreadable. -->
              <td class="tenant-log-detail">
                <span v-if="row.detail">{{ row.detail }}</span>
                <q-tooltip v-if="row.detail" max-width="480px">{{ row.detail }}</q-tooltip>
              </td>
            </tr>
          </template>

          <template #after>
            <tfoot>
              <tr>
                <td colspan="5">
                  <CursorSentinel
                    :loading="loading"
                    :exhausted="exhausted"
                    :error="error"
                    :done-label="rows.length ? 'The start of the log.' : 'Nothing yet.'"
                    @more="list.loadMore"
                  />
                </td>
              </tr>
            </tfoot>
          </template>
        </q-virtual-scroll>
      </q-card-section>

      <q-card-actions align="right">
        <q-btn v-close-popup flat label="Close" />
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
/* Bounded, so the dialog body scrolls rather than the page; the footer sentinel is at its bottom. */
.tenant-log-scroll {
  max-height: min(62vh, 640px);
}

.tenant-log-head th {
  position: sticky;
  top: 0;
  z-index: 1;
  background: var(--os-surface);
}

.tenant-log-when {
  white-space: nowrap;
}

.tenant-log-detail {
  max-width: 280px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
