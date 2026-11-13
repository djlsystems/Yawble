// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { COMPANION_LEVEL_TEXT, levelsUpTo } from '../../src/renderer/src/companionLevels';
import CompanionAsks from '../../src/renderer/src/components/CompanionAsks.vue';
import CompanionPanel from '../../src/renderer/src/components/CompanionPanel.vue';
import SettingsView from '../../src/renderer/src/components/SettingsView.vue';
import { isUiMethod, type CoreEvent, type YawbleApi } from '../../src/shared/api';

// A fake API only: no core, no network. Each method answers from `answers`; anything else fails the call.
function fakeApi(answers: Record<string, (params: any) => unknown>, extra: Partial<YawbleApi> = {}) {
  const calls: { method: string; params: any }[] = [];
  const listeners: ((e: CoreEvent) => void)[] = [];
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: (l) => { listeners.push(l); return () => { listeners.splice(listeners.indexOf(l), 1); }; },
    openTerminal: async () => {},
    pickFolder: async () => null,
    ...extra,
  };
  const emit = (event: string) => { for (const l of [...listeners]) l({ event, data: null }); };
  return { api, calls, emit, listeners };
}

const later = new Date(Date.now() + 5 * 60_000).toISOString();
const pairing = (level = 'act') => ({ id: 'p1', name: 'claude-code', level, code: 'K7Q2ZD', expires: later });
const action = (plan = 'Delete branch feature/x in team Alpha', state = 'pending') =>
  ({ id: 'a1', client: 'claude-code', tool: 'git_branches_delete', plan, expires: later, state });
const list = (over: Partial<{ clients: any[]; pairings: any[]; actions: any[] }> = {}) => ({ clients: [], pairings: [], actions: [], ...over });
const client = (name: string, revokedAt: string | null = null) =>
  ({ name, level: 'act', pairedAt: '2026-11-10T10:00:00Z', pairedBy: 'dana', lastUsedAt: '2026-11-11T09:00:00Z', revokedAt });

