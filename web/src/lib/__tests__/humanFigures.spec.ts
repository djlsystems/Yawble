// THE HUMAN FIGURES' WORDS: Human Interventions as the route counted them, Human Attention Time from measured
// durations only - "no measured time", never 0, when nothing was measured - and Waiting on a person apart.
import { describe, expect, it } from 'vitest';
import {
  attentionText,
  humanLineText,
  interventionsText,
  waitingText,
  HumanAttentionLabel,
  HumanInterventionsLabel,
  WaitingOnAPersonLabel,
} from '../outcomes';

describe('human figures in words', () => {
  it('uses the words exactly', () => {
    expect([HumanInterventionsLabel, HumanAttentionLabel, WaitingOnAPersonLabel])
      .toEqual(['Human Interventions', 'Human Attention Time', 'Waiting on a person']);
  });

  it('says no measured time when the route measured none, never 0', () => {
    expect(attentionText({ interventions: 4, measuredInterventions: 0, attentionSeconds: null })).toBe('no measured time');
    expect(attentionText(undefined)).toBe('no measured time');
    expect(attentionText({ interventions: 4, measuredInterventions: 1, attentionSeconds: 754 })).toBe('12m');
  });

  it('counts interventions as the route did, and a dash from a Host that sent none', () => {
    expect(interventionsText({ interventions: 1200, measuredInterventions: 0, attentionSeconds: null })).toBe((1200).toLocaleString());
    expect(interventionsText(undefined)).toBe('—');
  });

  it('keeps Waiting on a person a duration of its own', () => {
    expect(waitingText(7200)).toBe('2h');
    expect(humanLineText({ interventions: 3, measuredInterventions: 0, attentionSeconds: null }))
      .toBe('3 Human Interventions · Human Attention Time no measured time');
    expect(humanLineText(undefined)).toBe('');
  });
});
