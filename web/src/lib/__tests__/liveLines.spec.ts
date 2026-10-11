import { describe, expect, it } from 'vitest';
import { classifyLiveLine, liveLineTime, parseLiveLine } from '../liveLines';

describe('parseLiveLine', () => {
  it('splits the time off the front of the line', () => {
    const line = parseLiveLine('2026-09-26T13:34:41.454Z\tBash: npm test');

    expect(line.at?.toISOString()).toBe('2026-09-26T13:34:41.454Z');
    expect(line).toMatchObject({ kind: 'tool', label: 'Bash', text: 'npm test' });
  });

  it('reads an empty time as none', () => {
    expect(parseLiveLine('\tgarbage')).toEqual({ at: null, kind: 'text', label: '', text: 'garbage' });
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

  it('shows the time as hours, minutes and seconds', () => {
    expect(liveLineTime(new Date(2026, 8, 26, 9, 5, 7))).toBe('09:05:07');
  });
});

describe('classifyLiveLine', () => {
  it('reads a tool call by its name', () => {
    expect(classifyLiveLine('Bash: cd /src && git status')).toEqual({ kind: 'tool', label: 'Bash', text: 'cd /src && git status' });
    expect(classifyLiveLine('Read /data/notes.md')).toEqual({ kind: 'tool', label: 'Read', text: '/data/notes.md' });
    expect(classifyLiveLine('MCP: tell')).toEqual({ kind: 'tool', label: 'MCP', text: 'tell' });
    expect(classifyLiveLine('Edit: src/app.ts')).toEqual({ kind: 'tool', label: 'Edit', text: 'src/app.ts' });
  });

  it('leaves the agent prose that merely looks like a tool as text', () => {
    expect(classifyLiveLine('Note: the suite passes')).toEqual({ kind: 'text', label: '', text: 'Note: the suite passes' });
    expect(classifyLiveLine('Read the spec first.').kind).toBe('text');
    expect(classifyLiveLine('Read src/lib/kanban.ts')).toEqual({ kind: 'tool', label: 'Read', text: 'src/lib/kanban.ts' });
  });

  it('reads a tool result by its size', () => {
    expect(classifyLiveLine('0 (817 bytes)')).toEqual({ kind: 'result', label: '', text: '0 (817 bytes)' });
    expect(classifyLiveLine('(no text) (0 bytes)').kind).toBe('result');
  });

  it('reads user and attachment lines and strips their prefix', () => {
    expect(classifyLiveLine('User: carry on')).toEqual({ kind: 'user', label: 'User', text: 'carry on' });
    expect(classifyLiveLine('Attachment: environment')).toEqual({ kind: 'aside', label: 'Note', text: 'environment' });
  });

  it('treats anything else as the agent speaking', () => {
    expect(classifyLiveLine('Sandbox fetch check passes headless.')).toEqual({
      kind: 'text',
      label: '',
      text: 'Sandbox fetch check passes headless.',
    });
  });
});
