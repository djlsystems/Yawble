import { ref } from 'vue';

/**
 * A link to one skill in Settings > Skills (Team settings' "Lessons" line links to the team's team_retros). The
 * app opens Settings at Skills when it is set (App.vue); the Skills page selects that skill and clears it.
 */
export const skillLink = ref<{ teamId: number; name: string } | null>(null);

export function openSkill(teamId: number, name: string) {
  skillLink.value = { teamId, name };
}
