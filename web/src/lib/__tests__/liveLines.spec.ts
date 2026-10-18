import { describe, expect, it } from 'vitest';
import { liveLineTime, parseLiveLine, splitLiveLine } from '../liveLines';

describe('parseLiveLine', () => {
  it('splits the time and the kind off the front of the line', () => {
    const line = parseLiveLine('2026-09-26T13:34:41.454Z\ttool\tBash: npm test');

    expect(line.at?.toISOString()).toBe('2026-09-26T13:34:41.454Z');
    expect(line).toMatchObject({ kind: 'tool', label: 'Bash', text: 'npm test' });
  });

  it('reads an empty time as none', () => {
    expect(parseLiveLine('\ttext\tgarbage')).toEqual({ at: null, kind: 'text', label: '', text: 'garbage' });
  });

  it('takes the kind from the Host, never from the shape of the line', () => {
    // Prose the Host called text stays text, however much it looks like a tool call.
    expect(parseLiveLine('\ttext\tBash: npm test')).toEqual({ at: null, kind: 'text', label: '', text: 'Bash: npm test' });
    expect(parseLiveLine('\ttext\tTests passed (12 bytes)').kind).toBe('text');
    // And a tool the web has never heard of is still a tool when the Host says so.
    expect(parseLiveLine('\ttool\tNewTool: something')).toEqual({ at: null, kind: 'tool', label: 'NewTool', text: 'something' });
  });

  it('reads a line with no kind the Host names as text, the second field and all', () => {
    expect(parseLiveLine('\tThe transcript ended early.')).toEqual({ at: null, kind: 'text', label: '', text: 'The transcript ended early.' });
    expect(parseLiveLine('\tbogus\tBash: x')).toEqual({ at: null, kind: 'text', label: '', text: 'bogus\tBash: x' });
  });

  it('keeps a line with no time field whole, a tab in it included', () => {
    expect(parseLiveLine('not a time\tstill the line')).toEqual({
      at: null,
      kind: 'text',
      label: '',
      text: 'not a time\tstill the line',
    });
    expect(parseLiveLine('plain')).toEqual({ at: null, kind: 'text', label: '', text: 'plain' });
  });

  it('keeps a tab inside the line itself', () => {
    expect(parseLiveLine('\ttext\ta\tb')).toEqual({ at: null, kind: 'text', label: '', text: 'a\tb' });
  });

  it('shows the time as hours, minutes and seconds', () => {
    expect(liveLineTime(new Date(2026, 8, 26, 9, 5, 7))).toBe('09:05:07');
  });
});

describe('splitLiveLine', () => {
  it("shows a tool call's name as its chip", () => {
    expect(splitLiveLine('tool', 'Bash: cd /src && git status')).toEqual({ kind: 'tool', label: 'Bash', text: 'cd /src && git status' });
    expect(splitLiveLine('tool', 'Read /data/notes.md')).toEqual({ kind: 'tool', label: 'Read', text: '/data/notes.md' });
    expect(splitLiveLine('tool', 'Read src/lib/kanban.ts')).toEqual({ kind: 'tool', label: 'Read', text: 'src/lib/kanban.ts' });
    expect(splitLiveLine('tool', 'MCP: tell')).toEqual({ kind: 'tool', label: 'MCP', text: 'tell' });
    expect(splitLiveLine('tool', 'Edit: src/app.ts')).toEqual({ kind: 'tool', label: 'Edit', text: 'src/app.ts' });
  });

  it('leaves a result whole, with no chip', () => {
    expect(splitLiveLine('result', '0 (817 bytes)')).toEqual({ kind: 'result', label: '', text: '0 (817 bytes)' });
  });

  it('strips the prefix of a user line and a note', () => {
    expect(splitLiveLine('user', 'User: carry on')).toEqual({ kind: 'user', label: 'User', text: 'carry on' });
    expect(splitLiveLine('note', 'Attachment: environment')).toEqual({ kind: 'note', label: 'Note', text: 'environment' });
  });

  it("shows the agent's own text whole", () => {
    expect(splitLiveLine('text', 'Sandbox fetch check passes headless.')).toEqual({
      kind: 'text',
      label: '',
      text: 'Sandbox fetch check passes headless.',
    });
  });
});
