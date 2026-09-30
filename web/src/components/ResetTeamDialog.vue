<script setup lang="ts">
import { computed, ref, watch } from 'vue';
import { useQuasar } from 'quasar';
import { previewResetRepositories, resetTeam } from '../api/client';
import type { RepositoryResetPreview, Team, TeamWasReset } from '../api/types';
import { choicesFor, repositoryLosses, requestFrom, wouldDoSomething } from '../lib/reset';
import { useConsoleStore } from '../stores/console';

/**
 * Handing the active team a clean slate: KEEP the Agent Containers, reset the substrate underneath
 * them.
 *
 * ONE ENTRY POINT, scope chosen inside. The alternative — a per-member control on each card —
 * spreads one decision across the board and makes "reset the whole team" a sequence of clicks that
 * floors every member at a DIFFERENT point, which is the state the server reads the head once to
 * avoid.
 *
 * Available to any signed-in person, matching the route: there is no tier in this codebase, and
 * a reset destroys none of what a deletion does — the team, its documents and every
 * member's Agent, Prompt and label all survive it. That is why this is NOT
 * behind the same friction the Teams table puts in front of deletion - the typed-name
 * confirmation, which deletion still carries and this deliberately does not.
 *
 * The request-building itself is `lib/reset.ts`, and that placement is what makes it testable:
 * mounted dialog specs exist in this project's Vitest setup now, and the request still belongs as
 * a pure function rather than something a test has to tease back out of rendered controls.
 */
const open = defineModel<boolean>({ required: true });

const props = defineProps<{ team: Team }>();

const $q = useQuasar();
const board = useConsoleStore();

/** Every member on the team, by IDENTIFIER — which is what the request carries and what the server
 *  matches. The label is what gets rendered beside the box. */
const members = computed(() => props.team.containers);

const choices = ref(choicesFor(members.value.map((m) => m.id)));
const busy = ref(false);
const done = ref<TeamWasReset | null>(null);
const failed = ref('');

/** What Reset repositories would remove, read when its box is first ticked in this opening - never
 *  on open, so a reset that leaves code alone asks git nothing. */
const preview = ref<RepositoryResetPreview | null>(null);
const previewFailed = ref('');

/** Re-seeded on every OPEN rather than once, because the team may have gained or lost members since
 *  the last time — and a stale map would silently drop a new member from every reset. */
watch(open, (showing) => {
  if (!showing) return;

  choices.value = choicesFor(members.value.map((m) => m.id));
  done.value = null;
  failed.value = '';
  preview.value = null;
  previewFailed.value = '';
});

watch(
  () => choices.value.resetRepositories,
  async (ticked) => {
    if (!ticked || preview.value) return;

    previewFailed.value = '';

    try {
      preview.value = await previewResetRepositories(props.team.id);
    } catch (cause) {
      previewFailed.value = cause instanceof Error ? cause.message : String(cause);
    }
  },
);

/** The ticked members' trees and branches, and the team branch when every member is ticked. */
const losses = computed(() => (preview.value ? repositoryLosses(preview.value, choices.value) : null));

const ready = computed(() => wouldDoSomething(choices.value));

function labelFor(id: string): string {
  return members.value.find((m) => m.id === id)?.name ?? id;
}

async function reset() {
  if (!ready.value || busy.value) return;

  busy.value = true;
  failed.value = '';

  try {
    // Addressed by `id`, never by `name`. A team's name is the half that moves, and pointing this
    // at one is a 404 for every team that has ever been relabelled — and invisible to any test whose
    // fixtures give a team the same name and id.
    done.value = await resetTeam(props.team.id, requestFrom(choices.value));

    // The board holds this team's cards. Their floors have moved, so refetch rather than trusting
    // what is on screen — the snapshot push clears them already, and this reconciles anything the
    // socket missed.
    await board.refresh();
  } catch (cause) {
    // The server's own wording, which for a 409 names WHO is still working. A bare status tells the
    // person nothing they can act on.
    failed.value = cause instanceof Error ? cause.message : String(cause);
  } finally {
    busy.value = false;
  }
}

function finish() {
  open.value = false;

  if (done.value && done.value.failures.length === 0) {
    $q.notify({
      type: 'positive',
      timeout: 5000,
      message: `${props.team.name} was reset.`,
    });
  }
}
</script>

