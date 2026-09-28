/**
 * A team's TEAM INSTRUCTIONS: optional words a person writes for one team, added to the prompt of
 * EVERY agent member of that team - the Manager, members the Manager hired and members a person
 * added - after each member's built-in prompt and its own instructions. They never replace either.
 *
 * ONE LABEL AND ONE HINT for New Team and Team Settings, so the two dialogs cannot drift apart on
 * what the box does. The wire field stays `additionalInstructions`: only the words a person reads
 * changed.
 */
export const TeamInstructionsLabel = 'Team instructions (optional)'

export const TeamInstructionsHint =
  "How this team works: its purpose, rules, conventions and playbook. Every member reads it: the Manager and every member, whether the Manager hired them or a person added them. It is added after each member's built-in prompt and its own instructions, and never replaces them."

/** Under the field in Team Settings: saving re-prompts every member for its next wake. */
export const TeamInstructionsTakesEffect = "Takes effect on each member's next run."
