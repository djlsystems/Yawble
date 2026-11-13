<script setup lang="ts">
import mark from '../assets/yawble-mark.png';
import { computed } from 'vue';
import type { ConciergeToolUse } from '../../../shared/api';
import { copyCodeFrom, renderMarkdown } from '../markdown';
import Icon from './Icon.vue';

// One message of a Concierge chat: the person's on the right, the Concierge's as rendered markdown with the tools it
// used (what it looked at and did) and, for an answer that did not finish, why.
const props = defineProps<{
  role: 'user' | 'assistant' | 'update';
  text: string;
  tools?: ConciergeToolUse[];
  state?: string | null;
  error?: string | null;
  cli?: string | null;
  live?: boolean;
  kind?: string | null;
  at?: string;
}>();

const html = computed(() => renderMarkdown(props.text));
const toolLabel: Record<string, string> = {
  team_list: 'Looked at the teams', team_current: 'Checked the team in focus', team_status: 'Checked a team',
  team_activity: 'Read a team’s activity', tell: 'Told a Manager', answer: 'Answered a decision', team_create: 'Created a team',
  notes_read: 'Read its notes', notes_write: 'Updated its notes',
};
const label = (name: string) => toolLabel[name] ?? name;
const kinds: Record<string, { icon: string; label: string }> = {
  done: { icon: 'check', label: 'Finished' }, decision: { icon: 'question', label: 'Needs you' },
  failed: { icon: 'alert', label: 'Failed' }, blocked: { icon: 'alert', label: 'Blocked' }, expired: { icon: 'eye', label: 'Stopped watching' },
};
const kind = computed(() => kinds[props.kind ?? ''] ?? { icon: 'eye', label: 'Update' });
const time = computed(() => {
  const d = props.at ? new Date(props.at) : null;
  return d && !Number.isNaN(d.getTime()) ? d.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }) : '';
});
</script>

<template>
  <div class="msg" :class="role" data-test="chat-message" :data-role="role">
    <div v-if="role === 'user'" class="bubble">{{ text }}</div>
    <div v-else-if="role === 'update'" class="ucard" :class="kind ? `k-${props.kind}` : ''" data-test="chat-update" :data-kind="props.kind">
      <span class="u-icon"><Icon :name="kind.icon" :size="14" /></span>
      <div class="u-body">
        <div class="u-head">{{ kind.label }}<span class="muted"> · update<template v-if="time"> · {{ time }}</template></span></div>
        <!-- eslint-disable-next-line vue/no-v-html : markdown-it with html disabled (markdown.ts) -->
        <div class="md u-text" @click="copyCodeFrom" v-html="html" />
      </div>
    </div>
    <template v-else>
      <div class="who"><img class="avatar" :src="mark" alt="" />Concierge<span v-if="cli" class="muted small"> · {{ cli }}</span></div>
      <div v-if="tools?.length" class="tools">
        <span v-for="(t, i) in tools" :key="i" class="tool" :title="t.detail ?? ''"><Icon name="tool" :size="12" />{{ label(t.name) }}<template v-if="t.detail"> <span class="muted">{{ t.detail.length > 48 ? t.detail.slice(0, 48) + '…' : t.detail }}</span></template></span>
      </div>
      <!-- eslint-disable-next-line vue/no-v-html : markdown-it with html disabled (markdown.ts) -->
      <div v-if="text" class="md" data-test="chat-answer" @click="copyCodeFrom" v-html="html" />
      <div v-else-if="live" class="thinking" data-test="chat-thinking"><span /><span /><span /></div>
      <p v-if="state === 'failed'" class="failed" data-test="chat-failed"><Icon name="alert" :size="14" />The answer did not finish: {{ error ?? 'unknown error' }}</p>
      <p v-else-if="state === 'stopped'" class="muted small">Stopped.</p>
    </template>
  </div>
</template>

