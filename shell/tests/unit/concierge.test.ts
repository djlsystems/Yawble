// @vitest-environment happy-dom
import { flushPromises, mount } from '@vue/test-utils';
import { beforeEach, describe, expect, it } from 'vitest';
import App from '../../src/renderer/src/App.vue';
import type { CoreEvent, Conversation, ConversationSummary, YawbleApi } from '../../src/shared/api';
import { renderMarkdown } from '../../src/renderer/src/markdown';
import ChatMessage from '../../src/renderer/src/components/ChatMessage.vue';

// The chat-first window: the Concierge is home, chats are listed in the sidebar, a turn's reply streams in.
function fakeCore() {
  const calls: { method: string; params: any }[] = [];
  const listeners = new Set<(e: CoreEvent) => void>();
  const conversations: Conversation[] = [];
  const summary = (c: Conversation): ConversationSummary => ({ ...c });
  const api: YawbleApi = {
    getInfo: async () => { throw new Error('no'); },
    getAvailableBackends: async () => [],
    call: async (method: string, params?: Record<string, unknown>) => {
      calls.push({ method, params });
      switch (method) {
        case 'app.overview': return { slots: { running: 0, limit: 4, queued: 0 }, teams: [{ id: 1, name: 'payments', repoPath: null, members: 2, running: 0, queued: 0 }] } as any;
        case 'link.status': return { state: 'off', viewers: [] } as any;
        case 'agents.detect': return [{ cliId: 'claude', found: true, signIn: { status: 'signed-in', detail: null } }, { cliId: 'grok', found: true, signIn: { status: 'not-signed-in', detail: null } }] as any;
        case 'concierge.list': return conversations.map(summary) as any;
        case 'concierge.get': return JSON.parse(JSON.stringify(conversations.find((c) => c.id === params!.id))) as any; // a fresh copy, as over IPC
        case 'concierge.send': {
          const c: Conversation = {
            id: 'c1', title: String(params!.text), cli: String(params!.cli), teamId: (params!.teamId as number) ?? null, team: 'payments',
            createdAt: '', updatedAt: '', running: true, unread: 0, watching: 0, watches: [],
            messages: [{ id: 1, role: 'user', text: String(params!.text), at: '', state: null, error: null, tools: [], cli: null }],
            turn: { conversationId: 'c1', state: 'running', text: '', tools: [], error: null },
          };
          conversations.push(c);
          return { id: 'c1' } as any;
        }
        default: throw new Error(`unexpected ${method}`);
      }
    },
    onEvent: (l) => { listeners.add(l); return () => listeners.delete(l); },
    openTerminal: async () => {},
    pickFolder: async () => null,
  };
  const push = (event: string, data: unknown) => { for (const l of listeners) l({ event, data }); };
  return { api, calls, conversations, push };
}

