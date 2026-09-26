<script setup lang="ts">
import type { TeamId } from '../api/types';
import { useRibbon } from '../lib/useRibbon';
import type { RibbonItem } from '../lib/ribbon';

/**
 * The ribbon, as a list. Same data, same disabled rules, same live items (team-switcher and
 * documents) as RibbonBar - this is not a second catalog of commands. Walking `ribbon.tabs` is
 * load-bearing: a hand-written list of today's buttons would drift the first time anyone edited
 * the `Ribbon` constant in `lib/ribbon.ts`.
 */
/**
 * ONE ARGUMENT, NO TEAM. Documents is a plain button, so there is no team to pass - the dialog
 * chooses its own default folder.
 *
 * A PARAMETER THAT IS STRUCTURALLY ALWAYS UNDEFINED IS WORSE THAN NO PARAMETER: it invites a
 * caller to pass something the receiver will honour, which is how a strip starts disagreeing with
 * a drawer.
 */
const emit = defineEmits<{ action: [action: string]; close: [] }>();

const {
  board,
  teams,
  activeTeamId,
  ribbon,
  titleOf,
  needsTeam,
  tooltipFor,
  badgeFor,
} = useRibbon();

function run(item: RibbonItem) {
  if (!item.action || needsTeam(item)) return;

  emit('action', item.action);
  emit('close');
}

function pickTeam(id: TeamId) {
  board.setActiveTeam(id);
  emit('close');
}

</script>

<template>
  <q-list class="ribbon-menu" padding>
    <template v-for="tab in ribbon.tabs" :key="tab.id">
      <q-item-label header class="ribbon-menu-section">{{ tab.label }}</q-item-label>

      <q-item v-if="titleOf(tab)" dense class="ribbon-menu-title">
        <q-item-section>{{ titleOf(tab) }}</q-item-section>
      </q-item>

      <template v-for="(item, index) in tab.items" :key="`${tab.id}-${index}`">
        <q-separator v-if="item.kind === 'separator'" />

        <q-expansion-item
          v-else-if="item.kind === 'team-switcher'"
          :icon="item.icon"
          :label="item.label"
          :disable="teams.length === 0"
          expand-separator
        >
          <q-item
            v-for="team in teams"
            :key="team.id"
            clickable
            :active="team.id === activeTeamId"
            active-class="text-primary"
            @click="pickTeam(team.id)"
          >
            <!-- THE SAME CONTROL AS THE DESKTOP SWITCH-TEAM MENU, so it carries the same glyph. -->
            <q-item-section avatar>
              <q-icon name="groups" size="18px" class="os-text-muted" aria-hidden="true" />
            </q-item-section>

            <!-- The LABEL alone. The identifier is never shown to a person: it is a
                 directory name and half of a container id, and a caption carrying a second
                 name for the same team only invites "which of these is it really called". -->
            <q-item-section>{{ team.name }}</q-item-section>
          </q-item>
        </q-expansion-item>

        <!-- The ordinary row, which Documents is one of. The disable is the prefix rule
             rather than a hand-written `!activeWorkTeamId`: `documents-manage` is unprefixed, so
             it is never disabled - and the no-active-team case is the one it exists to serve,
             since a team is deleted the moment its work merges and its documents are what is
             left. -->
        <q-item
          v-else
          clickable
          :disable="needsTeam(item)"
          @click="run(item)"
        >
          <q-item-section v-if="item.icon" avatar>
            <q-icon :name="item.icon" />
          </q-item-section>
          <q-item-section>
            <q-item-label>{{ item.label }}</q-item-label>
            <!-- On a phone a tooltip never fires on a disabled row, so the disabled reason
                 has to live in the row itself. Same words as the strip's tooltip. -->
            <q-item-label v-if="needsTeam(item) && tooltipFor(item)" caption>
              {{ tooltipFor(item) }}
            </q-item-label>

            <!-- THE SAME MARK THE DESKTOP STRIP CARRIES. A mark on the switch-team menu and not on
                 the mobile one beside it teaches a person that the absence of a mark means
                 something. Same shared state, same predicate.

                 A phone gets the WORDS rather than a hover, for the reason the disabled reason
                 above is a caption: nothing on a touch screen fires a tooltip. -->
            <q-item-label v-if="badgeFor(item)" caption class="ribbon-menu-warning">
              <q-icon :name="badgeFor(item)?.icon" size="14px" class="q-mr-xs" aria-hidden="true" />
              {{ badgeFor(item)?.label }}
            </q-item-label>
          </q-item-section>

          <q-item-section v-if="badgeFor(item)" side>
            <q-badge
              color="warning"
              text-color="dark"
              :aria-label="badgeFor(item)?.label"
            >{{ badgeFor(item)?.text }}</q-badge>
          </q-item-section>
        </q-item>
      </template>
    </template>
  </q-list>
</template>

<style scoped>
/* Same tokens as the desktop strip: this IS the ribbon, unfolded. The dark header would
   otherwise inherit white ink onto a light drawer. */
.ribbon-menu {
  color: var(--os-ink-muted);
}

.ribbon-menu-section {
  font-size: 11px;
  font-weight: 500;
  letter-spacing: 0.09em;
  text-transform: uppercase;
  color: var(--os-ink-faint);
}

/* The live heading - in practice the ACTIVE TEAM's name. The accent rule is the same one
   moment the strip uses, for the same reason: this block's every control is scoped to that team. */
.ribbon-menu-title {
  color: var(--os-ink);
  font-size: 13px;
  font-weight: 600;
  border-bottom: 2px solid var(--q-primary);
  margin: 0 16px 4px;
  min-height: 28px;
  padding-left: 0;
}

/* The mark's words, wrapping rather than ellipsising: it names the presets and the remedy, and a
   caption that truncated would cut off the part worth reading. */
.ribbon-menu-warning {
  white-space: normal;
}

.ribbon-menu :deep(.q-item.disabled) {
  color: var(--os-ink-faint);
  opacity: 1;
}
</style>
