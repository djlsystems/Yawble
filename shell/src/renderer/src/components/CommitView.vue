<script setup lang="ts">
import { computed, onMounted, ref, shallowRef } from 'vue';
import { DiffView } from '@yawble/code-browser';
import type { CommitDiff, CommitFile, CommitFound, CommitShow, YawbleApi } from '../../../shared/api';
import { resolveApi } from '../api';
import { copyText } from '../markdown';
import { differentCommitter, fileStatusWord, gitRequest, itsBranch, mergeLine, spaced, whereLines } from '../commitView';
import LoadingNote from './LoadingNote.vue';
import LocalMark from './LocalMark.vue';

// One commit, explained (git.commitShow): what it is, where it is, the files it changed and their diffs
// (git.commitDiff, shown by the code browser's read-only DiffView), and three read-only actions. teams: where to look,
// in order (the first whose repository has it). A commit git no longer has says so and why. A cloud viewer with
// members' code hidden (codeShown=false) gets the commit and its file list only: no diff opens.
// repo: the repository a confirmed link named, in teams[0]; the other teams are asked about their main repository.
const props = defineProps<{ api?: YawbleApi; teams: string[]; sha: string; repo?: string | null }>();
const repoOf = (t: string) => (props.repo && t === props.teams[0] ? { repo: props.repo } : {});
const emit = defineEmits<{ done: [] }>();
const api = resolveApi(props.api);

const loading = ref(true);
const error = ref<string | null>(null);
const commit = shallowRef<CommitShow | null>(null);
const team = ref<string>(props.teams[0] ?? '');

onMounted(async () => {
  let last: CommitShow | null = null;
  let lastError: string | null = null;
  for (const t of props.teams) {
    try {
      const c = await api.call<CommitShow>('git.commitShow', { team: t, sha: props.sha, ...repoOf(t) });
      last = c; team.value = t; lastError = null;
      if (c?.found) break;
    } catch (e) {
      lastError = (e as Error)?.message ?? String(e);
    }
  }
  commit.value = last;
  error.value = last ? null : lastError ?? 'No team to look in.';
  loading.value = false;
});

const c = computed(() => commit.value);
/** The commit when git has it (a gone commit's answer is only found, goneReason and sha). */
const known = computed<CommitFound | null>(() => (commit.value?.found ? commit.value : null));
const merged = computed(() => (known.value ? mergeLine(known.value) : null));
const where = computed(() => (known.value ? whereLines(known.value) : []));
const when = (iso: string) => { const d = new Date(iso); return Number.isNaN(d.getTime()) ? iso : d.toLocaleString(); };
const totals = computed(() => (known.value?.files ?? []).reduce((a, f) => ({ added: a.added + (f.added || 0), deleted: a.deleted + (f.deleted || 0) }), { added: 0, deleted: 0 }));

// ---- Copy ID.
const copied = ref<boolean | null>(null);
async function copyId() {
  copied.value = await copyText(c.value?.sha || props.sha);
  setTimeout(() => { copied.value = null; }, 1600);
}

// ---- The file's diff.
const openPath = ref<string | null>(null);
const diff = shallowRef<CommitDiff | null>(null);
const diffLoading = ref(false);
const diffError = ref<string | null>(null);
async function openFile(file: CommitFile) {
  const shown = known.value;
  if (!shown?.codeShown) return;
  if (openPath.value === file.path) { openPath.value = null; diff.value = null; return; }
  openPath.value = file.path;
  diff.value = null;
  diffError.value = null;
  diffLoading.value = true;
  try {
    // Errors (gone, no_such_file, bad_params, git_failed, remote_refused) carry the core's own plain message.
    const d = await api.call<CommitDiff>('git.commitDiff', { team: team.value, sha: shown.sha, path: file.path, ...repoOf(team.value) });
    if (openPath.value === file.path) diff.value = d;
  } catch (e) {
    if (openPath.value === file.path) diffError.value = (e as Error)?.message ?? String(e);
  } finally {
    if (openPath.value === file.path) diffLoading.value = false;
  }
}

