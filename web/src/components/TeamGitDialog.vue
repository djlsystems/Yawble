<script setup lang="ts">
/**
 * GIT, FOR THE ACTIVE TEAM. The team's repositories, in a dialog off the Active Team ribbon tab.
 *
 * WHY IT IS NOT ON THE STRIP. The strip answers "how is this team doing right now" at a glance, in
 * figures — elapsed and spend. A repository is neither a figure nor a glance: one entry renders a
 * branch state, an origin freshness stamp, a worktree list and three actions, and a team may have
 * several. It would set the height of a row of tiles whose whole point is to stay one row, and
 * force the figures beside it into a third of the width each.
 *
 * It also sits beside Members, Documents, Kanban, Settings, Schedules and Reset — every other
 * team-scoped thing a person opens — rather than in the middle of the numbers.
 *
 * THE PANEL'S MEANING IS `repoPanel` IN `lib/repoStatus.ts` AND MUST STAY THERE. Logic in a
 * component is logic a test reaches only through the whole panel; a pure function can be pinned
 * directly against what the server actually sends.
 */
import { computed } from 'vue';
import type { TeamRepoStatus } from '../api/types';
import { asTeamId } from '../api/types';
import { cloneMainFinding, repoPanel } from '../lib/repoStatus';
import RepoCard from './RepoCard.vue';
import { useConsoleStore } from '../stores/console';

const props = defineProps<{
  /** The repo status for this team, or null until its fetch lands. */
  repoStatus: TeamRepoStatus | null;

  /** The team these repositories belong to. */
  teamId: string;

  /** Whether any member is Running, which the actions refuse to run against. */
  anyMemberRunning: boolean;
}>();

const open = defineModel<boolean>({ required: true });

const board = useConsoleStore();

const teamIdTyped = computed(() => asTeamId(props.teamId));
const panel = computed(() => repoPanel(props.repoStatus));
const findings = computed(() =>
  panel.value.repos
    .map((repo) => cloneMainFinding(repo))
    .filter((sentence): sentence is string => sentence !== null),
);

async function handleActionFailed() {
  // After a failed merge-to-main or bring-current, re-read the repo status
  // because the server already ran git fetch even though the action failed
  await board.pullRepoStatus();
}

async function handleActionSucceeded() {
  // After a successful merge-to-main or bring-current, re-read the repo status
  // because the server ran git fetch and the repo state has changed
  await board.pullRepoStatus();
}
</script>

<template>
  <q-dialog v-model="open">
    <q-card class="git-dialog-card os-dialog-lg">
      <q-card-section class="git-dialog-header">
        <div class="os-dialog-title">Git</div>
        <q-space />
        <q-btn v-close-popup flat round dense icon="close" aria-label="Close" />
      </q-card-section>

      <q-separator />

      <q-card-section class="git-dialog-body">
        <!-- NOT ANSWERED YET, WHICH IS NOT THE SAME AS ANSWERED "NO". An absent payload must not
             take the "not found" path, or a board that has merely not fetched accuses a working
             machine of having no git. -->
        <div v-if="panel.gitResolves === null" class="git-empty row items-center no-wrap q-gutter-sm">
          <q-spinner size="18px" color="primary" />
          <span>Checking git…</span>
        </div>

        <!-- git missing. The words are the SERVER'S, never a sentence composed here. -->
        <div v-else-if="!panel.gitResolves" class="repo-prerequisite-card">
          <div class="repo-prereq-header">
            <q-icon name="error" color="warning" />
            <span class="repo-prereq-text">Not found on this machine</span>
          </div>
          <div class="repo-prereq-message">{{ panel.gitMessage }}</div>
          <div class="repo-prereq-actions-disabled">
            All repo actions disabled — {{ panel.gitMessage }}
          </div>
        </div>

        <template v-else-if="panel.repos.length > 0">
          <div v-if="findings.length" class="git-findings">
            <p v-for="(finding, index) in findings" :key="index" class="git-finding">{{ finding }}</p>
          </div>
          <RepoCard
            v-for="repo in panel.repos"
            :key="repo.name"
            :team="teamIdTyped"
            :repo="repo"
            :git-resolves="panel.actionsEnabled"
            :any-member-running="anyMemberRunning"
            @updated="(updated) => {
              if (!repoStatus) return;
              const idx = repoStatus.repos.findIndex(r => r.name === updated.name);
              if (idx >= 0) repoStatus.repos[idx] = updated;
            }"
            @action-failed="handleActionFailed"
            @action-succeeded="handleActionSucceeded"
          />
        </template>

        <div v-else class="git-empty">No repositories configured</div>

        <!-- SILENT WHEN `gh` IS THERE, which is the rule this dialog's own dirty flag already
             follows: a mark that is usually absent gets read when it appears, and one that says
             "fine" every time gets read by nobody.

             IT SPEAKS WHEN gh IS MISSING, because that is the case the sentence is for, and it is
             the only mention of gh anywhere.
             Every operation the PLATFORM runs is plain git - worktree, fetch, ls-remote,
             merge --ff-only, push, rev-parse - so a missing gh breaks nothing here; it breaks what
             AGENTS do with it, which is a different problem and worth naming as one. -->
        <div v-if="panel.gh && !panel.gh.resolves" class="repo-gh-caption">
          <span class="repo-gh-label">gh</span>
          <span class="repo-gh-note">
            {{ panel.gh.message }} Agents use it; the Host does not, so nothing on this screen is
            blocked by it.
          </span>
        </div>
      </q-card-section>
    </q-card>
  </q-dialog>
</template>

<style scoped>
.git-dialog-header {
  display: flex;
  align-items: center;
}

.git-dialog-body {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.git-empty {
  padding: 12px;
  text-align: center;
  color: var(--os-ink-muted);
  font-size: 12px;
}

.git-findings {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.git-finding {
  margin: 0;
  padding: 8px 10px;
  border: 1px solid var(--q-warning);
  border-radius: 4px;
  background: var(--os-tint-warn);
  color: var(--os-ink);
  font-size: 0.85rem;
}

.repo-prerequisite-card {
  padding: 12px;
  background: var(--os-tint-warn);
  border: 1px solid var(--q-warning);
  border-radius: 4px;
}

.repo-prereq-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-weight: 500;
  color: var(--q-negative);
  margin-bottom: 8px;
}

.repo-prereq-text {
  font-size: 14px;
}

.repo-prereq-message {
  font-size: 12px;
  color: var(--os-ink);
  margin-bottom: 8px;
}

.repo-prereq-actions-disabled {
  font-size: 11px;
  color: var(--q-warning);
  font-style: italic;
}

.repo-gh-caption {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 0 4px;
  font-size: 12px;
  color: var(--os-ink-muted);
}

.repo-gh-label {
  font-weight: 500;
  font-family: monospace;
}

.repo-gh-note {
  font-size: 11px;
  color: var(--os-ink-faint);
}
</style>
