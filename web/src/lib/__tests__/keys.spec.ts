import { describe, expect, it } from 'vitest';
import { composedLine, ControlKeys, ctrlSequence } from '../keys';

describe('ControlKeys', () => {
  it('sends the escape byte for Esc', () => {
    expect(ControlKeys.Esc).toBe('\x1b');
  });

  it('sends a real tab for Tab', () => {
    expect(ControlKeys.Tab).toBe('\t');
  });

  /** Arrows are CSI sequences, not characters. A TUI reads history with these. */
  it('sends CSI sequences for the arrows', () => {
    expect(ControlKeys.Up).toBe('\x1b[A');
    expect(ControlKeys.Down).toBe('\x1b[B');
    expect(ControlKeys.Right).toBe('\x1b[C');
    expect(ControlKeys.Left).toBe('\x1b[D');
  });
});

describe('composedLine', () => {
  /**
   * CR, not LF. Enter on a real keyboard sends \r; a prompt that distinguishes the two simply will
   * not submit on \n, and the line then sits there looking sent.
   */
  it('submits with a carriage return', () => {
    expect(composedLine('ask the manager what time it is')).toBe(
      'ask the manager what time it is\r',
    );
  });

  /** Dictation habitually leaves a trailing space. The spaces between words are the message. */
  it('trims the ends without touching the middle', () => {
    expect(composedLine('  say hello  world ')).toBe('say hello  world\r');
  });

  /** A stray tap on Send must not enter a bare newline at the agent's prompt. */
  it('has nothing to send for blank input', () => {
    expect(composedLine('')).toBeNull();
    expect(composedLine('   \n\t ')).toBeNull();
  });
});

describe('ctrlSequence', () => {
  /**
   * Ctrl-<letter> is the letter's position in the alphabet as a raw byte. C is the third letter, so
   * Ctrl-C is 0x03. Getting this wrong means no interrupt, which is the most important key on the
   * bar and the one a person reaches for when something has gone wrong.
   */
  it('maps a letter to its control byte', () => {
    expect(ctrlSequence('c')).toBe('\x03');
    expect(ctrlSequence('a')).toBe('\x01');
    expect(ctrlSequence('d')).toBe('\x04');
    expect(ctrlSequence('z')).toBe('\x1a');
  });

  it('is case-insensitive, because the bar shows capitals', () => {
    expect(ctrlSequence('C')).toBe(ctrlSequence('c'));
  });

  it('passes a non-letter through rather than mangling it into a random byte', () => {
    expect(ctrlSequence('1')).toBe('1');
    expect(ctrlSequence('[')).toBe('[');
  });
});