// ---- Compare with main / Show its branch: the team's Git dialog (App.vue opens the team, TeamView the dialog).
const branch = computed(() => (known.value ? itsBranch(known.value) : null));
function askGit(focus: 'compare' | 'branch') {
  if (!c.value) return;
  gitRequest.value = { team: team.value, focus, branch: focus === 'branch' ? branch.value : null, sha: c.value.sha };
  emit('done');
}
</script>

<template>
  <div class="commit" data-test="commit-view" :data-team="team" :data-commit-team="team">
    <LoadingNote v-if="loading" text="Reading the commit…" />
    <p v-else-if="error || !c" class="error" data-test="commit-error">The commit could not be read: {{ error }}</p>

    <section v-else-if="!c.found" class="gone" data-test="commit-gone">
      <h3>This commit is gone</h3>
      <p><code class="sha">{{ c.sha || sha }}</code></p>
      <p data-test="commit-gone-reason">{{ c.goneReason || 'Git no longer has it in the team’s repository.' }}</p>
      <div class="actions"><button type="button" data-test="commit-copy" @click="copyId">{{ copied === true ? 'Copied' : copied === false ? 'Copy failed' : 'Copy ID' }}</button></div>
    </section>

    <template v-else>
      <section class="what" data-test="commit-what">
        <h3 class="title" data-test="commit-title">{{ c.title || '(no message)' }}</h3>
        <pre v-if="c.body" class="body" data-test="commit-body">{{ c.body }}</pre>
        <p v-if="merged" class="merge" data-test="commit-merge">{{ merged }}</p>
        <dl>
          <dt>Author</dt><dd data-test="commit-author">{{ c.author.name }} <span class="muted">&lt;{{ c.author.email }}&gt;</span> · {{ when(c.author.date) }}</dd>
          <template v-if="differentCommitter(c)">
            <dt>Committer</dt><dd data-test="commit-committer">{{ c.committer!.name }} <span class="muted">&lt;{{ c.committer!.email }}&gt;</span> · {{ when(c.committer!.date) }}</dd>
          </template>
          <template v-if="c.made">
            <dt>Made by</dt>
            <dd data-test="commit-made">{{ spaced(c.made.member) }}<template v-if="c.made.card"> · card {{ c.made.card }}</template><template v-if="c.made.workflow"> · workflow #{{ c.made.workflow }}</template></dd>
          </template>
          <dt>ID</dt><dd><code class="sha" data-test="commit-sha">{{ c.sha }}</code></dd>
        </dl>
      </section>

      <section class="where" data-test="commit-where">
        <h4>Where it is</h4>
        <ul><li v-for="w in where" :key="w.key" :class="w.tone" :data-test="`commit-where-${w.key}`"><LocalMark v-if="w.key === 'unpushed'" long /><template v-else>{{ w.text }}</template></li></ul>
        <p v-if="c.where.branches.length" class="small muted" data-test="commit-branches">Branches: {{ c.where.branches.join(', ') }}</p>
      </section>

      <div class="actions" data-test="commit-actions">
        <button type="button" data-test="commit-copy" @click="copyId">{{ copied === true ? 'Copied' : copied === false ? 'Copy failed' : 'Copy ID' }}</button>
        <button type="button" data-test="commit-compare" title="The team's Git dialog: its branches against main" @click="askGit('compare')">Compare with main</button>
        <button type="button" data-test="commit-branch" :disabled="!branch" :title="branch ? `The team's Git dialog, at ${branch}` : 'No branch holds this commit'" @click="askGit('branch')">Show its branch</button>
      </div>

      <section class="files" data-test="commit-files">
        <h4>{{ c.files.length }} file{{ c.files.length === 1 ? '' : 's' }} changed <span class="add">+{{ totals.added }}</span> <span class="del">−{{ totals.deleted }}</span>
          <span v-if="c.isMerge" class="small muted"> (against its first parent: what this merge brought in)</span></h4>
        <p v-if="!c.codeShown" class="small muted" data-test="commit-code-hidden">Members' code is not shown to cloud viewers, so the diffs are not opened here.</p>
        <ul>
          <li v-for="f in c.files" :key="f.path" data-test="commit-file" :data-path="f.path" :data-status="f.status">
            <component :is="c.codeShown ? 'button' : 'div'" :type="c.codeShown ? 'button' : undefined" class="file" :class="{ open: openPath === f.path }"
              :aria-expanded="c.codeShown ? openPath === f.path : undefined" data-test="commit-file-open" @click="openFile(f)">
              <span class="path"><template v-if="f.oldPath && f.oldPath !== f.path">{{ f.oldPath }} → </template>{{ f.path }}</span>
              <span class="chip" :class="f.status" data-test="commit-file-status">{{ fileStatusWord(f.status) }}</span>
              <span v-if="f.binary" class="chip" data-test="commit-file-binary">binary</span>
              <span v-else class="counts"><span class="add">+{{ f.added }}</span> <span class="del">−{{ f.deleted }}</span></span>
            </component>
            <div v-if="openPath === f.path" class="diff">
              <LoadingNote v-if="diffLoading" text="Reading the diff…" />
              <p v-else-if="diffError" class="error" data-test="commit-diff-error">The diff could not be read: {{ diffError }}</p>
              <DiffView v-else-if="diff" :diff="diff" />
            </div>
          </li>
        </ul>
      </section>
    </template>
  </div>
