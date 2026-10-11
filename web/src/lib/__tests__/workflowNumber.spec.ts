import { describe, expect, it } from 'vitest';
import { WORKFLOW_NUMBER_HINT, workflowNumberTitle } from '../workflowNumber';

describe('what a shown workflow number means', () => {
  it('says the number is a place in the log, not a count, so numbers skip', () => {
    expect(WORKFLOW_NUMBER_HINT).toBe(
      "The workflow's place in this instance's log, not a count of workflows, so the numbers skip.");
  });

  it('names the workflow and then carries the shared wording', () => {
    expect(workflowNumberTitle(14)).toBe(`Workflow #14. ${WORKFLOW_NUMBER_HINT}`);
  });
});
