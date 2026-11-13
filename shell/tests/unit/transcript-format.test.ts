import { describe, expect, it } from 'vitest';
import { parseTranscript, toolName, toolSummary } from '../../src/renderer/src/transcriptFormat';

const lines = (...events: unknown[]) => events.map((e) => (typeof e === 'string' ? e : JSON.stringify(e))).join('\n');

describe('a run\'s output, for a person to read', () => {
  it('turns Claude Code stream-json into what was said, each tool call and result, and how it ended', () => {
    const out = parseTranscript(lines(
      { type: 'system', subtype: 'init', model: 'claude-sonnet-5-5', cwd: 'C:\\repo', tools: ['Bash'] },
      { type: 'assistant', message: { content: [{ type: 'thinking', thinking: 'Plan the baseline.' }] } },
      { type: 'assistant', message: { content: [{ type: 'text', text: 'Running the **baseline** now.' }] } },
      { type: 'assistant', message: { content: [{ type: 'tool_use', name: 'PowerShell', input: { command: 'dotnet test core' } }] } },
      { type: 'user', message: { content: [{ type: 'tool_result', content: 'Passed: 30', is_error: false }] } },
      { type: 'assistant', message: { content: [{ type: 'tool_use', name: 'mcp__bus__tell', input: { member: 'Wren', instruction: 'Run card 16' } }] } },
      { type: 'user', message: { content: [{ type: 'tool_result', content: [{ type: 'text', text: 'denied' }], is_error: true }] } },
      { type: 'tool_progress', elapsed: 3 },
      { type: 'result', duration_ms: 61234, num_turns: 7, total_cost_usd: 0.4321, is_error: false },
    ));
    expect(out.map((e) => e.kind)).toEqual(['start', 'thinking', 'text', 'tool', 'result', 'tool', 'result', 'end']);
    expect(out[0].body).toBe('model claude-sonnet-5-5 · in C:\\repo');
    expect(out[2].body).toBe('Running the **baseline** now.');
    expect(out[3]).toMatchObject({ title: 'PowerShell', body: 'dotnet test core' });
    expect(out[5]).toMatchObject({ title: 'bus: tell', body: 'Run card 16 → Wren' });
    expect(out[6]).toMatchObject({ body: 'denied', error: true });
    expect(out[7]).toMatchObject({ title: 'Run finished', body: '61 s · 7 turns · $0.43' });
  });

  it('keeps lines that are not JSON as text, and shortens a long result', () => {
    const long = Array.from({ length: 40 }, (_, i) => `line ${i}`).join('\n');
    const out = parseTranscript(lines('starting the CLI…', { type: 'user', message: { content: [{ type: 'tool_result', content: long }] } }));
    expect(out[0]).toMatchObject({ kind: 'plain', body: 'starting the CLI…' });
    expect(out[1].body).toContain('line 13');
    expect(out[1].body).not.toContain('line 14');
    expect(out[1].body).toContain('switch to Raw for all of it');
  });

  it('reads another CLI\'s JSON events by their text', () => {
    const out = parseTranscript(lines({ type: 'item.completed', item: { type: 'agent_message', text: 'Done.' } }));
    expect(out).toEqual([{ kind: 'note', title: 'item.completed · agent_message', body: 'Done.' }]);
  });

  it('names tools as a person would', () => {
    expect(toolName('mcp__bus__needs_decision')).toBe('bus: needs_decision');
    expect(toolName('Read')).toBe('Read');
    expect(toolSummary('Read', { file_path: 'C:\\a.txt' })).toBe('C:\\a.txt');
  });
});