<style scoped>
.msg { margin: 0 0 22px; }
.msg.user { display: flex; justify-content: flex-end; }
.bubble { background: var(--bubble); padding: 10px 16px; border-radius: 18px; max-width: 78%; white-space: pre-wrap; line-height: 1.5; font-size: 15px; }
.who { display: flex; align-items: center; gap: 6px; font-weight: 600; font-size: 13px; margin-bottom: 6px; }
.avatar { width: 22px; height: 22px; object-fit: contain; }
.tools { display: flex; flex-wrap: wrap; gap: 6px; margin: 0 0 8px; }
.tool { display: inline-flex; align-items: center; gap: 4px; font-size: 12px; background: var(--chip); color: var(--ink-soft); border-radius: 12px; padding: 2px 9px; }
.md { font-size: 15px; line-height: 1.6; overflow-wrap: anywhere; }
.md :deep(p) { margin: 0 0 10px; }
.md :deep(ul), .md :deep(ol) { margin: 0 0 10px; padding-left: 22px; }
.md :deep(li) { margin: 2px 0; }
.md :deep(code) { background: var(--chip); padding: 1px 5px; border-radius: 4px; font-size: 13px; }
.md :deep(pre) { background: #1f2430; color: #e7e9ee; padding: 12px 14px; line-height: 1.45; border-radius: 10px; overflow: auto; margin: 0; }
.md :deep(.codeblock) { background: #1f2430; border-radius: 10px; margin: 0 0 10px; overflow: hidden; }
.md :deep(.codeblock pre) { border-radius: 0; padding-top: 4px; }
.md :deep(.codebar) { display: flex; align-items: center; justify-content: space-between; padding: 4px 6px 0 14px; color: #9aa3b5; font-size: 12px; }
.md :deep(.copy) { display: inline-flex; align-items: center; gap: 5px; background: transparent; border: 0; color: #c3c9d6; font-size: 12px; padding: 4px 8px; border-radius: 6px; cursor: pointer; }
.md :deep(.copy:hover) { background: #323949; color: #fff; }
.md :deep(.copy .i-done), .md :deep(.copy.copied .i-copy) { display: none; }
.md :deep(.copy.copied .i-done) { display: inline-flex; color: #7ad19b; }
.md :deep(pre code) { background: transparent; color: inherit; padding: 0; }
.md :deep(h1), .md :deep(h2), .md :deep(h3) { font-size: 16px; margin: 14px 0 6px; }
.md :deep(table) { border-collapse: collapse; margin: 0 0 10px; }
.md :deep(th), .md :deep(td) { border: 1px solid var(--line); padding: 4px 8px; text-align: left; }
.md :deep(a) { color: var(--accent); }
.ucard { display: flex; gap: 10px; align-items: flex-start; border: 1px solid var(--line); border-left: 3px solid var(--muted); background: var(--surface);
  border-radius: 10px; padding: 9px 12px; }
.ucard.k-done { border-left-color: var(--ok); }
.ucard.k-decision { border-left-color: var(--brand); }
.ucard.k-failed, .ucard.k-blocked { border-left-color: var(--bad); }
.u-icon { margin-top: 2px; color: var(--ink-soft); }
.k-done .u-icon { color: var(--ok); }
.k-decision .u-icon { color: var(--brand); }
.k-failed .u-icon, .k-blocked .u-icon { color: var(--bad); }
.u-body { min-width: 0; flex: 1; }
.u-head { font-size: 12px; font-weight: 600; margin-bottom: 2px; }
.u-text { font-size: 14px; }
.u-text :deep(p:last-child) { margin-bottom: 0; }
.failed { display: flex; align-items: center; gap: 6px; color: var(--bad); font-size: 13px; margin: 4px 0 0; }
.small { font-size: 12px; }
.thinking { display: flex; gap: 5px; padding: 8px 0; }
.thinking span { width: 7px; height: 7px; border-radius: 50%; background: #aab1bf; animation: blink 1.2s infinite ease-in-out; }
.thinking span:nth-child(2) { animation-delay: .15s; }
.thinking span:nth-child(3) { animation-delay: .3s; }
@keyframes blink { 0%, 80%, 100% { opacity: .25; } 40% { opacity: 1; } }
</style>