describe('CompanionAsks', () => {
  it('ShowsAPairingRequestWithNameLevelAndCode_ApproveCallsDecidePairing', async () => {
    let pending = true;
    const { api, calls } = fakeApi({
      'companion.list': () => list({ pairings: pending ? [pairing('act')] : [] }),
      'companion.decidePairing': () => { pending = false; return list(); },
    });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    const card = w.get('[data-test=companion-pairing]');
    expect(card.text()).toContain('claude-code');
    expect(card.text()).toContain('K7Q2ZD');
    expect(card.get('[data-test=companion-level-description]').text().length).toBeGreaterThan(10);
    await card.get('[data-test=companion-approve]').trigger('click');
    await flushPromises();
    expect(calls.find(c => c.method === 'companion.decidePairing')?.params).toEqual({ id: 'p1', approve: true, level: 'act' });
    expect(w.find('[data-test=companion-pairing]').exists()).toBe(false);
    w.unmount();
  });

  it('TheLevelCanOnlyBeLowered', async () => {
    const { api, calls } = fakeApi({
      'companion.list': () => list({ pairings: [pairing('act')] }),
      'companion.decidePairing': () => list(),
    });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    const select = w.get('[data-test=companion-pairing] select');
    expect(select.findAll('option').map(o => o.element.value)).toEqual(['read', 'act']);
    await select.setValue('read');
    await w.get('[data-test=companion-approve]').trigger('click');
    await flushPromises();
    expect(calls.find(c => c.method === 'companion.decidePairing')?.params).toEqual({ id: 'p1', approve: true, level: 'read' });
    w.unmount();
  });

  it('ShowsTheExactPlanAsText_ApproveCallsDecideAction', async () => {
    const plan = 'Merge <img src=x onerror="alert(1)"> into <b>main</b>\n  - 3 commits';
    let pending = true;
    const { api, calls } = fakeApi({
      'companion.list': () => list({ actions: pending ? [action(plan)] : [] }),
      'companion.decideAction': () => { pending = false; return { id: 'a1', state: 'done', plan, result: 'Merged <script>x</script>', nextId: null }; },
    });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    const card = w.get('[data-test=companion-action]');
    expect(card.text()).toContain('claude-code');
    expect(card.text()).toContain('git_branches_delete');
    expect(card.attributes('data-state')).toBe('pending');
    const pre = card.get('pre');
    expect(pre.element.textContent).toBe(plan);
    expect(pre.element.querySelector('img')).toBeNull();
    expect(pre.element.querySelector('b')).toBeNull();
    await card.get('[data-test=companion-approve]').trigger('click');
    await flushPromises();
    expect(calls.find(c => c.method === 'companion.decideAction')?.params).toEqual({ id: 'a1', approve: true });
    const outcome = w.get('[data-test=companion-outcome]');
    expect(outcome.attributes('data-state')).toBe('done');
    expect(outcome.text()).toContain('Merged <script>x</script>');
    expect(outcome.element.querySelector('script')).toBeNull();
    w.unmount();
  });

  it('ARunningRequestSaysSo_AndCannotBeDecidedAgain', async () => {
    const { api } = fakeApi({ 'companion.list': () => list({ actions: [action('Push branch x', 'running')] }) });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    const card = w.get('[data-test=companion-action]');
    expect(card.attributes('data-state')).toBe('running');
    expect(card.text()).toMatch(/running/i);
    expect(card.get('[data-test=companion-approve]').attributes('disabled')).toBeDefined();
    expect(card.get('[data-test=companion-deny]').attributes('disabled')).toBeDefined();
    w.unmount();
  });

  it('ShowsEachOutcome_AChangedListSaysANewRequestIsWaiting', async () => {
    const outcomes: Record<string, any> = {
      failed: { state: 'failed', result: 'git said no' },
      expired: { state: 'expired', result: null },
      changed: { state: 'changed', result: null, nextId: 'a2' },
    };
    for (const [state, o] of Object.entries(outcomes)) {
      const { api } = fakeApi({
        'companion.list': () => list({ actions: [action()] }),
        'companion.decideAction': () => ({ id: 'a1', plan: 'p', nextId: null, ...o }),
      });
      const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
      await flushPromises();
      await w.get('[data-test=companion-approve]').trigger('click');
      await flushPromises();
      const outcome = w.get('[data-test=companion-outcome]');
      expect(outcome.attributes('data-state')).toBe(state);
      if (state === 'failed') expect(outcome.text()).toContain('git said no');
      if (state === 'expired') expect(outcome.text()).toMatch(/nothing ran/i);
      if (state === 'changed') expect(outcome.get('[data-test=companion-next]').text()).toBe('New request a2 is waiting below.');
      await outcome.get('[data-test=companion-outcome-dismiss]').trigger('click');
      expect(w.find('[data-test=companion-outcome]').exists()).toBe(false);
      w.unmount();
    }
  });

  it('AnApprovalAnswersRunning_TheCardSaysRunning_TheOutcomeComesFromActionStatus', async () => {
    let state: 'pending' | 'running' | 'done' = 'pending';
    const { api, calls, emit } = fakeApi({
      'companion.list': () => list({ actions: state === 'done' ? [] : [action('Push branch x', state)] }),
      'companion.decideAction': () => { state = 'running'; return { id: 'a1', state: 'running', plan: 'Push branch x', result: null, nextId: null }; },
      'companion.actionStatus': () => ({ id: 'a1', state, plan: 'Push branch x', result: state === 'done' ? 'Pushed x' : null, nextId: null }),
    });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    await w.get('[data-test=companion-approve]').trigger('click');
    await flushPromises();
    // No outcome card for a run still under way: the request's own card says it is running.
    expect(w.find('[data-test=companion-outcome]').exists()).toBe(false);
    expect(w.get('[data-test=companion-action]').attributes('data-state')).toBe('running');
    expect(w.find('[data-test=companion-running]').exists()).toBe(true);
    const count = (m: string) => calls.filter(c => c.method === m).length;
    expect(count('companion.actionStatus')).toBe(0); // nothing asked while the list still shows it running

    state = 'done';
    emit('companion.changed');
    await flushPromises();
    expect(w.find('[data-test=companion-action]').exists()).toBe(false);
    const outcome = w.get('[data-test=companion-outcome]');
    expect(outcome.attributes('data-state')).toBe('done');
    expect(outcome.text()).toContain('Pushed x');
    expect(count('companion.actionStatus')).toBe(1);
    expect(calls.find(c => c.method === 'companion.actionStatus')?.params).toEqual({ id: 'a1' });
    emit('companion.changed');
    await flushPromises();
    expect(count('companion.actionStatus')).toBe(1); // settled: never asked again
    expect(count('companion.decideAction')).toBe(1); // the person's one click, nothing more
    expect(w.findAll('[data-test=companion-outcome]')).toHaveLength(1);
    w.unmount();
  });

  it('APendingAnswerOrAPendingListNeverLeadsToAnotherDecision', async () => {
    const { api, calls, emit } = fakeApi({
      'companion.list': () => list({ actions: [action()] }),
      // An odd answer (pending) to a Deny: never kept for re-polling, never decided again.
      'companion.decideAction': () => ({ id: 'a1', state: 'pending', plan: 'p', result: null, nextId: null }),
      'companion.actionStatus': () => ({ id: 'a1', state: 'pending', plan: 'p', result: null, nextId: null }),
    });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    emit('companion.changed');
    await flushPromises();
    expect(calls.filter(c => c.method === 'companion.decideAction')).toHaveLength(0); // a pending list decides nothing
    await w.get('[data-test=companion-action] [data-test=companion-deny]').trigger('click');
    await flushPromises();
    emit('companion.changed');
    await flushPromises();
    emit('companion.changed');
    await flushPromises();
    const decisions = calls.filter(c => c.method === 'companion.decideAction');
    expect(decisions).toHaveLength(1);
    expect(decisions[0].params).toEqual({ id: 'a1', approve: false });
    expect(calls.filter(c => c.method === 'companion.actionStatus')).toHaveLength(0);
    expect(w.find('[data-test=companion-outcome]').exists()).toBe(false);
    w.unmount();
  });

  it('DenySendsApproveFalse', async () => {
    const { api, calls } = fakeApi({
      'companion.list': () => list({ pairings: [pairing()], actions: [action()] }),
      'companion.decidePairing': () => list(),
      'companion.decideAction': () => ({ id: 'a1', state: 'denied', plan: 'p', result: null, nextId: null }),
    });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    await w.get('[data-test=companion-pairing] [data-test=companion-deny]').trigger('click');
    await w.get('[data-test=companion-action] [data-test=companion-deny]').trigger('click');
    await flushPromises();
    expect(calls.find(c => c.method === 'companion.decidePairing')?.params).toEqual({ id: 'p1', approve: false });
    expect(calls.find(c => c.method === 'companion.decideAction')?.params).toEqual({ id: 'a1', approve: false });
    expect(w.get('[data-test=companion-outcome]').attributes('data-state')).toBe('denied');
    w.unmount();
  });

  it('ShowsTheErrorWhenADecisionFails', async () => {
    const { api } = fakeApi({
      'companion.list': () => list({ pairings: [pairing()] }),
      'companion.decidePairing': () => { throw new Error('there is no pairing waiting'); },
    });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    await w.get('[data-test=companion-approve]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=companion-error]').text()).toContain('there is no pairing waiting');
    w.unmount();
  });

  it('RefreshesOnCompanionChanged', async () => {
    let n = 0;
    const { api, emit, listeners } = fakeApi({ 'companion.list': () => (n++ === 0 ? list() : list({ pairings: [pairing()] })) });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=companion-pairing]').exists()).toBe(false);
    emit('teams.changed');
    await flushPromises();
    expect(n).toBe(1);
    emit('companion.changed');
    await flushPromises();
    expect(w.find('[data-test=companion-pairing]').exists()).toBe(true);
    w.unmount();
    expect(listeners).toHaveLength(0);
  });

  it('HiddenInARemoteView', async () => {
    const { api, calls, listeners } = fakeApi({ 'companion.list': () => list({ pairings: [pairing()] }) }, { remote: true });
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=companion-pairing]').exists()).toBe(false);
    expect(calls).toHaveLength(0);
    expect(listeners).toHaveLength(0);
    w.unmount();
  });

  it('ShowsNothingWhenTheCoreHasNoCompanion', async () => {
    const { api } = fakeApi({});
    const w = mount(CompanionAsks, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=companion-asks]').exists()).toBe(false);
    w.unmount();
  });
});

