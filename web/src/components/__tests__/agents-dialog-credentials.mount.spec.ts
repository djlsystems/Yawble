// @vitest-environment happy-dom
//
// ISSUED CREDENTIALS ON THE AGENTS SCREEN. Two scopes on one row: the SOURCE (shared home or issued)
// is the preset's own and is written to the tenant setting `agents.credentialSource`; the CREDENTIAL
// is its command's, one value shared by every preset that runs that command, so setting or clearing
// it from one row changes every row of that command. The field is write-only, as Connections' client
// secret is: never filled, empty keeps, clear is a checkbox, emptied once saved, and no value is ever
// shown - only who set it and when.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const {
  listCatalog,
  getAgentAuth,
  getAgentTools,
  getTenantSettings,
  saveTenantSettings,
  getAgentCredentials,
  setAgentCredential,
  clearAgentCredential,
} = vi.hoisted(() => ({
  listCatalog: vi.fn(),
  getAgentAuth: vi.fn(),
  getAgentTools: vi.fn(),
  getTenantSettings: vi.fn(),
  saveTenantSettings: vi.fn(),
  getAgentCredentials: vi.fn(),
  setAgentCredential: vi.fn(),
  clearAgentCredential: vi.fn(),
}));

vi.mock('../../api/client', async (importOriginal) => ({
  ...(await importOriginal<Record<string, unknown>>()),
  listAgentUpdates: async () => [],
  listCatalog,
  saveCatalog: vi.fn(),
  getAgentAuth,
  getAgentTools,
  getTenantSettings,
  saveTenantSettings,
  getAgentCredentials,
  setAgentCredential,
  clearAgentCredential,
}));

import AgentsDialog from '../AgentsDialog.vue';
import type { Agent, AgentCredential, IssuedCredentialDeclaration } from '../../api/types';
import { bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, field, settle, type } from '../../test/formProbe';
import { ProviderTermsSentence } from '../../lib/agentCredentials';

const Secret = 'sk-ant-api03-not-a-real-value-0123456789';

const claudeDeclaration: IssuedCredentialDeclaration = {
  kinds: [
    { kind: 'apiKey', variable: 'ANTHROPIC_API_KEY' },
    { kind: 'token', variable: 'CLAUDE_CODE_OAUTH_TOKEN' },
  ],
  displaces: ['ANTHROPIC_API_KEY', 'CLAUDE_CODE_OAUTH_TOKEN', 'ANTHROPIC_AUTH_TOKEN'],
  loginPrecedence: 'credential',
  measuredWith: '2.1.287',
  homeVariables: ['CLAUDE_CONFIG_DIR'],
};

const grokDeclaration: IssuedCredentialDeclaration = {
  kinds: [{ kind: 'apiKey', variable: 'XAI_API_KEY' }],
  displaces: ['XAI_API_KEY', 'GROK_CODE_XAI_API_KEY'],
  loginPrecedence: 'login',
  measuredWith: '1.0.0',
  homeVariables: ['GROK_HOME'],
};

// Copilot's token kind refuses a classic personal access token: the Host answers 400 for one.
const copilotDeclaration: IssuedCredentialDeclaration = {
  kinds: [{ kind: 'token', variable: 'COPILOT_GITHUB_TOKEN', refusedPrefixes: ['ghp_'] }],
  displaces: ['COPILOT_GITHUB_TOKEN', 'GH_TOKEN', 'GITHUB_TOKEN'],
  loginPrecedence: 'credential',
  measuredWith: '0.0.400',
  homeVariables: ['COPILOT_HOME'],
};

const CopilotRefusal = 'This CLI refuses to start with a credential beginning `ghp_` in COPILOT_GITHUB_TOKEN, so it is not stored.';

const preset = (name: string, fileName: string, mode: 'Headless' | 'Interactive', builtIn = true): Agent => ({
  name,
  mode,
  builtIn,
  launch: { fileName, arguments: [] },
  tags: [],
  tagsFromOperator: false,
  buildTags: [],
});

const agents: Agent[] = [
  preset('claude', 'claude', 'Interactive'),
  preset('claude-headless', 'claude', 'Headless'),
  preset('grok', 'grok', 'Interactive'),
  preset('copilot-headless', 'copilot', 'Headless'),
  preset('my-model', 'my-cli', 'Headless', false),
  // Hidden: the Host lists only the visible presets, so this one has no credentials entry.
  preset('hidden-model', 'claude', 'Headless'),
];

const entry = (agent: string, command: string, sharedWith: string[], declaration: IssuedCredentialDeclaration | null,
  over: Partial<AgentCredential> = {}): AgentCredential => ({
  agent,
  command,
  sharedWith,
  source: 'home',
  issuedCredential: declaration,
  set: false,
  setBy: null,
  setAt: null,
  ...over,
});

