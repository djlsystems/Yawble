import { describe, expect, it } from 'vitest';
import { highlightLines, languageFor, splitHtmlLines } from '../src/highlight';
import {
  changeStops, editedAgo, formatSize, highlightStrength, latestChangeLine, lineDecorations, nextStop, notShownText, onlyChanged, previousStop,
  removedByLine, shownMarker, sortEntries, strongest,
} from '../src/markers';
import { PlaceHistory } from '../src/history';
import { NOW, ago, entry } from './fakes';

describe('highlighting', () => {
  it('picks the language from the core hint, else the file name, else plain text', () => {
    expect(languageFor('a/b.ts')).toBe('typescript');
    expect(languageFor('x.cs')).toBe('csharp');
    expect(languageFor('Dockerfile')).toBe('dockerfile');
    expect(languageFor('notes.unknownext')).toBe('plaintext');
    expect(languageFor('x.txt', 'python')).toBe('python');
    expect(languageFor('x.txt', 'cobol')).toBe('plaintext'); // not registered: common languages only
    expect(languageFor('data', undefined, 'json')).toBe('json');
  });

  it('keeps a span that crosses lines whole on every line', () => {
    const lines = splitHtmlLines('a<span class="c">/* one\ntwo */</span>b\nc');
    expect(lines).toEqual(['a<span class="c">/* one</span>', '<span class="c">two */</span>b', 'c']);
  });

  it('escapes what it shows: a file is never HTML', () => {
    const plain = highlightLines('<script>alert(1)</script>\n<b>x</b>', 'plaintext');
    expect(plain[0]).toBe('&lt;script&gt;alert(1)&lt;/script&gt;');
    const code = highlightLines('const s = "<img src=x onerror=alert(1)>";', 'javascript');
    expect(code.join('')).not.toContain('<img');
    expect(code[0]).toContain('hljs-');
  });

  it('gives one line per line of the file, without a trailing empty one', () => {
    expect(highlightLines('a\nb\n', 'plaintext')).toEqual(['a', 'b']);
    expect(highlightLines('a\r\nb', 'plaintext')).toEqual(['a', 'b']);
  });
});

describe('markers', () => {
  it('a folder shows the strongest marker', () => {
    expect(strongest('branch', 'live')).toBe('live');
    expect(strongest('recent', null)).toBe('recent');
    expect(strongest(null, null)).toBeNull();
  });

  it('a live file turns recent when its last write is over a minute old; hidden entries and links get none', () => {
    expect(shownMarker(entry('a', { marker: 'live', mtime: ago(10_000) }), NOW)).toBe('live');
    expect(shownMarker(entry('a', { marker: 'live', mtime: ago(90_000) }), NOW)).toBe('recent');
    expect(shownMarker(entry('a', { marker: 'recent', hidden: true }), NOW)).toBeNull();
    expect(shownMarker(entry('a', { marker: 'recent', kind: 'link' }), NOW)).toBeNull();
  });

  it('lists folders first and filters to changed entries', () => {
    const list = [entry('b.ts'), entry('src', { kind: 'dir', marker: 'recent' }), entry('a.ts', { marker: 'branch' }), entry('docs', { kind: 'dir' })];
    expect(sortEntries(list).map((e) => e.name)).toEqual(['docs', 'src', 'a.ts', 'b.ts']);
    expect(onlyChanged(sortEntries(list), NOW).map((e) => e.name)).toEqual(['src', 'a.ts']);
  });

  it('a line highlight is strong for a minute, then fades to 30 minutes, then is gone', () => {
    expect(highlightStrength(ago(30_000), NOW)).toBe(1);
    const mid = highlightStrength(ago(15 * 60_000), NOW);
    expect(mid).toBeGreaterThan(0.15);
    expect(mid).toBeLessThan(1);
    expect(highlightStrength(ago(29.9 * 60_000), NOW)).toBeGreaterThan(0.14);
    expect(highlightStrength(ago(31 * 60_000), NOW)).toBe(0);
  });

  it('decorates lines, keeps removals, and steps through changes', () => {
    const file = {
      lines: [
        { from: 2, to: 3, kind: 'added' as const, at: ago(5_000) },
        { from: 10, to: 10, kind: 'branch' as const },
        { from: 6, to: 6, kind: 'changed' as const, at: ago(40 * 60_000) }, // too old
      ],
      removed: [{ afterLine: 7, text: 'gone', at: ago(60_000) }, { afterLine: 1, text: 'old', at: ago(2 * 3600_000) }],
    };
    const d = lineDecorations(file, 12, NOW);
    expect(d[2].strength).toBe(1);
    expect(d[3].strength).toBe(1);
    expect(d[6].strength).toBe(0);
    expect(d[10].branch).toBe(true);
    expect([...removedByLine(file.removed, NOW).keys()]).toEqual([7]);
    const stops = changeStops(file, NOW);
    expect(stops).toEqual([2, 7, 10]);
    expect(nextStop(stops, 2)).toBe(7);
    expect(nextStop(stops, 10)).toBe(2); // wraps
    expect(previousStop(stops, 7)).toBe(2);
    expect(previousStop(stops, 1)).toBe(10);
    expect(latestChangeLine(file)).toBe(2);
  });

  it('says sizes, ages and why a file is not shown', () => {
    expect(formatSize(512)).toBe('512 B');
    expect(formatSize(3.4 * 1024 * 1024)).toBe('3.4 MB');
    expect(editedAgo(ago(2 * 60_000), NOW)).toBe('edited 2 min ago');
    expect(editedAgo(ago(5_000), NOW)).toBe('edited just now');
    expect(notShownText({ notShownReason: 'binary', size: 3.4 * 1024 * 1024 })).toBe("This file isn't shown (binary / 3.4 MB)");
    expect(notShownText({ notShownReason: 'tooLarge', size: 2 * 1024 * 1024 })).toBe("This file isn't shown (too large / 2 MB)");
  });
});

describe('back and forward', () => {
  it('works like a browser', () => {
    const h = new PlaceHistory();
    h.push({ dir: '', file: null });
    h.push({ dir: 'src', file: null });
    h.push({ dir: 'src', file: null }); // the same place twice is kept once
    h.push({ dir: 'src', file: 'src/a.ts' });
    expect(h.back()).toEqual({ dir: 'src', file: null });
    expect(h.back()).toEqual({ dir: '', file: null });
    expect(h.canBack).toBe(false);
    expect(h.forward()).toEqual({ dir: 'src', file: null });
    h.push({ dir: 'docs', file: null }); // drops what was ahead
    expect(h.canForward).toBe(false);
  });
});
