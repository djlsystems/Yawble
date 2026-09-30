// @vitest-environment happy-dom
//
// ADMIN → AGENTS, THE TOOLS CAPTION. Per preset, from the Host's pre-flight: a member's preset is
// isolated, has foreign tools (named), is not verified, or was not measured - never green without a
// listing - and shows its recorded gaps. The Concierge's connectors and servers are listed as
// information, never as a warning.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { listCatalog, getAgentAuth, getAgentTools, getTenantSettings } = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  getAgentAuth: vi.fn(),
  getAgentTools: vi.fn(),
  getTenantSettings: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listCatalog,
  getAgentAuth,
  getAgentTools,
  getTenantSettings,
}));

import AgentsDialog from '../AgentsDialog.vue';
import type { Agent, PresetToolReport } from '../../api/types';
import { mountDialog, resetBody } from '../../test/mountQuasar';

const preset = (name: string, mode: Agent['mode'], fileName: string, builtIn = true): Agent => ({
  name,
  mode,
  builtIn,
  launch: { fileName, arguments: [] },
  tags: [],
  tagsFromOperator: false,
  buildTags: builtIn ? [] : null,
});

const report = (over: Partial<PresetToolReport> & Pick<PresetToolReport, 'preset' | 'verdict'>): PresetToolReport => ({
  mode: 'headless',
  command: 'claude',
  foreign: [],
  loaded: [],
  switchedOff: [],
  gaps: [],
  ran: [],
  detail: null,
  ...over,
});

const gmail = { kind: 'connector' as const, name: 'claude.ai Gmail', source: 'claude.ai account', off: null };

beforeEach(() => {
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({
    agents: [
      preset('claude', 'Interactive', 'claude'),
      preset('claude-headless', 'Headless', 'claude'),
      preset('grok-headless', 'Headless', 'grok'),
      preset('copilot-headless', 'Headless', 'copilot'),
      preset('my-claude', 'Headless', 'claude', false),
      preset('leaky', 'Headless', 'claude', false),
    ],
  });
  getAgentAuth.mockReset();
  getAgentAuth.mockResolvedValue([]);
  getTenantSettings.mockReset();
  getTenantSettings.mockResolvedValue({ settings: [], roots: [] });
  getAgentTools.mockReset();
  getAgentTools.mockResolvedValue({
    at: '2026-09-30T02:00:00Z',
    running: false,
    presets: [
      report({
        preset: 'claude',
        mode: 'interactive',
        verdict: 'concierge',
        loaded: [gmail, { kind: 'skill', name: 'docs', source: null, off: null }],
      }),
      report({ preset: 'claude-headless', verdict: 'isolated', gaps: ["A repository's own .claude/settings.json still loads."] }),
      report({
        preset: 'grok-headless',
        command: 'grok',
        verdict: 'isolated',
        loaded: [{ kind: 'server', name: 'harness', source: null, off: null }],
      }),
      report({ preset: 'copilot-headless', command: 'copilot', verdict: 'notMeasured', detail: "'copilot' is not on PATH." }),
      report({ preset: 'my-claude', verdict: 'notVerified', foreign: [gmail] }),
      report({ preset: 'leaky', verdict: 'foreignFound', foreign: [gmail] }),
    ],
  });
});

afterEach(resetBody);

function toolsLine(name: string): HTMLElement {
  const found = [...document.body.querySelectorAll<HTMLElement>('.q-item')]
    .find((item) => item.querySelector('.mono')?.textContent?.trim().split(/\s+/)[0] === name);
  if (!found) throw new Error(`no row for ${name}`);
  const line = found.querySelector<HTMLElement>('.agent-tools-line');
  if (!line) throw new Error(`no tools line on ${name}`);
  return line;
}

describe('AgentsDialog, the tools caption', () => {
  it('shows each state per preset, the foreign tools by name, and a declared preset with no listing as not measured', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(getAgentTools).toHaveBeenCalled();

    const isolated = toolsLine('claude-headless');
    expect(isolated.textContent).toContain('Isolated: harness and its own tools only');
    expect(isolated.querySelector('.text-positive')).not.toBeNull();
    expect(toolsLine('grok-headless').textContent).toContain('Isolated');

    const leaky = toolsLine('leaky');
    expect(leaky.textContent).toContain('Foreign tools found:');
    expect(leaky.textContent).toContain('claude.ai Gmail');
    expect(leaky.querySelector('.text-warning')).not.toBeNull();

    const unverified = toolsLine('my-claude');
    expect(unverified.textContent).toContain('Not verified: this preset declares no isolation, and would get:');
    expect(unverified.textContent).toContain('claude.ai Gmail');
    expect(unverified.querySelector('.text-warning')).not.toBeNull();

    const unmeasured = toolsLine('copilot-headless');
    expect(unmeasured.textContent).toContain('Tools not measured');
    expect(unmeasured.textContent).toContain("'copilot' is not on PATH.");
    expect(unmeasured.querySelector('.text-positive')).toBeNull();

    wrapper.unmount();
  });

  it("lists the Concierge's connectors as information, never as a warning, and not its skills", async () => {
    const wrapper = await mountDialog(AgentsDialog);

    const concierge = toolsLine('claude');
    expect(concierge.textContent).toContain('Concierge has:');
    expect(concierge.textContent).toContain('claude.ai Gmail');
    expect(concierge.textContent).not.toContain('docs');
    expect(concierge.querySelector('.text-warning')).toBeNull();
    expect(concierge.querySelector('.os-text-muted')).not.toBeNull();

    wrapper.unmount();
  });

  it("shows a preset's recorded gaps under it", async () => {
    const wrapper = await mountDialog(AgentsDialog);

    const row = toolsLine('claude-headless').closest('.q-item')!;
    expect(row.querySelector('.agent-tools-gap')?.textContent).toContain(
      "Gap: A repository's own .claude/settings.json still loads.",
    );

    wrapper.unmount();
  });

  it('reads an unanswered pre-flight as not listed yet, never as isolated', async () => {
    getAgentTools.mockRejectedValue(new Error('offline'));
    const wrapper = await mountDialog(AgentsDialog);

    expect(toolsLine('claude-headless').textContent).toContain('Tools not listed yet');
    expect(toolsLine('claude-headless').textContent).not.toContain('Isolated');

    wrapper.unmount();
  });
});
