<script setup lang="ts">
import { computed, ref, shallowRef } from 'vue';
import { DiffView } from '@yawble/code-browser';
import { GIT_MERGE_DIFF, type CommitDiff, type CommitFile, type MergePreview, type YawbleApi } from '../../../shared/api';
import { mergePreviewView } from '../gitRange';
import { fileStatusWord } from '../commitView';
import CommitList from './CommitList.vue';
import LoadingNote from './LoadingNote.vue';
import LocalMark from './LocalMark.vue';
import { branchLocalOnly, branchLocalTitle } from '../localMark';

// Merge to <base>… shows this before its Merge button: the core's outcome sentence, the commits that land (each
// opens the Commit view), and the files changed all together with their combined diff (git.mergeDiff, in the code
// browser's DiffView, as the Commit view shows a commit's).
const props = defineProps<{ api: YawbleApi; team: string; params: Record<string, unknown>; preview: MergePreview }>();
const view = computed(() => mergePreviewView(props.preview));

const openPath = ref<string | null>(null);
const diff = shallowRef<CommitDiff | null>(null);
const diffError = ref<string | null>(null);
const diffLoading = ref(false);
async function openFile(f: CommitFile) {
  if (openPath.value === f.path) { openPath.value = null; diff.value = null; return; }
  openPath.value = f.path;
  diff.value = null;
  diffError.value = null;
  diffLoading.value = true;
  const p = props.preview;
  try {
    if (!p.diffFrom || !p.diffTo) throw new Error('the preview gave no commits to compare');
    const d = await props.api.call<CommitDiff>(GIT_MERGE_DIFF, { ...props.params, from: p.diffFrom, to: p.diffTo, path: f.path });
    if (openPath.value === f.path) diff.value = d;
  } catch (e) {
    if (openPath.value === f.path) diffError.value = (e as Error)?.message ?? String(e);
  } finally {
    if (openPath.value === f.path) diffLoading.value = false;
  }
}
</script>

<template>
  <section class="preview" :class="`tone-${view.tone}`" data-test="git-merge-preview" :data-outcome="preview.outcome">
    <p class="sentence" data-test="git-merge-outcome">{{ view.sentence }}</p>
    <p v-if="preview.teamPush && branchLocalOnly(preview.teamPush, preview.hasOrigin)" class="small" data-test="git-merge-team-push">
      <code>{{ preview.teamBranch }}</code><LocalMark :title="branchLocalTitle(preview.teamPush)" /></p>
    <template v-if="view.commitsHeading">
      <div class="small"><strong data-test="git-merge-commits-heading">{{ view.commitsHeading }}</strong></div>
      <CommitList :team="team" :commits="preview.commits" :total="preview.count" :has-origin="preview.hasOrigin" />
    </template>
    <div v-if="preview.conflictFiles.length" class="small" data-test="git-merge-conflicts">Conflicting files:
      <code v-for="(f, i) in preview.conflictFiles" :key="f">{{ i ? ', ' : '' }}{{ f }}</code></div>
    <template v-if="view.filesHeading">
      <div class="small"><strong data-test="git-merge-files-heading">{{ view.filesHeading }}</strong></div>
      <ul class="files">
        <li v-for="f in preview.files" :key="f.path" data-test="git-merge-file" :data-path="f.path">
          <button type="button" class="file" :class="{ open: openPath === f.path }" :aria-expanded="openPath === f.path" data-test="git-merge-file-open" @click="openFile(f)">
            <span class="path"><template v-if="f.oldPath && f.oldPath !== f.path">{{ f.oldPath }} → </template>{{ f.path }}</span>
            <span class="chip">{{ fileStatusWord(f.status) }}</span>
            <span v-if="f.binary" class="chip">binary</span>
            <span v-else class="counts"><span class="add">+{{ f.added }}</span> <span class="del">−{{ f.deleted }}</span></span>
          </button>
          <div v-if="openPath === f.path" class="diff">
            <LoadingNote v-if="diffLoading" text="Reading the diff…" />
            <p v-else-if="diffError" class="error" data-test="git-merge-diff-error">The diff could not be read: {{ diffError }}</p>
            <DiffView v-else-if="diff" :diff="diff" />
          </div>
        </li>
      </ul>
    </template>
  </section>
</template>

<style scoped>
.preview { display: flex; flex-direction: column; gap: 4px; padding: 8px 10px; border-radius: 6px; background: var(--chip); margin: 8px 0; min-width: 0; }
.preview.tone-ok { border-left: 3px solid var(--ok-rule); }
.preview.tone-bad { border-left: 3px solid var(--state-failed); }
.sentence { margin: 0; font-weight: 600; overflow-wrap: anywhere; }
.files { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 4px; }
.file { all: unset; box-sizing: border-box; cursor: pointer; display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; width: 100%; padding: 4px 8px; border: 1px solid var(--line); border-radius: 6px; font-size: 12.5px; }
.file:hover, .file.open { border-color: var(--accent); }
.file:focus-visible { outline: 2px solid var(--accent); }
.path { flex: 1 1 220px; min-width: 0; overflow-wrap: anywhere; font-family: ui-monospace, 'Cascadia Code', Consolas, monospace; }
.chip { font-size: 11px; padding: 1px 6px; border-radius: 9px; background: var(--bg); }
.add { color: var(--ok); }
.del { color: var(--bad); }
.diff { margin-top: 6px; min-width: 0; }
.error { color: var(--bad); }
.small { font-size: 12px; }
</style>
