<script setup lang="ts">
import type { TeamId } from '../api/types';
import { useRibbon } from '../lib/useRibbon';

/**
 * The team switcher's rows, ONCE. The command bar shows them in two places - the switcher on the
 * strip, and the same switcher as a submenu when it has spilled into the overflow menu - and a
 * mark present in one and missing from the other is an easy drift to ship.
 */
const emit = defineEmits<{ pick: [id: TeamId] }>();

const { teams, activeTeamId } = useRibbon();
</script>

<template>
  <q-list>
    <q-item
      v-for="team in teams"
      :key="team.id"
      v-close-popup
      clickable
      :active="team.id === activeTeamId"
      active-class="text-primary"
      @click="emit('pick', team.id)"
    >
      <!-- A plain list of names. The LABEL alone: the identifier is never shown to a person.
           The glyph carries no information and says so - `aria-hidden`, no tooltip. It is
           `groups`, the glyph the switch-team button itself uses. -->
      <q-item-section avatar>
        <q-icon name="groups" size="18px" class="os-text-muted" aria-hidden="true" />
      </q-item-section>
      <q-item-section>{{ team.name }}</q-item-section>
    </q-item>
  </q-list>
</template>