describe('the Concierge', () => {
  beforeEach(() => { try { localStorage.clear(); } catch { /* none */ } });

  it('starts at a new chat, sends with the agent and the team in focus, streams the reply and lists the chat', async () => {
    const core = fakeCore();
    const w = mount(App, { props: { api: core.api, pollMs: 60_000 } });
    await flushPromises();
    await flushPromises();
    expect(w.find('[data-test=concierge-hero]').exists()).toBe(true);
    // Only the signed-in agent can be picked; it is the default.
    const agent = w.get('[data-test=composer-agent]').element as HTMLSelectElement;
    expect(agent.value).toBe('claude');
    expect([...agent.options].find((o) => o.value === 'grok')!.disabled).toBe(true);

    await w.get('[data-test=composer-team]').setValue('1');
    await w.get('[data-test=composer-text]').setValue('What is payments doing?');
    await w.get('[data-test=composer-text]').trigger('keydown', { key: 'Enter' });
    await flushPromises();
    await flushPromises();
    const send = core.calls.find((c) => c.method === 'concierge.send')!;
    expect(send.params).toMatchObject({ id: null, text: 'What is payments doing?', cli: 'claude', teamId: 1 });
    expect(w.find('[data-test=concierge-hero]').exists()).toBe(false);
    expect(w.findAll('[data-test=chat-message]')[0].text()).toContain('What is payments doing?');
    expect(w.findAll('[data-test=chat-item]')).toHaveLength(1);

    // The reply streams in, then the turn ends and the kept answer replaces it.
    core.push('concierge.turn', { conversationId: 'c1', state: 'running', text: 'Looking **now**', tools: [{ name: 'team_status', detail: 'team: payments' }], error: null });
    await flushPromises();
    expect(w.get('[data-test=composer-stop]').exists()).toBe(true);
    expect(w.html()).toContain('<strong>now</strong>');
    expect(w.text()).toContain('Checked a team');

    const c = core.conversations[0];
    c.messages.push({ id: 2, role: 'assistant', text: 'All quiet.', at: '', state: 'done', error: null, tools: [], cli: 'claude' });
    c.turn = null;
    c.running = false;
    core.push('concierge.turn', { conversationId: 'c1', state: 'done', text: 'All quiet.', tools: [], error: null });
    for (let i = 0; i < 20 && w.findAll('[data-test=chat-answer]').at(-1)?.text() !== 'All quiet.'; i++) await flushPromises();
    expect(w.findAll('[data-test=chat-answer]').at(-1)?.text()).toBe('All quiet.');
    expect(w.find('[data-test=composer-send]').exists()).toBe(true);
  });

  it('asks the Concierge about the team in view', async () => {
    const core = fakeCore();
    const w = mount(App, { props: { api: { ...core.api, call: async (m: string, p?: any) => (m === 'teams.agents' || m === 'bus.decisions' ? [] : core.api.call(m, p)) } as YawbleApi, pollMs: 60_000 } });
    await flushPromises();
    await w.get('[data-test=team-item]').trigger('click');
    await flushPromises();
    await w.get('[data-test=ask-concierge]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=concierge-hero]').exists()).toBe(true);
    expect((w.get('[data-test=composer-team]').element as HTMLSelectElement).value).toBe('1');
  });
});

describe('the Teams place', () => {
  it('always opens the list of teams, not the team last looked at', async () => {
    const core = fakeCore();
    const w = mount(App, { props: { api: { ...core.api, call: async (m: string, p?: any) => (m === 'teams.agents' || m === 'bus.decisions' ? [] : core.api.call(m, p)) } as YawbleApi, pollMs: 60_000 } });
    await flushPromises();
    await w.get('[data-test=team-item]').trigger('click');
    await flushPromises();
    expect(document.title).toBe('payments · Yawble'); // the window's title follows the place
    await w.get('[data-test=nav-teams]').trigger('click');
    await flushPromises();
    expect(document.title).toBe('Teams · Yawble');
    expect(w.find('[data-test=team-grid]').exists()).toBe(true);
  });
});

describe('the Cloud Link indicator', () => {
  it('sits in the top bar, shows the link state and who is viewing, and opens the Cloud Link', async () => {
    const core = fakeCore();
    const w = mount(App, { props: { api: { ...core.api, platform: 'win32' } as YawbleApi, pollMs: 60_000 }, attachTo: document.body });
    await flushPromises();
    expect(w.get('.app').classes()).toContain('own-bar');
    const ind = w.get('[data-test=cloud-link-indicator]');
    expect(ind.classes()).toContain('off');
    expect(ind.get('svg').attributes('fill')).toBe('none');
    expect(ind.attributes('title')).toContain('Cloud Link: off');

    core.push('link.state', { state: 'connected', url: 'https://manage.example.com', viewers: ['ana@example.com'] });
    await flushPromises();
    expect(w.get('[data-test=cloud-link-indicator]').classes()).toContain('connected');
    expect(w.get('[data-test=cloud-link-indicator] svg').attributes('fill')).toBe('currentColor'); // a green filled cloud
    expect(w.get('[data-test=cloud-link-viewers]').text()).toBe('1');
    expect(w.get('[data-test=cloud-link-indicator]').attributes('title')).toContain('viewing now: ana@example.com');

    await w.get('[data-test=cloud-link-indicator]').trigger('click');
    await flushPromises();
    expect(document.body.textContent).toContain('Cloud site address');
    w.unmount();
  });

  it('is not shown in a browser view', async () => {
    const core = fakeCore();
    const w = mount(App, { props: { api: { ...core.api, remote: true } as YawbleApi, pollMs: 60_000 } });
    await flushPromises();
    expect(w.find('[data-test=cloud-link-indicator]').exists()).toBe(false);
    expect(w.get('.app').classes()).not.toContain('own-bar');
  });
});

