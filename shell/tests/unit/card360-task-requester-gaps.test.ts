// @vitest-environment happy-dom
// Card 360 (verify B066, cards 358 + 359): how a host task request from the Concierge or a companion client shows to the person.
// Card 358 records the requester as "_concierge/<conversationId>" (a 32-hex Guid, ConciergeService) or "_companion/<client>"
// (ConciergeHostTasks.Requester). The request card on the team's page (TaskAskCard), the task row's "asked by" and the
// run records (TasksPanel) must say who asked in words, never the raw conversation id or the "_concierge/" key.
// Also: the core refuses a description or check-after over 1000 characters (HostTaskRules.MaxTextLength); the panel says so.
import { flushPromises, mount } from '@vue/test-utils';
import { describe, expect, it } from 'vitest';
import TaskAskCard from '../../src/renderer/src/components/TaskAskCard.vue';
import TasksPanel from '../../src/renderer/src/components/TasksPanel.vue';
import type { HostTaskDef, HostTaskInfo, IntegrationAsk, TaskRun, YawbleApi } from '../../src/shared/api';

const conversation = '3f2a9c0d6b1e4f7a8c5d2e1b0a9f8e7d';
const concierge = `_concierge/${conversation}`;
const companion = '_companion/claude-code';

const ask = (member: string): IntegrationAsk => ({
  id: 41, at: '2026-11-17T16:00:00Z', integration: 'task', teamId: 3, member, command: 'pwsh -File scripts/deploy.ps1', reason: 'the person asked to deploy',
  workflow: 0, task: true, taskName: 'deploy', commit: 'abcdef1234567890', detail: null,
});

function fakeApi(answers: Record<string, (params: any) => unknown>) {
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      const answer = answers[method];
      if (!answer) throw new Error(`unexpected ${method}`);
      return answer(params) as any;
    },
    onEvent: () => () => {},
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  return api;
}

const teams = [{ id: 3, name: 'ops', repoPath: null, members: 2, running: 0, queued: 0 }];
const def: HostTaskDef = { name: 'deploy', repo: 'main', program: 'pwsh', args: ['-File', 'scripts/deploy.ps1'], workdir: '.', timeoutSeconds: 1800, envNames: [], watch: [], pinMode: 'files', commandLineOnly: false };
const run = (id: number, requestedBy: string | null): TaskRun => ({
  id, task: 'deploy', teamId: 3, state: 'done', startedAt: '2026-11-17T16:00:00Z', endedAt: '2026-11-17T16:01:00Z', commit: 'abcdef1234567890', headAfter: null,
  definitionHash: null, filesHash: null, program: 'C:\\pwsh.exe', exitCode: 0, timedOut: false, killedBy: null, outputBytes: 10, truncated: false, logPath: null,
  dirtyAfter: false, askId: 41, requestedBy, missingEnv: null,
});
const info = (lastRun: TaskRun): HostTaskInfo => ({
  id: 1, def, commandLine: 'pwsh -File scripts/deploy.ps1', approval: 'asks', approvedAt: null, approvedCommit: null, approvedBy: null,
  programResolved: 'C:\\pwsh.exe', commit: 'abcdef1234567890', pinned: ['scripts/deploy.ps1'], warnings: [], problem: null, runningRunId: null, pendingAskId: null,
  lastRun, updatedAt: '2026-11-17T10:00:00Z', updatedBy: 'me',
} as HostTaskInfo);

describe('card 360: who asked for a host task, in words', () => {
  it('a Concierge request card names the Concierge, not the conversation id', () => {
    const text = mount(TaskAskCard, { props: { ask: ask(concierge) } }).text();
    expect(text).toContain('Concierge');
    expect(text).not.toContain(conversation);
    expect(text).not.toContain('_concierge');
  });

  it('a companion request card names the client without the "_companion/" key', () => {
    const text = mount(TaskAskCard, { props: { ask: ask(companion) } }).text();
    expect(text).toContain('claude-code');
    expect(text).not.toContain('_companion');
  });

  it("an agent's request card still names the agent", () => {
    expect(mount(TaskAskCard, { props: { ask: ask('ops/DeveloperAda') } }).text()).toContain('DeveloperAda asks to run the host task');
  });

  it("the task row's \"asked by\" and the run records do not show the raw requester keys", async () => {
    const api = fakeApi({
      'tasks.list': () => ({ tasks: [info(run(7, concierge))], repos: ['main'] }),
      'tasks.runs': () => [run(7, concierge), run(8, companion)],
      'integrations.connections': () => [],
    });
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    const text = w.text();
    expect(w.find('[data-test=task-deploy]').text()).toContain('Concierge');
    expect(w.find('[data-test=task-run-7]').text()).toContain('Concierge');
    expect(w.find('[data-test=task-run-8]').text()).toContain('claude-code');
    expect(text).not.toContain(conversation);
    expect(text).not.toContain('_concierge');
    expect(text).not.toContain('_companion');
  });
});

describe('card 360: a description over the core limit', () => {
  it("the core's refusal of a 1001-character description is shown and nothing is saved", async () => {
    let saves = 0;
    const api = fakeApi({
      'tasks.list': () => ({ tasks: [], repos: ['main'] }),
      'tasks.runs': () => [],
      'tasks.preview': () => ({ problem: null, programResolved: 'C:\\pwsh.exe', commit: 'abcdef1234567890', pinned: [], warnings: [], commandLineOnly: false }),
      'tasks.save': () => { saves++; throw new Error('the description is at most 1000 characters'); },
      'integrations.connections': () => [],
    });
    const w = mount(TasksPanel, { props: { api, teams } });
    await flushPromises();
    await w.find('[data-test=task-add]').trigger('click');
    await flushPromises();
    await w.find('[data-test=task-name]').setValue('release');
    await w.find('[data-test=task-program]').setValue('pwsh');
    await w.find('[data-test=task-description]').setValue('x'.repeat(1001));
    await w.find('[data-test=task-save]').trigger('click');
    await flushPromises();
    expect(saves).toBe(1);
    expect(w.text()).toContain('at most 1000 characters');
    expect(w.find('[data-test=task-description]').exists()).toBe(true); // the form stays open with what was typed
  });
});
