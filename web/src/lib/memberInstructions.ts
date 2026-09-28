/**
 * A member's OWN INSTRUCTIONS: `systemPrompt` on the member row, added to its prompt after the
 * built-in role prompt and before the team instructions. A Manager writes them once, when it hires;
 * after that only a person changes them. An agent member has them; a plugin member has no prompt
 * and so never shows the field.
 *
 * ONE LABEL for Add member and Member settings, so the two dialogs name the same thing alike.
 */
export const MemberInstructionsLabel = 'Instructions (optional)'

/** Under the field for a member - hired by a person or by the Manager. */
export const MemberInstructionsHint =
  'Who this member is and how it works: added to its prompt after the built-in Member prompt, before the team instructions.'

/** Under the field on the Manager's own card: its instructions are procedures only it runs. */
export const ManagerInstructionsHint =
  "The Manager's own instructions: procedures only it runs, such as what to do when a trigger wakes it."

/** Member settings: a saved change is read at the member's next wake. */
export const MemberInstructionsTakesEffect = 'Takes effect on its next run.'

/** Who last set a member's instructions, as `GET /api/teams/{team}/containers/{name}` answers it. */
export interface InstructionsProvenance {
  systemPromptSetBy?: string | null
  systemPromptSetByKind?: 'manager' | 'person' | null
  systemPromptSetAt?: string | null
}

/**
 * The line under the field: who last set it and when, or null when nobody is recorded - a member
 * from before the platform kept this shows nothing until it is next edited.
 *
 * The date is the UTC calendar day of `systemPromptSetAt`, so the line reads the same wherever it
 * is opened.
 */
export function instructionsWrittenBy(provenance: InstructionsProvenance | null | undefined): string | null {
  if (!provenance) return null

  if (provenance.systemPromptSetByKind === 'manager') return 'Written by the Manager when hiring'

  if (provenance.systemPromptSetByKind === 'person' && provenance.systemPromptSetBy) {
    const day = calendarDay(provenance.systemPromptSetAt)
    return day ? `Edited by ${provenance.systemPromptSetBy}, ${day}` : `Edited by ${provenance.systemPromptSetBy}`
  }

  return null
}

function calendarDay(iso: string | null | undefined): string | null {
  if (!iso) return null

  const at = new Date(iso)
  if (Number.isNaN(at.getTime())) return null

  return at.toISOString().slice(0, 10)
}