const status = (over: Record<string, unknown> = {}) => ({
  endpoint: '\\\\.\\pipe\\yawble-ctl-0123456789abcdef',
  controlFile: 'C:\\Users\\me\\AppData\\Roaming\\yawble-companion\\control.json',
  binaryPath: 'C:\\Users\\me\\AppData\\Local\\Programs\\Yawble\\resources\\bin\\yawble.exe',
  claudeCommand: 'claude mcp add --scope user yawble -- "C:\\Users\\me\\AppData\\Local\\Programs\\Yawble\\resources\\bin\\yawble.exe" mcp',
  problem: null,
  ...over,
});

describe('CompanionPanel', () => {
  it('SettingsListsClients_RevokeAsksFirst', async () => {
    let revoked = false;
    const { api, calls } = fakeApi({
      'companion.list': () => list({ clients: [client('claude-code', revoked ? '2026-11-11T10:00:00Z' : null), client('old', '2026-11-01T10:00:00Z')] }),
      'companion.status': () => status(),
      'companion.revoke': () => { revoked = true; return { revoked: true }; },
    });
    const w = mount(CompanionPanel, { props: { api } });
    await flushPromises();
    const rows = w.findAll('[data-test=companion-client]');
    expect(rows).toHaveLength(2);
    expect(rows[0].text()).toContain('claude-code');
    expect(rows[0].text()).toContain('act');
    expect(rows[0].text()).toContain('dana');
    expect(rows[1].attributes('data-revoked')).toBe('true');
    expect(rows[1].find('[data-test=companion-revoke]').exists()).toBe(false);

    await rows[0].get('[data-test=companion-revoke]').trigger('click');
    await flushPromises();
    expect(calls.some(c => c.method === 'companion.revoke')).toBe(false);
    expect(w.get('[data-test=companion-revoke-confirm]').text()).toContain('claude-code');
    await w.get('[data-test=companion-revoke-no]').trigger('click');
    expect(w.find('[data-test=companion-revoke-confirm]').exists()).toBe(false);
    expect(calls.some(c => c.method === 'companion.revoke')).toBe(false);

    await w.findAll('[data-test=companion-client]')[0].get('[data-test=companion-revoke]').trigger('click');
    await w.get('[data-test=companion-revoke-yes]').trigger('click');
    await flushPromises();
    expect(calls.find(c => c.method === 'companion.revoke')?.params).toEqual({ name: 'claude-code' });
    expect(w.findAll('[data-test=companion-client]')[0].attributes('data-revoked')).toBe('true');
    w.unmount();
  });

  async function withClipboard(copied: string[], body: () => Promise<void>) {
    const before = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async (t: string) => { copied.push(t); } } });
    try { await body(); } finally {
      // The stub never outlives its test.
      if (before) Object.defineProperty(navigator, 'clipboard', before);
      else delete (navigator as unknown as Record<string, unknown>).clipboard;
    }
  }
  const panel = async (clients: any[], s = status()) => {
    const { api } = fakeApi({ 'companion.list': () => list({ clients }), 'companion.status': () => s });
    const w = mount(CompanionPanel, { props: { api } });
    await flushPromises();
    return w;
  };
  const sentence = (w: any) => w.get('[data-test=companion-status-text]').text();

  it('ShowsTheClaudeCodeCommand_CopyCopiesTheExactText', async () => {
    const copied: string[] = [];
    await withClipboard(copied, async () => {
      const s = status();
      const w = await panel([], s);
      expect(w.get('[data-test=companion-claude-command]').text()).toBe(s.claudeCommand);
      await w.get('[data-test=companion-copy]').trigger('click');
      await flushPromises();
      expect(copied).toEqual([s.claudeCommand]);
      expect(w.get('[data-test=companion-endpoint]').attributes('data-state')).toBe('on');
      w.unmount();
    });
  });

  it('PairCommandUsesTheQuotedBinaryPath_FollowsTheLevelPicker_CopyCopiesIt', async () => {
    const copied: string[] = [];
    await withClipboard(copied, async () => {
      const s = status();
      const w = await panel([], s);
      const cmd = () => w.get('[data-test=companion-pair-command]').text();
      const ps = (lvl: string) => `& '${s.binaryPath}' pair --name claude-code --level ${lvl}`;
      const cmdline = (lvl: string) => `"${s.binaryPath}" pair --name claude-code --level ${lvl}`;
      expect(cmd()).toBe(ps('act'));
      expect(w.get('[data-test=companion-pair-command-cmd]').text()).toBe(cmdline('act'));
      expect((w.get('[data-test=companion-level]').element as HTMLSelectElement).value).toBe('act');
      expect(w.get('[data-test=companion-level-text]').text()).toBe(COMPANION_LEVEL_TEXT.act);
      await w.get('[data-test=companion-level]').setValue('read');
      expect(cmd()).toBe(ps('read'));
      expect(w.get('[data-test=companion-level-text]').text()).toBe(COMPANION_LEVEL_TEXT.read);
      await w.get('[data-test=companion-level]').setValue('destructive');
      expect(cmd()).toContain('--level destructive');
      await w.get('[data-test=companion-copy-pair]').trigger('click');
      await w.get('[data-test=companion-copy-pair-cmd]').trigger('click');
      await flushPromises();
      expect(copied).toEqual([ps('destructive'), cmdline('destructive')]);
      expect(w.get('[data-test=companion-copy-pair-cmd]').text()).toBe('Copied');
      await w.get('[data-test=companion-level]').setValue('act');
      expect(w.get('[data-test=companion-copy-pair-cmd]').text()).toBe('Copy'); // the command changed
      const other = w.get('[data-test=companion-pair-other]');
      expect(other.text()).toContain("change my-tool to the program's name");
      expect(other.text()).not.toMatch(/[<>]/);
      expect(w.get('[data-test=companion-pair-other-ps]').text()).toBe(`& '${s.binaryPath}' pair --name my-tool --level act`);
      expect(w.get('[data-test=companion-pair-other-plain]').text()).toBe(`"${s.binaryPath}" pair --name my-tool --level act`);
      w.unmount();
    });
  });

  it('PairCommand_PowerShellDoublesASingleQuote_PosixPathsStayPlainQuoted', async () => {
    const w = await panel([], status({ binaryPath: "C:\\Users\\o'neil\\yawble.exe" }));
    expect(w.get('[data-test=companion-pair-command]').text()).toBe("& 'C:\\Users\\o''neil\\yawble.exe' pair --name claude-code --level act");
    w.unmount();
    const w2 = await panel([], status({ binaryPath: '/opt/yawble/yawble' }));
    expect(w2.get('[data-test=companion-pair-command]').text()).toBe('"/opt/yawble/yawble" pair --name claude-code --level act');
    expect(w2.find('[data-test=companion-pair-command-cmd]').exists()).toBe(false);
    w2.unmount();
  });

  it('NoBinaryNote_NotWhileLoading_NotAfterAStatusError', async () => {
    const { api } = fakeApi({ 'companion.list': () => list(), 'companion.status': () => { throw new Error('nope'); } });
    const w = mount(CompanionPanel, { props: { api } });
    expect(w.find('[data-test=companion-no-binary]').exists()).toBe(false);
    await flushPromises();
    expect(w.find('[data-test=companion-panel-error]').exists()).toBe(true);
    expect(w.find('[data-test=companion-no-binary]').exists()).toBe(false);
    w.unmount();
  });

  it('LastUsedIsTheMostRecentByDate_NotByText', async () => {
    const a = { ...client('a'), lastUsedAt: '2026-11-11T09:00:00+10:00' }; // earlier instant, later text
    const b = { ...client('b'), lastUsedAt: '2026-11-11T05:00:00Z' };
    const w = await panel([a, b]);
    expect(sentence(w)).toContain(`Last used ${new Date(b.lastUsedAt).toLocaleString()}`);
    w.unmount();
  });

  it('StatusSentence_OffWithProblem', async () => {
    const w = await panel([], status({ endpoint: null, problem: 'boom' }));
    expect(sentence(w)).toBe('Not available: boom');
    expect(w.text()).not.toMatch(/\bOn\b/);
    w.unmount();
    const w2 = await panel([], status({ endpoint: null, problem: null }));
    expect(sentence(w2)).toBe('Not available: the companion is turned off for this run');
    w2.unmount();
  });

  it('StatusSentence_OnWithNoClients_AndRevokedOnlyIsStillNotConnected', async () => {
    for (const clients of [[], [client('claude-code', '2026-11-11T10:00:00Z')]]) {
      const w = await panel(clients);
      expect(w.get('[data-test=companion-endpoint]').attributes('data-status')).toBe('ready');
      expect(sentence(w)).toBe('Ready — no program is connected yet. Follow the steps below to connect Claude Code.');
      w.unmount();
    }
  });

  it('StatusSentence_ConnectedListsActiveNamesOnly', async () => {
    const w = await panel([client('claude-code'), client('other'), client('old', '2026-11-01T10:00:00Z')]);
    expect(w.get('[data-test=companion-endpoint]').attributes('data-status')).toBe('connected');
    const t = sentence(w);
    expect(t).toContain('Connected — 2 programs can use Yawble: claude-code, other.');
    expect(t).toContain('Last used');
    expect(t).not.toContain('old');
    w.unmount();
  });

  it('PairStepTickedAndStepsCollapsedOnlyWhenAnActiveClaudeCodeExists', async () => {
    for (const clients of [[], [client('claude-code', '2026-11-11T10:00:00Z')], [client('other')]]) {
      const w = await panel(clients);
      expect(w.get('[data-test=companion-step-pair]').attributes('data-done')).toBe('false');
      expect(w.find('[data-test=companion-step-pair-done]').exists()).toBe(false);
      expect(w.find('[data-test=companion-steps-toggle]').exists()).toBe(false);
      w.unmount();
    }
    const w = await panel([client('claude-code')]);
    expect(w.find('[data-test=companion-steps]').exists()).toBe(false);
    await w.get('[data-test=companion-steps-toggle]').trigger('click');
    expect(w.get('[data-test=companion-step-pair]').attributes('data-done')).toBe('true');
    expect(w.get('[data-test=companion-step-pair-done]').text()).toContain('done');
    w.unmount();
  });

  it('ShowsTheProblemAndTheFolderWhenTheEndpointIsOff', async () => {
    const problem = "the folder 'D:\\keys' already holds other files, so Yawble will not change who can open it";
    const w = await panel([], status({ endpoint: null, controlFile: 'D:\\keys\\control.json', problem }));
    const endpoint = w.get('[data-test=companion-endpoint]');
    expect(endpoint.attributes('data-state')).toBe('off');
    expect(endpoint.text()).toContain(problem);
    expect(endpoint.text()).toContain('D:\\keys');
    w.unmount();
  });

  it('SaysWhenThisBuildShipsNoYawbleClient', async () => {
    const { api } = fakeApi({ 'companion.list': () => list(), 'companion.status': () => status({ binaryPath: null, claudeCommand: null }) });
    const w = mount(CompanionPanel, { props: { api } });
    await flushPromises();
    expect(w.find('[data-test=companion-claude-command]').exists()).toBe(false);
    expect(w.get('[data-test=companion-no-binary]').exists()).toBe(true);
    expect(w.find('[data-test=companion-steps]').exists()).toBe(false);
    expect(w.find('[data-test=companion-pair-command]').exists()).toBe(false);
    w.unmount();
  });

  it('RefreshesOnCompanionChanged', async () => {
    let n = 0;
    const { api, emit } = fakeApi({ 'companion.list': () => list({ clients: n++ === 0 ? [] : [client('claude-code')] }), 'companion.status': () => status() });
    const w = mount(CompanionPanel, { props: { api } });
    await flushPromises();
    expect(w.findAll('[data-test=companion-client]')).toHaveLength(0);
    emit('companion.changed');
    await flushPromises();
    expect(w.findAll('[data-test=companion-client]')).toHaveLength(1);
    w.unmount();
  });
});

