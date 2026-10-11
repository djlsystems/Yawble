import { describe, expect, it } from 'vitest';
import {
  clipboardImage,
  imagePasteChordBytes,
  isImagePasteChord,
  NoConciergeToReceive,
  refusedUploadSentence,
  xtermSeesMac,
} from '../conciergeAttachment';
import { ActionRefused } from '../../api/client';

const chord = (overrides: Partial<Parameters<typeof isImagePasteChord>[0]> = {}) => ({
  type: 'keydown',
  key: 'v',
  code: 'KeyV',
  altKey: true,
  shiftKey: false,
  ctrlKey: false,
  metaKey: false,
  ...overrides,
});

describe('the Alt+V chord', () => {
  it('is Alt+V by key, wherever the layout puts V', () => {
    expect(isImagePasteChord(chord())).toBe(true);
    expect(isImagePasteChord(chord({ key: 'V', shiftKey: true }))).toBe(true);
    // Dvorak types v on the key QWERTY calls Period.
    expect(isImagePasteChord(chord({ code: 'Period' }))).toBe(true);
  });

  it('is not the V position when the layout types another letter there (Dvorak: k)', () => {
    expect(isImagePasteChord(chord({ key: 'k' }))).toBe(false);
    expect(isImagePasteChord(chord({ key: 'K', shiftKey: true }))).toBe(false);
  });

  it('falls back to the V position only when key is no ASCII letter: a Mac √, a Cyrillic м', () => {
    expect(isImagePasteChord(chord({ key: '√' }))).toBe(true);
    expect(isImagePasteChord(chord({ key: 'м' }))).toBe(true);
    expect(isImagePasteChord(chord({ key: '√', code: 'KeyB' }))).toBe(false);
  });

  it('is not plain V, Ctrl+Alt+V, Cmd+Alt+V, or a keyup', () => {
    expect(isImagePasteChord(chord({ altKey: false }))).toBe(false);
    expect(isImagePasteChord(chord({ ctrlKey: true }))).toBe(false);
    expect(isImagePasteChord(chord({ metaKey: true }))).toBe(false);
    expect(isImagePasteChord(chord({ type: 'keyup' }))).toBe(false);
  });

  it('forwards the bytes xterm would have sent off a Mac: ESC and v, upper case only with Shift', () => {
    expect(imagePasteChordBytes({ key: 'v', shiftKey: false }, false)).toBe('\x1bv');
    expect(imagePasteChordBytes({ key: 'V', shiftKey: true }, false)).toBe('\x1bV');
    // Caps Lock: xterm reads the key and Shift, not the case the layout produced.
    expect(imagePasteChordBytes({ key: 'V', shiftKey: false }, false)).toBe('\x1bv');
    expect(imagePasteChordBytes({ key: 'м', shiftKey: false }, false)).toBe('\x1bv');
  });

  it('forwards the character a Mac composed, and nothing for a dead key', () => {
    expect(imagePasteChordBytes({ key: '√', shiftKey: false }, true)).toBe('√');
    expect(imagePasteChordBytes({ key: 'Dead', shiftKey: false }, true)).toBe('');
  });

  it('sees a Mac where xterm does', () => {
    expect(xtermSeesMac('MacIntel')).toBe(true);
    expect(xtermSeesMac('Win32')).toBe(false);
    expect(xtermSeesMac('Linux x86_64')).toBe(false);
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

describe('the sentence a refused upload is shown with', () => {
  const refused = (status: number, body: Record<string, unknown>, message = 'raw') =>
    Object.assign(new ActionRefused(message, body), { status });

  it('is the body\'s error, else its message', () => {
    expect(refusedUploadSentence(refused(409, { error: 'No CLI.' }))).toBe('No CLI.');
    expect(refusedUploadSentence(refused(409, { message: 'No CLI here.' }))).toBe('No CLI here.');
  });

  it('is a sentence of its own for a 409 that said nothing, and the failure\'s words otherwise', () => {
    expect(refusedUploadSentence(Object.assign(new Error('409 Conflict'), { status: 409 }))).toBe(NoConciergeToReceive);
    expect(refusedUploadSentence(refused(409, { error: '  ' }))).toBe(NoConciergeToReceive);
    expect(refusedUploadSentence(new Error('Too big.'))).toBe('Too big.');
  });
});
