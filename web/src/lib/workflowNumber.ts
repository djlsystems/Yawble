/**
 * WHAT A WORKFLOW NUMBER IS, IN ONE PLACE. A workflow's number is the position of its first row in
 * this instance's log, so every other row in between (another team's message, a progress line, a
 * setting) takes a number too. A fresh install can show workflows 1, 14, 39, 45, which reads like
 * lost work unless the screen says why. Every place that shows the number hovers this wording, so
 * they all say the same thing.
 */
export const WORKFLOW_NUMBER_HINT =
  "The workflow's place in this instance's log, not a count of workflows, so the numbers skip.";

/** The hover text for a shown workflow number: which workflow, then what the number means. */
export function workflowNumberTitle(workflow: number | string): string {
  return `Workflow #${workflow}. ${WORKFLOW_NUMBER_HINT}`;
}