function credentials(over: Partial<AgentCredential> = {}): AgentCredential[] {
  return [
    entry('claude', 'claude', ['claude-headless'], claudeDeclaration, over),
    entry('claude-headless', 'claude', ['claude'], claudeDeclaration, over),
    entry('grok', 'grok', [], grokDeclaration),
    entry('copilot-headless', 'copilot', [], copilotDeclaration),
    entry('my-model', 'my-cli', [], null),
  ];
}

const settingsWith = (value: unknown) => ({
  settings: [{
    name: 'agents.credentialSource',
    value,
    default: {},
    source: 'row',
    updatedAt: null,
    updatedBy: null,
    description: '',
  }],
  roots: [],
});

beforeEach(() => {
  listCatalog.mockReset();
  listCatalog.mockResolvedValue({ agents });
  getAgentAuth.mockReset();
  getAgentAuth.mockResolvedValue([]);
  getAgentTools.mockReset();
  getAgentTools.mockResolvedValue({ at: null, running: false, presets: [] });
  getTenantSettings.mockReset();
  // `gone` names no preset: it is ignored where it is read, and a save here must keep it.
  getTenantSettings.mockResolvedValue(settingsWith({ grok: 'issued', gone: 'issued' }));
  saveTenantSettings.mockReset();
  saveTenantSettings.mockResolvedValue(undefined);
  getAgentCredentials.mockReset();
  getAgentCredentials.mockResolvedValue(credentials());
  setAgentCredential.mockReset();
  setAgentCredential.mockResolvedValue({ command: 'claude', set: true, setBy: 'person@example.com', setAt: '2026-10-02T10:00:00Z' });
  clearAgentCredential.mockReset();
  clearAgentCredential.mockResolvedValue({ command: 'claude', set: false, setBy: null, setAt: null });
});

afterEach(resetBody);

function block(name: string): HTMLElement {
  const found = document.body.querySelector<HTMLElement>(`[data-agent-credential="${name}"]`);
  if (!found) throw new Error(`no credential block for ${name}`);
  return found;
}

function rowButton(name: string, label: string): HTMLButtonElement {
  const found = document.body.querySelector<HTMLButtonElement>(`button[aria-label="${label} ${name}"]`);
  if (!found) throw new Error(`no ${label} button for ${name}`);
  return found;
}

function sourceButton(name: string, label: string): HTMLButtonElement {
  const found = [...block(name).querySelectorAll<HTMLButtonElement>('[data-credential-source] button')]
    .find((candidate) => candidate.textContent?.trim() === label);
  if (!found) throw new Error(`no ${label} source for ${name}`);
  return found;
}

const stateOf = (name: string) => block(name).querySelector('[data-credential-state]')?.textContent?.replace(/\s+/g, ' ').trim() ?? '';

/** Every value a person could see or a field still holds. */
function everythingShown(): string {
  const values = [...document.body.querySelectorAll('input')].map((input) => input.value);
  return [bodyText(), ...values].join('\n');
}

async function openEditor(name: string) {
  rowButton(name, 'Credential').click();
  await settle();
}