<template>
  <q-dialog v-model="open" @hide="done && finish()">
    <q-card class="reset-card os-dialog-md">
      <q-card-section class="row items-center q-pb-none">
        <div class="os-dialog-title">Reset {{ team.name }}</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" :disable="busy" />
      </q-card-section>

      <!-- The one sentence that says what this is FOR. Without it the checkboxes read as a
           destructive tool rather than as "start the next task". -->
      <q-card-section class="os-body os-text-muted q-pt-xs">
        The members stay exactly as they are — their names, Agents and access all survive.
        What resets is what they remember, and whatever else you tick below.
      </q-card-section>

      <!-- The RESULT replaces the form rather than sitting under it: the choices no longer describe
           anything that can be acted on, and leaving them live invites a second reset nobody meant. -->
      <q-card-section v-if="done">
        <div class="text-body2 q-mb-sm">
          <template v-if="done.floored.length > 0">
            Reset {{ done.floored.map(labelFor).join(', ') }}.
          </template>
          <template v-else>No memory was deleted.</template>
        </div>

        <div v-if="done.purged > 0 || done.retained > 0" class="text-body2 q-mb-sm">
          Deleted {{ done.purged }} message(s).
          <!-- RETAINED IS NOT A FAILURE, and has to say why or it reads as one: a message that a
               SURVIVING row cites cannot go without destroying that row's causation, which is the
               one field that answers what caused what.

               IT MUST NOT NAME THE CAUSE. The server cannot say whose messages cite them -
               `IMessageLog.DeleteAsync` knows only "cited by something not in the set" - and the
               citer is often a deleted member of the same team or the team's own Concierge, so
               "other teams" would be a guess, and a wrong guess is worse than saying less. -->
          <template v-if="done.retained > 0">
            {{ done.retained }} were kept: messages outside this reset still cite them, and that
            link is the record of what caused what.
          </template>
        </div>

        <!-- NO SESSION LINE, and its absence matches the warning below rather than contradicting
             it. A Concierge is keyed on the person, so a reset ends none and cannot; a session
             count here would always be zero, which says "nobody was connected" over a terminal
             somebody is typing into. -->

        <div v-if="done.cleared.length > 0" class="text-body2 q-mb-sm">
          Cleared {{ done.cleared.length }} folder(s).
        </div>

        <!-- WHAT RESET REPOSITORIES DID, and above all what it KEPT: a tree or branch that would
             have lost work stays, and the person decides what happens to it. -->
        <div v-if="done.repositories" class="reset-repositories-result text-body2 q-mb-sm">
          <div>
            Removed {{ done.repositories.worktreesRemoved.length }} worktree(s) and deleted
            {{ done.repositories.branchesDeleted.length }} branch(es).
          </div>
          <div v-for="item in done.repositories.teamBranchReset" :key="`reset-${item.repo}-${item.name}`">
            {{ item.name }} ({{ item.repo }}): {{ item.reason }}
          </div>
          <template
            v-if="
              done.repositories.worktreesKept.length > 0 ||
              done.repositories.branchesKept.length > 0 ||
              done.repositories.teamBranchKept.length > 0
            "
          >
            <div class="q-mt-xs">Kept, because removing them would lose work:</div>
            <ul class="q-my-xs">
              <li
                v-for="item in [
                  ...done.repositories.worktreesKept,
                  ...done.repositories.branchesKept,
                  ...done.repositories.teamBranchKept,
                ]"
                :key="`kept-${item.repo}-${item.name}`"
              >
                {{ item.name }} ({{ item.repo }}): {{ item.reason }}
              </li>
            </ul>
          </template>
        </div>

        <!-- Named, not swallowed. The reset happened either way, so this is a WARNING rather than an
             error: the usual cause is a file still held by a child that has not finished exiting. -->
        <q-banner v-if="done.failures.length > 0" dense class="bg-warning text-black">
          Some of it could not be cleared: {{ done.failures.join('; ') }}
        </q-banner>
      </q-card-section>

      <template v-else>
        <q-card-section class="q-pb-none">
          <div class="text-subtitle2 q-mb-xs">Members to reset</div>

          <div v-if="members.length === 0" class="os-body os-text-muted">
            This team has no members.
          </div>

          <div class="column items-start">
            <q-checkbox
              v-for="member in members"
              :key="member.id"
              v-model="choices.members[member.id]"
              :label="member.name"
              :disable="busy"
              dense
            />
          </div>
        </q-card-section>

        <q-card-section class="q-pb-none">
          <div class="text-subtitle2 q-mb-xs">For each member…</div>

          <div class="column items-start">
            <!-- ONE control over three server flags - the floor, the messages and the run
                 transcripts on disk. They stay separate on the wire because they genuinely are
                 separable, but to a person there is only one thing here: what this member
                 remembers. Three checkboxes made one decision look like three, and let you reach a
                 state that means nothing - delete the rows without moving the floor. -->
            <q-checkbox
              v-model="choices.deleteMemory"
              label="Delete memory"
              color="negative"
              :disable="busy"
              dense
            />

            <q-checkbox
              v-model="choices.clearWorkspaces"
              label="Clear working folders"
              :disable="busy"
              dense
            />
          </div>
        </q-card-section>

        <q-card-section>
          <div class="text-subtitle2 q-mb-xs">Team-wide</div>

          <div class="column items-start">
            <q-checkbox
              v-model="choices.clearSharedDocuments"
              label="Clear shared docs"
              :disable="busy"
              dense
            />

            <q-checkbox
              v-model="choices.resetRepositories"
              label="Reset repositories"
              color="negative"
              :disable="busy"
              dense
            />
          </div>

          <!-- WHAT IS LOST, READ BEFORE THE PERSON CONFIRMS. One line on what it does, then the
               list: the ticked members' trees and branches, and the team branch only when every
               member is ticked - the server's rule, shown rather than decided here. -->
          <div v-if="choices.resetRepositories" class="reset-repositories q-mt-xs q-ml-lg">
            <div class="os-body os-text-muted">
              Removes the ticked members' worktrees and branches, and with every member ticked
              resets the team branch to the default branch. Nothing is forced: a worktree or branch
              holding work that is on no remote is kept and named.
            </div>

            <div v-if="previewFailed" class="text-negative q-mt-xs">{{ previewFailed }}</div>
            <div v-else-if="!losses" class="os-text-muted q-mt-xs">Reading what would be removed…</div>
            <template v-else>
              <div class="text-body2 q-mt-xs">What is lost:</div>
              <ul class="reset-repositories-losses q-my-xs">
                <li v-for="tree in losses.worktrees" :key="`wt-${tree.repo}-${tree.name}`">
                  {{ labelFor(tree.member ?? '') }}'s worktree {{ tree.name }} ({{ tree.repo }})
                </li>
                <li v-for="branch in losses.branches" :key="`br-${branch.repo}-${branch.name}`">
                  {{ labelFor(branch.member ?? '') }}'s branch {{ branch.name }} ({{ branch.repo }})
                </li>
                <li v-if="losses.teamBranch">The team branch {{ losses.teamBranch }}</li>
                <li
                  v-if="losses.worktrees.length === 0 && losses.branches.length === 0 && !losses.teamBranch"
                >
                  Nothing: the ticked members have no worktrees or branches.
                </li>
              </ul>
              <q-banner v-if="losses.refusedFor.length > 0" dense class="bg-warning text-black">
                The default branch of {{ losses.refusedFor.join(', ') }} is not known, so resetting
                the team branch will be refused and nothing will change. Fetch the repository, or
                set it in Team settings.
              </q-banner>
            </template>
          </div>

          <!-- NO "RESET CONCIERGE" BOX, AND THERE MUST NOT BE ONE. A Concierge is keyed on the
               PERSON - one per signed-in user, serving every team they reach - so no team-scoped
               operation can address a session and this route ends none. The box stood here
               permanently `:disable="true"` beside a warning line that could never render, which
               is the worst version of the trade this dialog already made about the session count:
               a control that names a capability the platform does not have is read as one that
               works and is merely switched off today.

               The other half is the reset RESULT, which for the same reason carries no session
               count - see the comment above `done.cleared`. -->
        </q-card-section>

        <q-card-section v-if="failed" class="q-pt-none">
          <q-banner dense class="bg-negative text-white">{{ failed }}</q-banner>
        </q-card-section>
      </template>

      <q-card-actions align="right">
        <q-btn v-if="done" flat label="Close" :disable="busy" @click="finish" />

        <template v-else>
          <q-btn v-close-popup flat label="Cancel" :disable="busy" />
          <q-btn
            unelevated
            color="primary"
            label="Reset"
            :loading="busy"
            :disable="!ready"
            @click="reset"
          />
        </template>
      </q-card-actions>
    </q-card>
  </q-dialog>
</template>

<style scoped>
</style>
