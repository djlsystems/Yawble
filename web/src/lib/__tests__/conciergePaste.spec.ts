import { describe, expect, it } from 'vitest';
import { normalisePaste } from '../conciergePaste';

describe('normalisePaste', () => {
  it('turns CRLF and lone CR line endings into one newline each', () => {
    expect(normalisePaste('one\r\ntwo\r\nthree')).toBe('one\ntwo\nthree');
    expect(normalisePaste('one\rtwo\r')).toBe('one\ntwo\n');
    expect(normalisePaste('a\r\n\r\nb')).toBe('a\n\nb');
  });

  it('keeps legitimate newlines, blank lines and tabs', () => {
    expect(normalisePaste('first\n\n\tindented\nlast\n')).toBe('first\n\n\tindented\nlast\n');
  });

  it('turns Unicode line and paragraph separators into newlines', () => {
    expect(normalisePaste('a\u2028b\u2029c')).toBe('a\nb\nc');
  });

  it('strips bracketed-paste markers carried in from another terminal', () => {
    expect(normalisePaste('\x1b[200~fix the build\x1b[201~')).toBe('fix the build');
    expect(normalisePaste('a\x1b[200~b\x1b[201~c\x1b[201~')).toBe('abc');
  });

  it('turns no-break spaces into ordinary spaces', () => {
    expect(normalisePaste('10\u00a0MB and\u202fthin\u2007figure')).toBe('10 MB and thin figure');
  });

  it('strips zero-width characters, the BOM and soft hyphens', () => {
    expect(normalisePaste('\ufeffzero\u200bwidth\u2060joined\u00adsoft')).toBe('zerowidthjoinedsoft');
  });

  it('strips NUL, other C0 and C1 controls, ESC and DEL', () => {
    expect(normalisePaste('a\x00b\x07c\x08d\x1be\x7ff\x85g\x9bh')).toBe('abcdefgh');
  });

  it('keeps Unicode text, including emoji ZWJ sequences and ZWNJ', () => {
    const text = 'Grüße, 日本語, Ελληνικά, עברית, 👩\u200d💻 family 👨\u200d👩\u200d👧, می\u200cخواهم';
    expect(normalisePaste(text)).toBe(text);
  });

  it('leaves plain text exactly as it was', () => {
    expect(normalisePaste('Explain src/app.ts line 42, please.')).toBe('Explain src/app.ts line 42, please.');
  });

  it('cleans a CRLF paste from a Fedora clipboard into lines the prompt accepts', () => {
    expect(normalisePaste('\x1b[200~Summarise\u00a0this:\r\n- item one\u200b\r\n- item two\r\n\x1b[201~')).toBe(
      'Summarise this:\n- item one\n- item two\n',
    );
  });
});