describe('keeping tabs', () => {
  it('shows what the chat watches, posts updates as they arrive, and lets the person stop a watch or ask for reviews', async () => {
    const core = fakeCore();
    const watch = { id: 7, team: 'payments', workflow: 12, what: 'Add a README', followUp: false, createdAt: '', expiresAt: new Date().toISOString() };
    core.conversations.push({
      id: 'c9', title: 'README', cli: 'claude', teamId: null, team: null, createdAt: '', updatedAt: '', running: false, unread: 2, watching: 1,
      messages: [{ id: 1, role: 'user', text: 'Get payments a README', at: '', state: null, error: null, tools: [], cli: null }],
      turn: null, watches: [watch],
    });
    const calls: string[] = [];
    const call = core.api.call;
    const api: YawbleApi = {
      ...core.api,
      call: async (m: string, p?: any) => {
        calls.push(m);
        if (m === 'concierge.read') { core.conversations[0].unread = 0; return { unread: 0 } as any; }
        if (m === 'concierge.setWatch') return { ...watch, followUp: p.followUp } as any;
        if (m === 'concierge.unwatch') { core.conversations[0].watches = []; core.conversations[0].watching = 0; return { stopped: true } as any; }
        return call(m, p);
      },
    };
    const w = mount(App, { props: { api, pollMs: 60_000 } });
    await flushPromises();
    expect(w.get('[data-test=chat-unread]').text()).toBe('2');
    await w.get('[data-test=chat-item] button.open').trigger('click');
    await flushPromises();
    await flushPromises();
    expect(calls).toContain('concierge.read');                       // opening the chat counts its updates as seen
    expect(w.get('[data-test=watch]').text()).toContain('payments');
    expect(w.get('[data-test=watch]').text()).toContain('Add a README');

    core.push('concierge.update', {
      conversationId: 'c9', unread: 1,
      message: { id: 2, role: 'update', text: '**payments** · dev needs your decision (question 14): MIT or Apache?', at: new Date().toISOString(), state: null, error: null, tools: [], cli: null, team: 'payments', kind: 'decision' },
    });
    await flushPromises();
    const update = w.get('[data-test=chat-update]');
    expect(update.attributes('data-kind')).toBe('decision');
    expect(update.text()).toContain('Needs you');
    expect(update.html()).toContain('<strong>payments</strong>');

    await w.get('[data-test=watch-review]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=watch-review]').attributes('aria-pressed')).toBe('true');
    await w.get('[data-test=watch-stop]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=watches]').exists()).toBe(false);
  });
});

describe('agent-written markdown', () => {
  it('is rendered without raw HTML or script links', () => {
    const html = renderMarkdown('**ok** <img src=x onerror=alert(1)> <script>alert(1)</script> [x](javascript:alert(1)) [y](https://example.com)');
    expect(html).toContain('<strong>ok</strong>');
    expect(html).not.toContain('<img');
    expect(html).not.toContain('<script');
    expect(html).not.toContain('href="javascript:');
    expect(html).toContain('href="https://example.com" target="_blank" rel="noopener noreferrer"');
  });

  it('turns web addresses into links but leaves file names and code alone', () => {
    const html = renderMarkdown('See https://github.com/microsoft/mxc/issues/1706, www.example.org and docs.github.com. Edit README.md and run.py. `https://in.code.com`');
    expect(html).toContain('<a href="https://github.com/microsoft/mxc/issues/1706" target="_blank"');
    expect(html).toContain('<a href="http://www.example.org" target="_blank"');
    expect(html).toContain('<a href="http://docs.github.com" target="_blank"');
    expect(html).not.toContain('href="http://README.md"');
    expect(html).not.toContain('href="http://run.py"');
    expect(html).toContain('<code>https://in.code.com</code>');
  });

  it('gives a code block a copy button that copies its text', async () => {
    const copied: string[] = [];
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async (t: string) => { copied.push(t); } } });
    const w = mount(ChatMessage, { props: { role: 'assistant', text: 'Run:\n\n```powershell\ndotnet build -c Release\n```' }, attachTo: document.body });
    expect(w.find('.lang').text()).toBe('powershell');
    await w.get('[data-test=copy-code]').trigger('click');
    await flushPromises();
    expect(copied).toEqual(['dotnet build -c Release']);
    expect(w.get('[data-test=copy-code]').text()).toContain('Copied');
    w.unmount();
  });
});