describe('AgentsDialog credentials, mounted', () => {
  it('sets a credential and empties the field after saving', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    await openEditor('claude-headless');
    expect(field('API key').value).toBe('');
    await type('API key', Secret);
    button('Save').click();
    await settle();

    expect(setAgentCredential).toHaveBeenCalledTimes(1);
    expect(setAgentCredential.mock.calls[0]).toEqual(['claude-headless', { kind: 'apiKey', value: Secret }]);
    expect(document.body.querySelector('[data-credential-editor]')).toBeNull();
    expect(stateOf('claude-headless')).toContain('set by person@example.com');

    // Opened again, the field is empty: nothing kept it.
    await openEditor('claude-headless');
    expect(field('API key').value).toBe('');
    expect(document.body.querySelector('[data-credential-set]')).not.toBeNull();

    wrapper.unmount();
  });

  it('sends the token kind when it is chosen', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    await openEditor('claude');
    const token = [...document.body.querySelectorAll<HTMLElement>('[data-credential-editor] .q-radio')]
      .find((radio) => radio.textContent?.includes('CLAUDE_CODE_OAUTH_TOKEN'))!;
    token.click();
    await settle();
    await type('Token', 'oauth-not-a-real-token');
    button('Save').click();
    await settle();

    expect(setAgentCredential.mock.calls[0]).toEqual(['claude', { kind: 'token', value: 'oauth-not-a-real-token' }]);

    wrapper.unmount();
  });

  it('replaces and clears a credential', async () => {
    getAgentCredentials.mockResolvedValue(credentials({ set: true, setBy: 'first@example.com', setAt: '2026-10-01T09:00:00Z' }));
    setAgentCredential.mockResolvedValue({ command: 'claude', set: true, setBy: 'second@example.com', setAt: '2026-10-02T10:00:00Z' });
    const wrapper = await mountDialog(AgentsDialog);

    // Empty keeps: a save with nothing typed sends nothing.
    await openEditor('claude-headless');
    button('Save').click();
    await settle();
    expect(setAgentCredential).not.toHaveBeenCalled();
    expect(clearAgentCredential).not.toHaveBeenCalled();

    // Typed, it replaces.
    await openEditor('claude-headless');
    await type('API key', Secret);
    button('Save').click();
    await settle();
    expect(setAgentCredential).toHaveBeenCalledTimes(1);
    expect(stateOf('claude-headless')).toContain('set by second@example.com');

    // The checkbox clears it.
    await openEditor('claude-headless');
    const clear = [...document.body.querySelectorAll<HTMLElement>('[data-credential-editor] .q-checkbox')]
      .find((box) => box.textContent?.includes('Clear it'))!;
    clear.click();
    await settle();
    button('Save').click();
    await settle();
    expect(clearAgentCredential.mock.calls[0]).toEqual(['claude-headless']);
    expect(stateOf('claude-headless')).toContain('not set');

    wrapper.unmount();
  });

  it('keeps the editor open with the server sentence when a save is refused', async () => {
    setAgentCredential.mockRejectedValue(new Error('the credential holds a line break'));
    const wrapper = await mountDialog(AgentsDialog);

    await openEditor('claude-headless');
    await type('API key', Secret);
    button('Save').click();
    await settle();

    expect(document.body.querySelector('[data-credential-problem]')?.textContent).toContain('line break');
    expect(bodyText()).not.toContain(Secret);

    wrapper.unmount();
  });

  it('shows the Host\'s 400 for a value the CLI refuses, and stores nothing', async () => {
    setAgentCredential.mockRejectedValue(new Error(CopilotRefusal));
    const wrapper = await mountDialog(AgentsDialog);

    await openEditor('copilot-headless');
    await type('Token', 'ghp_not-a-real-token');
    button('Save').click();
    await settle();

    expect(setAgentCredential.mock.calls[0]).toEqual(['copilot-headless', { kind: 'token', value: 'ghp_not-a-real-token' }]);
    expect(document.body.querySelector('[data-credential-problem]')?.textContent).toContain(CopilotRefusal);
    expect(document.body.querySelector('[data-credential-editor]')).not.toBeNull();
    expect(stateOf('copilot-headless')).toContain('not set');

    wrapper.unmount();
  });

  it('shows no credential block for a preset the credentials list leaves out', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(document.body.querySelector('[data-agent-credential="hidden-model"]')).toBeNull();
    expect(document.body.querySelector('button[aria-label="Credential hidden-model"]')).toBeNull();
    expect(stateOf('claude')).toContain('not set');

    wrapper.unmount();
  });

  it('never shows a value, only set by and when', async () => {
    getAgentCredentials.mockResolvedValue(credentials({ set: true, setBy: 'person@example.com', setAt: '2026-10-02T10:00:00Z' }));
    const wrapper = await mountDialog(AgentsDialog);

    expect(stateOf('claude')).toMatch(/claude credential: set by person@example\.com at /);
    expect(stateOf('grok')).toContain('not set');

    await openEditor('claude-headless');
    await type('API key', Secret);
    button('Save').click();
    await settle();

    expect(everythingShown()).not.toContain(Secret);
    expect(everythingShown()).not.toContain(Secret.slice(0, 12));
    expect(everythingShown()).not.toContain(Secret.slice(-12));

    wrapper.unmount();
  });

  it('setting through one preset updates the sibling preset\'s row', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(stateOf('claude')).toContain('not set');
    expect(stateOf('claude-headless')).toContain('not set');

    await openEditor('claude-headless');
    await type('API key', Secret);
    button('Save').click();
    await settle();

    // One PUT, never one per sibling: the value is the command's.
    expect(setAgentCredential).toHaveBeenCalledTimes(1);
    expect(stateOf('claude')).toContain('set by person@example.com');
    expect(stateOf('claude-headless')).toContain('set by person@example.com');
    // Another command's row is untouched.
    expect(stateOf('grok')).toContain('not set');

    // Cleared from the sibling, both rows follow.
    await openEditor('claude');
    [...document.body.querySelectorAll<HTMLElement>('[data-credential-editor] .q-checkbox')]
      .find((box) => box.textContent?.includes('Clear it'))!.click();
    await settle();
    button('Save').click();
    await settle();
    expect(stateOf('claude')).toContain('not set');
    expect(stateOf('claude-headless')).toContain('not set');

    wrapper.unmount();
  });

  it('labels the credential as shared by every preset that runs the command, and offers no sibling button', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(block('claude-headless').querySelector('[data-credential-shared]')?.textContent)
      .toContain('Shared by every preset that runs claude: also claude.');
    expect(block('claude').querySelector('[data-credential-shared]')?.textContent)
      .toContain('also claude-headless');
    expect(bodyText()).not.toMatch(/also set for/i);

    await openEditor('claude-headless');
    expect(document.body.querySelector('[data-credential-editor]')?.textContent).toContain('Stored once for claude');
    expect(document.body.querySelector('[data-provider-terms]')?.textContent).toContain(ProviderTermsSentence);

    wrapper.unmount();
  });

  it('switches a preset to issued through the tenant settings', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    sourceButton('claude-headless', 'Issued').click();
    await settle();

    expect(saveTenantSettings).toHaveBeenCalledTimes(1);
    // The preset's entry is added; every other entry - one naming no preset included - goes back.
    expect(saveTenantSettings.mock.calls[0]).toEqual([
      { 'agents.credentialSource': { grok: 'issued', gone: 'issued', 'claude-headless': 'issued' } },
    ]);
    // Only this preset's source moved: the sibling stays on the shared home.
    expect(sourceButton('claude-headless', 'Issued').getAttribute('aria-pressed')).toBe('true');
    expect(sourceButton('claude', 'Shared home').getAttribute('aria-pressed')).toBe('true');
    // Issued with nothing set says what that means.
    expect(block('claude-headless').querySelector('[data-credential-missing]')?.textContent).toContain('do not start');

    // Back to home removes the entry: absent is home.
    sourceButton('claude-headless', 'Shared home').click();
    await settle();
    expect(saveTenantSettings.mock.calls[1]).toEqual([{ 'agents.credentialSource': { grok: 'issued', gone: 'issued' } }]);

    wrapper.unmount();
  });

  it('states which credential the Concierge uses', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    expect(block('claude').querySelector('[data-concierge-line]')?.textContent)
      .toContain('The Concierge uses the issued credential, even when a login exists');
    expect(block('grok').querySelector('[data-concierge-line]')?.textContent)
      .toContain("uses the person's own login while one exists");
    // A headless preset runs no Concierge.
    expect(block('claude-headless').querySelector('[data-concierge-line]')).toBeNull();

    wrapper.unmount();
  });

  it('says the Concierge starts on the person\'s login only on a Concierge row', async () => {
    // Every preset on issued with nothing set: each row warns, but only a Concierge row names it.
    getTenantSettings.mockResolvedValue(settingsWith({
      claude: 'issued', 'claude-headless': 'issued', grok: 'issued', 'copilot-headless': 'issued',
    }));
    getAgentCredentials.mockResolvedValue(credentials({ source: 'issued' }).map((one) => ({ ...one, source: 'issued' as const })));
    const wrapper = await mountDialog(AgentsDialog);

    for (const name of ['claude', 'grok']) {
      expect(block(name).querySelector('[data-credential-missing]')?.textContent)
        .toContain("The Concierge starts on the person's own login");
    }
    for (const name of ['claude-headless', 'copilot-headless']) {
      const missing = block(name).querySelector('[data-credential-missing]')?.textContent ?? '';
      expect(missing).toContain('do not start');
      expect(missing).not.toContain('Concierge');
      expect(block(name).textContent).not.toContain("person's own login");
    }

    wrapper.unmount();
  });

  it('takes the value in a password field and holds nothing once it is saved', async () => {
    const wrapper = await mountDialog(AgentsDialog);
    const state = wrapper.vm as unknown as { credentialValue: string };

    await openEditor('claude-headless');
    expect(field('API key').getAttribute('type')).toBe('password');
    await type('API key', Secret);
    expect(state.credentialValue).toBe(Secret);

    button('Save').click();
    await settle();

    expect(setAgentCredential).toHaveBeenCalledTimes(1);
    // The saved value is gone from the screen's state, not only from a field that closed.
    expect(state.credentialValue).toBe('');
    expect(everythingShown()).not.toContain(Secret.slice(0, 12));

    wrapper.unmount();
  });

  it('refuses issued for a preset with no declaration', async () => {
    const wrapper = await mountDialog(AgentsDialog);

    const issued = sourceButton('my-model', 'Issued');
    expect(issued.hasAttribute('disabled') || issued.getAttribute('aria-disabled') === 'true').toBe(true);
    issued.click();
    await settle();

    expect(saveTenantSettings).not.toHaveBeenCalled();
    expect(block('my-model').querySelector('[data-no-declaration]')?.textContent).toContain('declares no issued credential');
    expect(document.body.querySelector('button[aria-label="Credential my-model"]')).toBeNull();

    wrapper.unmount();
  });
});
