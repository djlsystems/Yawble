import { ref, type Ref } from 'vue'
import { DefaultTeamsPeriod, TeamsPeriodKey, readTeamsPeriod, writeTeamsPeriod, type TeamsPeriodChoice } from './teamsPeriod'

/**
 * THE ONE PERIOD CHOICE THE TEAMS LIST AND A TEAM PAGE SHARE: Daily or Monthly, over the last two months or a custom
 * range, remembered in this browser under `harness.teamsCostPeriod`. A person who picks Monthly on the Teams list and
 * opens a team sees that team's tiles over the same months - the row they clicked, in more detail - and a period picked
 * on a team page is the list's when they go back.
 *
 * ONE REF FOR EVERY SURFACE ON SCREEN, read again from storage each time a surface takes it, so a surface mounted later
 * starts from what was last chosen anywhere.
 */
const shared = ref<TeamsPeriodChoice>(DefaultTeamsPeriod)

function stored(): TeamsPeriodChoice {
  try {
    return readTeamsPeriod(localStorage.getItem(TeamsPeriodKey))
  } catch {
    return DefaultTeamsPeriod
  }
}

export function useCostPeriodChoice(): { choice: Ref<TeamsPeriodChoice>; choose: (next: TeamsPeriodChoice) => void } {
  const now = stored()
  if (writeTeamsPeriod(now) !== writeTeamsPeriod(shared.value)) shared.value = now

  return {
    choice: shared,
    choose(next: TeamsPeriodChoice) {
      shared.value = next
      try {
        localStorage.setItem(TeamsPeriodKey, writeTeamsPeriod(next))
      } catch {
        // Still applies for this session.
      }
    },
  }
}
