<script setup lang="ts">
import { computed, ref } from 'vue';
import type { SolutionDiff, SolutionDiffSection, SolutionPlan, SolutionPlanMember, SolutionPlanTrigger } from '../api/types';
import { capWords, diffMark, removedItems, triggerSource, wakeWords, type ValueChange } from '../lib/solutions';
import { rawCron } from '../lib/scheduleWords';

/**
 * EVERYTHING A PACKAGE'S INSTALL MAKES, WHOLE: the install wizard's Review, and the full plan under
 * the guided setup wizard's Advanced… on Ready. Every member's instructions and every trigger's
 * instruction are shown in full; an update marks what is new or changed (`diff`), shows each changed
 * value as it was beside what it becomes (`memberWas`, `triggerWas`), and lists what goes.
 */
const props = defineProps<{
  plan: SolutionPlan;
  diff?: SolutionDiff | null;
  memberWas?: (member: SolutionPlanMember) => ValueChange[];
  triggerWas?: (trigger: SolutionPlanTrigger) => ValueChange[];
}>();

const removed = computed(() => (props.diff ? removedItems(props.diff) : []));

function mark(section: keyof SolutionDiff, name: string) {
  return diffMark(props.diff?.[section] as SolutionDiffSection | undefined, name);
}

const memberChanges = (member: SolutionPlanMember) => props.memberWas?.(member) ?? [];
const triggerChanges = (trigger: SolutionPlanTrigger) => props.triggerWas?.(trigger) ?? [];

const openSkills = ref<Record<string, boolean>>({});

function memberKindLine(member: SolutionPlanMember) {
  if (member.kind === 'plugin') return `plugin ${member.pluginId ?? ''} ${member.pluginVersion ?? ''}`.trim();
  return `agent${member.preset ? `, preset ${member.preset}` : ', the team chooses the preset'}`;
}
</script>

