import { describe, expect, it } from 'vitest';
import { settingDefaultWords } from '../solutions';
import type { SolutionPersonSetting } from '../../api/types';

const setting = (type: string, value: unknown): SolutionPersonSetting =>
  ({ member: 'Fetcher', setting: 'places', type, default: value, required: false, description: '' }) as unknown as SolutionPersonSetting;

describe("a person-only setting's default in words", () => {
  it('says a list as its items joined by commas, never as JSON, so it can wrap', () => {
    const words = settingDefaultWords(setting('list', ['Woburn', 'Burlington', 'Salem NH']));
    expect(words).toBe('Woburn, Burlington, Salem NH');
    expect(words).not.toContain('"');
  });

  it('says an empty list as none, a toggle as on or off, and anything else as itself', () => {
    expect(settingDefaultWords(setting('list', []))).toBe('none');
    expect(settingDefaultWords(setting('bool', true))).toBe('on');
    expect(settingDefaultWords(setting('bool', false))).toBe('off');
    expect(settingDefaultWords(setting('number', 20))).toBe('20');
    expect(settingDefaultWords(setting('string', 'upper'))).toBe('upper');
  });

  it('is nothing when the manifest gives no default', () => {
    expect(settingDefaultWords(setting('string', null))).toBeNull();
    expect(settingDefaultWords(setting('string', undefined))).toBeNull();
  });
});
