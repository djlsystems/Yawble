import { describe, expect, it } from 'vitest';
import { filterWords, matchesWords } from '../filterWords';

describe('the filter boxes rule', () => {
  it('splits text into lower-case words, and reads cleared as none', () => {
    expect(filterWords('  Job   TRACKER ')).toEqual(['job', 'tracker']);
    expect(filterWords('')).toEqual([]);
    expect(filterWords(null)).toEqual([]);
    expect(filterWords(undefined)).toEqual([]);
  });

  it('matches when every word is found in some field, whatever the case', () => {
    expect(matchesWords(['job', 'hunt'], 'Job Tracker', 'Job Hunt')).toBe(true);
    expect(matchesWords(['TRACK'.toLowerCase()], 'Job Tracker')).toBe(true);
    expect(matchesWords(['job', 'news'], 'Job Tracker', 'Job Hunt')).toBe(false);
  });

  it('matches anything with no words, and skips empty fields', () => {
    expect(matchesWords([], 'anything')).toBe(true);
    expect(matchesWords([])).toBe(true);
    expect(matchesWords(['tracker'], null, undefined, 'Tracker')).toBe(true);
    expect(matchesWords(['tracker'], null, undefined)).toBe(false);
  });

  it('does not match a word across two fields', () => {
    expect(matchesWords(['jobtracker'], 'job', 'tracker')).toBe(false);
  });
});