<template>
  <div data-plan-review>
    <section v-if="plan.team.instructions" class="q-mb-md">
      <div class="solution-heading">Team instructions</div>
      <pre class="solution-text" data-team-instructions>{{ plan.team.instructions }}</pre>
    </section>

    <section class="q-mb-md">
      <div class="solution-heading">Members</div>
      <div v-for="member in plan.members" :key="member.name" class="solution-item" :data-member="member.name">
        <div class="row items-center q-gutter-x-sm">
          <span class="text-weight-medium">{{ member.name }}</span>
          <span class="os-text-muted">{{ member.role === 'manager' ? 'Manager' : 'member' }}, {{ memberKindLine(member) }}</span>
          <q-badge v-if="mark('members', member.name)" outline color="primary" :label="mark('members', member.name)!" data-mark />
        </div>
        <pre v-if="member.instructions" class="solution-text" data-instructions>{{ member.instructions }}</pre>
        <div v-if="memberChanges(member).length > 0" class="solution-changes" data-changes>
          <div class="solution-changes-title">What changes</div>
          <div v-for="change in memberChanges(member)" :key="change.label" class="solution-change" :data-change="change.label">
            <div class="solution-change-label">{{ change.label }}</div>
            <div class="solution-change-side" data-was><div class="solution-change-head">Was</div><pre class="solution-text">{{ change.was }}</pre></div>
            <div class="solution-change-side" data-now><div class="solution-change-head">Now</div><pre class="solution-text">{{ change.now }}</pre></div>
          </div>
        </div>
      </div>
    </section>

    <section class="q-mb-md">
      <div class="solution-heading">Triggers</div>
      <div v-if="plan.triggers.length === 0" class="os-text-muted">None</div>
      <div v-for="trigger in plan.triggers" :key="trigger.name" class="solution-item" :data-trigger="trigger.name">
        <div class="row items-center q-gutter-x-sm">
          <span class="text-weight-medium">{{ trigger.name }}</span>
          <span class="os-text-muted">{{ trigger.kind }}</span>
          <q-badge v-if="mark('triggers', trigger.name)" outline color="primary" :label="mark('triggers', trigger.name)!" data-mark />
        </div>
        <dl class="solution-facts">
          <dt>When</dt>
          <dd>
            <span data-source>{{ triggerSource(trigger) }}</span>
            <!-- The raw schedule, for whoever wants it, under Advanced. -->
            <details v-if="trigger.cron" class="solution-advanced" data-cron>
              <summary>Advanced</summary>
              <span class="mono">{{ rawCron(trigger.cron, trigger.timezone) }}</span>
            </details>
          </dd>
          <dt>Wakes</dt><dd data-target>{{ trigger.member }}</dd>
          <dt>Manager</dt><dd data-wake>{{ wakeWords(trigger.wakeManager) }}</dd>
          <dt>Daily cap</dt><dd data-cap>{{ capWords(trigger.dailyTokenCap) }}</dd>
          <dt>Busy</dt><dd>{{ trigger.idleOnly ? 'Skipped while the member is busy' : 'Queued while the member is busy' }}</dd>
        </dl>
        <pre class="solution-text" data-instruction>{{ trigger.instruction }}</pre>
        <!-- A CHANGED TRIGGER: each value the update changes, what it was beside what it becomes. -->
        <div v-if="triggerChanges(trigger).length > 0" class="solution-changes" data-changes>
          <div class="solution-changes-title">What changes</div>
          <div v-for="change in triggerChanges(trigger)" :key="change.label" class="solution-change" :data-change="change.label">
            <div class="solution-change-label">{{ change.label }}</div>
            <template v-if="change.long">
              <div class="solution-change-side" data-was><div class="solution-change-head">Was</div><pre class="solution-text">{{ change.was }}</pre></div>
              <div class="solution-change-side" data-now><div class="solution-change-head">Now</div><pre class="solution-text">{{ change.now }}</pre></div>
            </template>
            <template v-else>
              <div class="solution-change-side" data-was><div class="solution-change-head">Was</div>{{ change.was }}</div>
              <div class="solution-change-side" data-now><div class="solution-change-head">Now</div>{{ change.now }}</div>
            </template>
          </div>
        </div>
      </div>
    </section>

    <section class="q-mb-md">
      <div class="solution-heading">Skills</div>
      <div v-if="plan.skills.length === 0" class="os-text-muted">None</div>
      <div v-for="skill in plan.skills" :key="skill.name" class="solution-item" :data-skill="skill.name">
        <div class="row items-center q-gutter-x-sm">
          <span class="text-weight-medium mono">{{ skill.name }}</span>
          <span class="os-text-muted">for {{ skill.roles.join(', ') }}</span>
          <q-badge v-if="mark('skills', skill.name)" outline color="primary" :label="mark('skills', skill.name)!" data-mark />
          <q-btn
            flat
            dense
            no-caps
            size="sm"
            :label="openSkills[skill.name] ? 'Hide the skill' : 'Show the skill'"
            @click="openSkills = { ...openSkills, [skill.name]: !openSkills[skill.name] }"
          />
        </div>
        <div>{{ skill.description }}</div>
        <pre v-if="openSkills[skill.name]" class="solution-text" data-skill-body>{{ skill.body }}</pre>
      </div>
    </section>

    <section class="q-mb-md">
      <div class="solution-heading">Sites</div>
      <div v-if="plan.sites.length === 0" class="os-text-muted">None</div>
      <div v-for="site in plan.sites" :key="site.name" :data-site="site.name">
        <span class="mono">{{ site.name }}</span>
        <span class="os-text-muted"> · {{ site.files.length }} {{ site.files.length === 1 ? 'file' : 'files' }}</span>
        <q-badge v-if="mark('sites', site.name)" outline color="primary" class="q-ml-sm" :label="mark('sites', site.name)!" data-mark />
      </div>
    </section>

    <section class="q-mb-md">
      <div class="solution-heading">Tools</div>
      <div v-if="!plan.tools" class="os-text-muted">None</div>
      <div v-else data-tools>
        <div>Installed as <span class="mono">{{ plan.tools.installedAs }}/</span> in the team's folder:</div>
        <div v-for="file in plan.tools.files" :key="file" class="mono">
          {{ file }}
          <q-badge v-if="mark('tools', file)" outline color="primary" class="q-ml-sm" :label="mark('tools', file)!" data-mark />
        </div>
      </div>
    </section>

    <section class="q-mb-md">
      <div class="solution-heading">Plugins</div>
      <div v-if="plan.plugins.length === 0" class="os-text-muted">None</div>
      <div v-for="plugin in plan.plugins" :key="plugin.id" :data-plugin="plugin.id">
        <span class="mono">{{ plugin.id }} {{ plugin.version }}</span>
        <span v-if="plugin.description" class="os-text-muted"> · {{ plugin.description }}</span>
        <q-badge v-if="mark('plugins', plugin.id)" outline color="primary" class="q-ml-sm" :label="mark('plugins', plugin.id)!" data-mark />
      </div>
    </section>

    <section v-if="removed.length > 0" class="q-mb-md" data-removed>
      <div class="solution-heading">Removed by the update</div>
      <div v-for="item in removed" :key="item" data-removed-item>{{ item }}</div>
    </section>
  </div>
</template>

<style scoped>
.solution-heading {
  font-weight: 600;
  margin-bottom: 4px;
}

.solution-item {
  margin-bottom: 12px;
}

.solution-text {
  margin: 4px 0 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: inherit;
  background: var(--os-surface-2, transparent);
  border-left: 2px solid var(--os-ink-faint, #ccc);
  padding: 4px 8px;
}

.solution-facts {
  display: grid;
  grid-template-columns: max-content 1fr;
  column-gap: 12px;
  row-gap: 2px;
  margin: 4px 0 0;
}

.solution-facts dt {
  color: var(--os-ink-muted);
}

.solution-facts dd {
  margin: 0;
}

.solution-advanced summary {
  cursor: pointer;
  color: var(--os-ink-muted);
  font-size: 0.85em;
}

/* An update's old and new values: a label, then Was and Now side by side. */
.solution-changes {
  margin-top: 6px;
}

.solution-changes-title {
  font-weight: 600;
  font-size: 0.9em;
}

.solution-change {
  display: grid;
  grid-template-columns: max-content 1fr 1fr;
  column-gap: 12px;
  align-items: start;
  margin-top: 4px;
}

.solution-change-label,
.solution-change-head {
  color: var(--os-ink-muted);
}

.solution-change-side {
  min-width: 0;
}

</style>
