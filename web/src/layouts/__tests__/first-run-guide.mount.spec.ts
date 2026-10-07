// @vitest-environment happy-dom
//
// THE FIRST-RUN GUIDE, THROUGH THE REAL SHELL. MainLayout is mounted because the guide's whole
// contract lives there: it opens itself on a first visit, it reads the platform's own answer about
// sign-in, and the account menu is how a person gets it back. A spec of the guide alone would test
// a card that nothing opens.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { concierge, listCatalog, getAgentAuth } = vi.hoisted(() => ({
  concierge: vi.fn(),
  listCatalog: vi.fn(),
  getAgentAuth: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  concierge,
  listCatalog,
  getAgentAuth,
  // The app bar's activity monitor reads its route on mount; nothing here is about its figures.
  getCapacity: vi.fn(() => new Promise(() => {})),
}));

vi.mock('../../lib/concierge-socket', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  connectConcierge: vi.fn(() => ({ send: vi.fn(), resize: vi.fn(), dispose: vi.fn() })),
}));

import { flushPromises, mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { createMemoryHistory, createRouter } from 'vue-router';
import MainLayout from '../MainLayout.vue';
import { useSessionStore } from '../../stores/session';
import type { AgentAuthReport } from '../../api/types';
import { resetBody } from '../../test/mountQuasar';

const stubs = {
  ProfileDialog: true,
  BoardDisplayDialog: true,
  CreateTeamDialog: true,
  TeamSettingsDialog: true,
  TeamGitDialog: true,
  DocumentsDialog: true,
  UsersDialog: true,
  AgentsDialog: true,
  ResetTeamDialog: true,
  TenantLogDialog: true,
  ApiKeysDialog: true,
  SkillsDialog: true,
  ConciergePanel: true,
};

function report(over: Partial<AgentAuthReport>): AgentAuthReport {
  return {
    agent: 'claude-interactive',
    command: 'claude',
    installed: true,
    authenticated: false,
    detail: 'asked the agent and it said not logged in',
    referenced: true,
    ...over,
  };
}

async function mountShell() {
  setActivePinia(createPinia());
  useSessionStore().$patch({ user: { id: 'u1', email: 'sam@example.com' } });

  const router = createRouter({
    history: createMemoryHistory(),
    routes: [{ path: '/', component: MainLayout, children: [{ path: '', component: { template: '<div />' } }] }],
  });

  await router.push('/');

  const wrapper = mount({ template: '<router-view />' }, { attachTo: document.body, global: { plugins: [router], stubs } });

  await flushPromises();
  await flushPromises();

  return wrapper;
}

const guide = () => document.body.querySelector<HTMLElement>('[data-test="first-run-guide"]');
const guideText = () => guide()?.textContent?.replace(/\s+/g, ' ') ?? '';

async function reopenFromAccountMenu(wrapper: Awaited<ReturnType<typeof mountShell>>) {
  await wrapper.find('.user-menu').trigger('click');
  await flushPromises();

  const item = document.body.querySelector<HTMLElement>('[data-test="account-first-run-guide"]');
  expect(item, 'no Getting started item in the account menu').not.toBeNull();
  expect(item!.textContent).toContain('Getting started');

  item!.click();
  await flushPromises();
  await flushPromises();
}

beforeEach(() => {
  localStorage.clear();
  concierge.mockReset();
  concierge.mockResolvedValue({ agent: 'claude-interactive', prompt: null, promptName: null });
  listCatalog.mockReset();
  listCatalog.mockResolvedValue([]);
  getAgentAuth.mockReset();
});

afterEach(() => {
  resetBody();
});

describe('the first-run guide', () => {
  it('shows on a first visit when no agent is signed in, with the steps to sign one in', async () => {
    getAgentAuth.mockResolvedValue([report({ authenticated: false }), report({ agent: 'codex', command: 'codex', installed: false, authenticated: null })]);

    const wrapper = await mountShell();

    expect(getAgentAuth).toHaveBeenCalled();
    expect(guide(), 'the guide did not open on a first visit').not.toBeNull();

    const text = guideText();
    expect(text).toContain('No agent is signed in yet');
    expect(text).toContain('The Concierge');
    expect(text).toContain('Choose its agent');
    expect(text).toContain('Sign in there');
    expect(text).toContain('ask the Concierge for a team');
    expect(text).toContain('New Team');
    // Plain words: no internal terms, and no setting keys.
    expect(text.toLowerCase()).not.toContain('container');
    expect(text).not.toMatch(/\b[a-z]+\.[a-z][A-Za-z]+\b/);

    wrapper.unmount();
  });

  it('does not show once the platform says an agent is signed in', async () => {
    getAgentAuth.mockResolvedValue([report({ authenticated: true }), report({ agent: 'codex', command: 'codex', authenticated: false })]);

    const wrapper = await mountShell();

    expect(getAgentAuth, 'the shell never asked the platform whether an agent is signed in').toHaveBeenCalled();
    expect(guide(), 'the guide opened although an agent is signed in').toBeNull();

    // Asked for from the account menu, it says what the platform said.
    await reopenFromAccountMenu(wrapper);
    expect(guideText()).toContain('An agent is signed in');

    wrapper.unmount();
  });

  it('stays dismissed across a reload and opens again from the account menu', async () => {
    getAgentAuth.mockResolvedValue([report({ authenticated: false })]);

    const first = await mountShell();
    expect(guide(), 'the guide did not open on a first visit').not.toBeNull();

    document.body.querySelector<HTMLElement>('[data-test="first-run-guide-dismiss"]')!.click();
    await flushPromises();
    expect(guide(), 'dismissing did not close the guide').toBeNull();

    first.unmount();
    resetBody();

    // The reload: a fresh shell over the same browser storage, and still no agent signed in.
    const reloaded = await mountShell();
    expect(guide(), 'the dismissed guide came back on reload').toBeNull();

    await reopenFromAccountMenu(reloaded);
    expect(guide(), 'the account menu did not open the guide').not.toBeNull();
    expect(guideText()).toContain('No agent is signed in yet');

    reloaded.unmount();
  });

  it('says not measured when the platform does not answer, never signed in or signed out', async () => {
    getAgentAuth.mockRejectedValue(new Error('503'));

    const wrapper = await mountShell();

    expect(guide(), 'the guide did not open when the answer was unavailable').not.toBeNull();

    const text = guideText();
    expect(text).toContain('Not measured');
    expect(text).not.toContain('No agent is signed in');
    expect(text).not.toContain('An agent is signed in');

    wrapper.unmount();
  });

  it('says not measured when the platform could not ask an agent', async () => {
    getAgentAuth.mockResolvedValue([
      report({ authenticated: false }),
      report({ agent: 'codex', command: 'codex', installed: null, authenticated: null, detail: 'no answer' }),
    ]);

    const wrapper = await mountShell();

    const text = guideText();
    expect(text).toContain('Not measured');
    expect(text).not.toContain('No agent is signed in');
    expect(text).not.toContain('An agent is signed in');

    wrapper.unmount();
  });
});
