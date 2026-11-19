<script setup lang="ts">
import { computed, watchEffect } from 'vue';
import type { CommitMatch } from '../../../shared/api';
import { commitParts, commitTeams } from '../commitView';
import { cachedMatches, chooseTitle, commitTitle, pickMatches, requestResolve, resolvedVersion } from '../commitResolve';

// B074: plain text (not markdown) that may name commits: the card trail, activity lines, a workflow's ending. The same
// treatment as rendered markdown (markdown.ts + commitResolve.ts): a commit-shaped ID is text until git.commitsResolve
// confirms it (asked in the same one-per-screen batch), then a [data-commit] link that App.vue's click handler opens,
// with the commit's title (and "not pushed") on hover; an ID naming several commits opens the chooser. team: the place's
// team, first in the order the commit is looked for (then the team on screen, then every team).
const props = defineProps<{ text: string | null | undefined; team?: string | null }>();

const parts = computed(() => commitParts(props.text ?? ''));
const teams = computed(() => {
  const t = commitTeams.value;
  return [props.team, t.current, ...t.all].filter((x): x is string => !!x);
});

watchEffect(() => {
  const ids = parts.value.filter((p) => p.sha).map((p) => p.sha!);
  if (ids.length) requestResolve(ids);
});

interface Shown { text: string; link?: { id: string; matches: CommitMatch[] } }
const shown = computed<Shown[]>(() => {
  void resolvedVersion.value;
  return parts.value.map((p) => {
    if (!p.sha) return { text: p.text };
    const matches = pickMatches(cachedMatches(p.sha) ?? [], teams.value);
    return matches.length ? { text: p.text, link: { id: p.sha, matches } } : { text: p.text };
  });
});
</script>

<template>
  <span class="commit-text" :data-commit-team="team || undefined"><template v-for="(p, i) in shown" :key="i"><span v-if="p.link && p.link.matches.length === 1"
    class="commit-id commit-link" role="button" tabindex="0" data-test="commit-link" data-commit-state="link"
    :data-commit="p.link.matches[0].fullId" :data-commit-in="p.link.matches[0].team" :data-commit-repo="p.link.matches[0].repo || undefined"
    :title="commitTitle(p.link.matches[0])">{{ p.text }}</span><span v-else-if="p.link" class="commit-id commit-link" role="button" tabindex="0"
    data-test="commit-link" data-commit-state="choose" :data-commit="p.link.id" data-commit-choose
    :title="chooseTitle(p.link.id, p.link.matches)">{{ p.text }}</span><template v-else>{{ p.text }}</template></template></span>
</template>
