import { describe, expect, it } from 'vitest';
import {
  bracketedPaste,
  clipboardImage,
  imagePasteChordBytes,
  isImagePasteChord,
} from '../conciergeAttachment';

const chord = (overrides: Partial<Parameters<typeof isImagePasteChord>[0]> = {}) => ({
  type: 'keydown',
  key: 'v',
  code: 'KeyV',
  altKey: true,
  ctrlKey: false,
  metaKey: false,
  ...overrides,
});

describe('the Alt+V chord', () => {
  it('is Alt+V on keydown, by code, so a Mac Option+V that types a character still counts', () => {
    expect(isImagePasteChord(chord())).toBe(true);
    expect(isImagePasteChord(chord({ key: '√' }))).toBe(true);
  });

  it('is not plain V, Ctrl+Alt+V, Cmd+Alt+V, or a keyup', () => {
    expect(isImagePasteChord(chord({ altKey: false }))).toBe(false);
    expect(isImagePasteChord(chord({ ctrlKey: true }))).toBe(false);
    expect(isImagePasteChord(chord({ metaKey: true }))).toBe(false);
    expect(isImagePasteChord(chord({ type: 'keyup' }))).toBe(false);
  });

  it('forwards the bytes xterm would have sent: ESC and the letter, or the character a Mac composed', () => {
    expect(imagePasteChordBytes({ key: 'v' })).toBe('\x1bv');
    expect(imagePasteChordBytes({ key: 'V' })).toBe('\x1bV');
    expect(imagePasteChordBytes({ key: '√' })).toBe('√');
  });
});

describe('reading the clipboard', () => {
  const item = (types: Record<string, Blob>) => ({ types: Object.keys(types), getType: async (t: string) => types[t]! });

  it('answers the first image as a file of its type', async () => {
    const image = await clipboardImage({ read: async () => [item({ 'image/webp': new Blob([new Uint8Array([1])]) })] });

    expect(image?.type).toBe('image/webp');
  });

  it('answers null for text beside an image, no API, and a refusal', async () => {
    const both = { 'text/plain': new Blob(['x']), 'image/png': new Blob([new Uint8Array([1])]) };

    expect(await clipboardImage({ read: async () => [item(both)] })).toBeNull();
    expect(await clipboardImage(undefined)).toBeNull();
    expect(await clipboardImage({})).toBeNull();
    expect(await clipboardImage({ read: () => Promise.reject(new Error('denied')) })).toBeNull();
  });
});

it('wraps a path in the bracketed-paste markers', () => {
  expect(bracketedPaste('/a/b.png')).toBe('\x1b[200~/a/b.png\x1b[201~');
});