</template>

<style scoped>
.commit { display: flex; flex-direction: column; gap: 12px; min-width: 0; }
h3 { margin: 0; font-size: 16px; overflow-wrap: anywhere; }
h4 { margin: 0 0 6px; font-size: 13px; }
.body { margin: 6px 0 0; white-space: pre-wrap; overflow-wrap: anywhere; font: inherit; color: var(--ink); }
.merge { margin: 6px 0 0; font-weight: 600; }
dl { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 4px 12px; margin: 8px 0 0; font-size: 13px; }
dt { color: var(--muted); }
dd { margin: 0; overflow-wrap: anywhere; min-width: 0; }
.sha { overflow-wrap: anywhere; word-break: break-all; }
.where ul, .files ul { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
.where li { font-size: 13px; padding-left: 10px; border-left: 3px solid var(--line); }
.where li.ok { border-left-color: var(--ok); }
.where li.warn { border-left-color: var(--warn-rule); }
.actions { display: flex; flex-wrap: wrap; gap: 8px; }
.file { all: unset; box-sizing: border-box; display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; width: 100%; padding: 5px 8px; border: 1px solid var(--line); border-radius: 6px; font-size: 12.5px; }
button.file { cursor: pointer; }
button.file:hover, button.file.open { border-color: var(--accent); }
button.file:focus-visible { outline: 2px solid var(--accent); }
.path { flex: 1 1 220px; min-width: 0; overflow-wrap: anywhere; font-family: ui-monospace, 'Cascadia Code', Consolas, monospace; }
.chip { font-size: 11px; padding: 1px 6px; border-radius: 9px; background: color-mix(in srgb, var(--line) 50%, var(--surface)); }
.chip.added { background: color-mix(in srgb, var(--ok) 18%, var(--surface)); }
.chip.deleted { background: color-mix(in srgb, var(--bad) 16%, var(--surface)); }
.chip.renamed { background: color-mix(in srgb, var(--accent) 16%, var(--surface)); }
.add { color: var(--ok); }
.del { color: var(--bad); }
.diff { margin-top: 6px; min-width: 0; }
.error { color: var(--bad); }
.small { font-size: 12px; }
.muted { color: var(--muted); }
@media (max-width: 759.98px) {
  .actions button, .file { min-height: 44px; }
  dl { grid-template-columns: minmax(0, 1fr); }
  dt { margin-top: 4px; }
}
</style>