describe('SettingsView companion tab', () => {
  it('HasACompanionTabOnTheComputer', async () => {
    const { api } = fakeApi({ 'companion.list': () => list(), 'companion.status': () => status() });
    const w = mount(SettingsView, { props: { api, tab: 'companion', teams: [] } });
    await flushPromises();
    expect(w.find('[data-test=open-companion]').exists()).toBe(true);
    expect(w.find('[data-test=settings-companion]').exists()).toBe(true);
    w.unmount();
  });

  it('NoCompanionTabInARemoteView', async () => {
    const { api, calls } = fakeApi({ 'companion.list': () => list(), 'companion.status': () => status() }, { remote: true });
    const w = mount(SettingsView, { props: { api, tab: 'companion', teams: [] } });
    await flushPromises();
    expect(w.find('[data-test=open-companion]').exists()).toBe(false);
    expect(w.find('[data-test=settings-companion]').exists()).toBe(false);
    expect(calls.some(c => c.method.startsWith('companion.'))).toBe(false);
    w.unmount();
  });
});

describe('companion levels', () => {
  it('APairingMayBeGrantedItsLevelOrLower_AnUnknownOneOnlyRead', () => {
    expect(levelsUpTo('read')).toEqual(['read']);
    expect(levelsUpTo('act')).toEqual(['read', 'act']);
    expect(levelsUpTo('destructive')).toEqual(['read', 'act', 'destructive']);
    expect(levelsUpTo('root' as never)).toEqual(['read']);
  });
});

describe('companion styles', () => {
  it('UseTheThemesColoursOnly', () => {
    for (const name of ['CompanionAsks.vue', 'CompanionPanel.vue']) {
      const source = readFileSync(path.join(process.cwd(), "src", "renderer", "src", "components", name), "utf8");
      const style = source.slice(source.indexOf('<style'));
      expect(style, name).not.toMatch(/#[0-9a-fA-F]{3,8}\b|rgba?\(/);
    }
  });
});

describe('companion methods', () => {
  it('TheWindowMayCallCompanionMethods', () => {
    for (const m of ['companion.list', 'companion.status', 'companion.decidePairing', 'companion.decideAction', 'companion.actionStatus', 'companion.revoke']) expect(isUiMethod(m)).toBe(true);
  });
});
