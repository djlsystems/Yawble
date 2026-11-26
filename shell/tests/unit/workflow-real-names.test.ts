// @vitest-environment happy-dom
// Workflows name the person's integrations by their real names: the member tile shows what each member uses
// ("Yawble DistinctID Support → Other inbox") and sets or clears the override; Add member uses a real name as it is;
// the wrappers for workflows.catalog, workflows.useRealNames and schemas.usage; and the Schemas tab: a built-in schema as a
// read-only tree, your own schema's Design and JSON tabs as one schema (the designer's round trip), and Used in. Fakes only.
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils';
import { afterEach, describe, expect, it } from 'vitest';
import type { AgentCard, YawbleApi } from '../../src/shared/api';
import WorkflowMemberAdd from '../../src/renderer/src/components/WorkflowMemberAdd.vue';
import WorkflowMemberPane from '../../src/renderer/src/components/WorkflowMemberPane.vue';
import SchemasTab from '../../src/renderer/src/components/workflows/SchemasTab.vue';
import { applyRemoteAccess } from '../../src/renderer/src/remoteAccess';
import { bindingsFrom, integrationText, overrideChoices } from '../../src/renderer/src/workflowMembers';
import { workflowsApi, teamBindingChoices } from '../../src/renderer/src/workflows/api';
import {
  designFields, designProblems, moveField, newField, schemaFromFields, schemaTree, typeNode, typeText, type JsonSchema,
} from '../../src/renderer/src/workflows/schemaModel';

const SUPPORT = 'Yawble DistinctID Support';
const OTHER = 'Other inbox';

type Calls = { method: string; params: Record<string, unknown> }[];

function fakeApi(calls: Calls, answers: Record<string, (p: Record<string, unknown>) => unknown>) {
  return {
    getInfo: async () => ({}), getAvailableBackends: async () => [], onEvent: () => () => {},
    call: async (method: string, params: Record<string, unknown> = {}) => {
      calls.push({ method, params });
      const a = answers[method];
      if (a) return a(params);
      if (method.endsWith('.list') || method.endsWith('s')) return [];
      throw new Error(`no fake for ${method}`);
    },
  } as unknown as YawbleApi;
}

const wrappers: VueWrapper[] = [];
const keep = <T extends VueWrapper>(w: T) => { wrappers.push(w); return w; };
afterEach(() => { applyRemoteAccess('full'); for (const w of wrappers.splice(0)) w.unmount(); });

// ---------------------------------------------------------------- integrations

const emails = [
  { id: 1, name: SUPPORT, kind: 'email', enabledAll: false, teamIds: [1, 2] },
  { id: 2, name: OTHER, kind: 'email', enabledAll: false, teamIds: [1] },
  { id: 3, name: 'Elsewhere inbox', kind: 'email', enabledAll: false, teamIds: [7] },
];
const apis = [{ kind: 'http', name: 'Twilio', all: true, teams: [] }, { kind: 'website', name: 'Portal', all: true, teams: [] }];

const member = (bindings: Record<string, string>, integrations: unknown[]) => ({
  memberId: 9, teamId: 1, team: 'Support', name: 'InboxWatcher', workflowId: 5, workflow: 'inbox-sms-alert', version: null, runsVersion: 4,
  settings: {}, bindings, timeoutMinutes: 10, consecutiveFailures: 0, running: false, lastRun: null, nextTriggerAt: null, integrations,
});
const overridden = member({ [SUPPORT]: OTHER }, [
  { name: SUPPORT, kind: 'email', uses: OTHER, overridden: true },
  { name: 'Twilio', kind: 'api', uses: 'Twilio', overridden: false },
]);
const plain = member({}, [
  { name: SUPPORT, kind: 'email', uses: SUPPORT, overridden: false },
  { name: 'Twilio', kind: 'api', uses: 'Twilio', overridden: false },
]);

const card = {
  member: { id: 9, teamId: 1, team: 'Support', name: 'InboxWatcher', role: 'member', cli: 'claude', instructions: '', model: '', credential: 'default' },
  kind: 'workflow', state: 'idle', queuePosition: null, queueLength: 0, queuedInstructions: 0, statusLine: null, runId: null,
  runStartedAt: null, lastRunState: null, lastRunReason: null,
} as unknown as AgentCard;

function memberApi(calls: Calls, current: { m: unknown }, over: Record<string, (p: Record<string, unknown>) => unknown> = {}) {
  return fakeApi(calls, {
    'workflows.memberGet': () => current.m,
    'workflows.runs': () => [],
    'workflows.memberState': () => ({ memberId: 9, variables: {}, consecutiveFailures: 0, updatedAt: null }),
    'workflows.memberFiles': () => ({ memberId: 9, files: [] }),
    'triggers.list': () => [],
    'integrations.emailList': () => emails,
    'integrations.apiList': () => apis,
    'workflows.memberUpdate': (p) => {
      const b = p.bindings as Record<string, string>;
      current.m = b[SUPPORT] ? overridden : plain;
      return current.m;
    },
    ...over,
  });
}

describe('the helpers', () => {
  it('say an override as "name → other", offer the same kind only, and save no entry for "use it as it is"', () => {
    expect(integrationText({ name: SUPPORT, uses: OTHER })).toBe(`${SUPPORT} → ${OTHER}`);
    expect(integrationText({ name: 'Twilio', uses: 'Twilio' })).toBe('Twilio');
    expect(integrationText({ name: 'mail', uses: SUPPORT })).toBe(`mail → ${SUPPORT}`);
    expect(integrationText({ name: 'mail', uses: '' })).toBe('mail → (not set)');
    const choices = [{ name: SUPPORT, kind: 'email' }, { name: OTHER, kind: 'email' }, { name: 'Twilio', kind: 'api' }];
    expect(overrideChoices(choices, { name: SUPPORT, kind: 'email' })).toEqual([OTHER]);
    expect(overrideChoices(choices, { name: 'Twilio', kind: 'api' })).toEqual([]);
    const real = (n: string) => n !== 'mail';
    expect(bindingsFrom({ [SUPPORT]: '', Twilio: 'Twilio', mail: SUPPORT }, real)).toEqual({ mail: SUPPORT });
    expect(bindingsFrom({ [SUPPORT]: OTHER }, real)).toEqual({ [SUPPORT]: OTHER });
  });

  it('teamBindingChoices lists the Email integrations and web APIs turned on for the team, by their names', async () => {
    const api = fakeApi([], { 'integrations.emailList': () => emails, 'integrations.apiList': () => apis });
    expect(await teamBindingChoices(api, 1)).toEqual([
      { name: 'Twilio', kind: 'api' }, { name: SUPPORT, kind: 'email' }, { name: OTHER, kind: 'email' },
    ]);
    expect((await teamBindingChoices(api, 2)).map((c) => c.name)).toEqual(['Twilio', SUPPORT]);
  });

  it('the wrappers call workflows.catalog, workflows.useRealNames and schemas.usage with the pinned params', async () => {
    const calls: Calls = [];
    const api = fakeApi(calls, {
      'workflows.catalog': () => ({ integrations: [], functions: [] }),
      'workflows.useRealNames': (p) => ({ document: p.document, renamed: { mail: SUPPORT }, unresolved: [] }),
      'schemas.usage': () => ({ workflows: [] }),
    });
    const s = workflowsApi(api);
    await s.catalog(1);
    await s.catalog();
    const doc = { format: 'yawble-workflow/1', id: 'x', steps: [] };
    expect(await s.useRealNames<{ renamed: Record<string, string> }>(doc, { mail: SUPPORT })).toMatchObject({ renamed: { mail: SUPPORT } });
    await s.useRealNames(doc);
    await s.schemaUsage('EmailMessage');
    expect(calls.map((c) => [c.method, c.params])).toEqual([
      ['workflows.catalog', { teamId: 1 }], ['workflows.catalog', {}],
      ['workflows.useRealNames', { document: doc, bindings: { mail: SUPPORT } }], ['workflows.useRealNames', { document: doc }],
      ['schemas.usage', { name: 'EmailMessage' }],
    ]);
  });
});

describe('the member tile', () => {
  it(`shows "${SUPPORT} → ${OTHER}" and clears the override`, async () => {
    const calls: Calls = [];
    const current = { m: overridden as unknown };
    const w = keep(mount(WorkflowMemberPane, { props: { card, api: memberApi(calls, current), pollMs: 60_000 } }));
    await flushPromises();
    const line = w.get(`[data-test="workflow-member-integration-${SUPPORT}"]`);
    expect(line.text()).toContain(`${SUPPORT} → ${OTHER}`);
    expect(line.classes()).toContain('over');
    expect(w.get('[data-test=workflow-member-integration-Twilio]').text()).toContain('Twilio');

    await w.get('[data-test=workflow-member-integrations-change]').trigger('click');
    await flushPromises();
    const select = w.get(`[data-test="workflow-member-override-${SUPPORT}"] select`);
    expect(select.findAll('option').map((o) => o.text())).toEqual([`Use ${SUPPORT}`, `Use ${OTHER} instead`]);
    expect((select.element as HTMLSelectElement).value).toBe(OTHER);
    // Twilio has no other API on the team: only "Use Twilio".
    expect(w.get('[data-test=workflow-member-override-Twilio] select').findAll('option').map((o) => o.text())).toEqual(['Use Twilio']);
    await select.setValue('');
    await w.get('[data-test=workflow-member-integrations-form]').trigger('submit');
    await flushPromises();
    expect(calls.find((c) => c.method === 'workflows.memberUpdate')!.params).toEqual({ memberId: 9, bindings: {} });
    expect(w.get(`[data-test="workflow-member-integration-${SUPPORT}"]`).text()).not.toContain('→');
  });

  it('sets an override, and shows the core\'s refusal without losing the choice', async () => {
    const calls: Calls = [];
    const current = { m: plain as unknown };
    let refuse = true;
    const w = keep(mount(WorkflowMemberPane, { props: { card, api: memberApi(calls, current, {
      'workflows.memberUpdate': (p) => {
        if (refuse) throw Object.assign(new Error(`'${SUPPORT}' cannot be replaced by ${OTHER}: ${OTHER} is not turned on for Support`), { code: 'bad_workflow' });
        current.m = overridden;
        return current.m;
      },
    }), pollMs: 60_000 } }));
    await flushPromises();
    expect(w.get(`[data-test="workflow-member-integration-${SUPPORT}"]`).text()).toBe(`${SUPPORT} (Email)`);
    await w.get('[data-test=workflow-member-integrations-change]').trigger('click');
    await flushPromises();
    await w.get(`[data-test="workflow-member-override-${SUPPORT}"] select`).setValue(OTHER);
    await w.get('[data-test=workflow-member-integrations-form]').trigger('submit');
    await flushPromises();
    expect(w.get('[data-test=workflow-member-integrations-error]').text()).toContain('is not turned on for Support');
    expect((w.get(`[data-test="workflow-member-override-${SUPPORT}"] select`).element as HTMLSelectElement).value).toBe(OTHER);

    refuse = false;
    await w.get('[data-test=workflow-member-integrations-form]').trigger('submit');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'workflows.memberUpdate').at(-1)!.params).toEqual({ memberId: 9, bindings: { [SUPPORT]: OTHER } });
    expect(w.get(`[data-test="workflow-member-integration-${SUPPORT}"]`).text()).toContain(`${SUPPORT} → ${OTHER}`);
  });

  it('a Visitor reads the integrations and gets no Change', async () => {
    applyRemoteAccess('read');
    const w = keep(mount(WorkflowMemberPane, { props: { card, api: memberApi([], { m: overridden }), pollMs: 60_000 } }));
    await flushPromises();
    expect(w.get(`[data-test="workflow-member-integration-${SUPPORT}"]`).text()).toContain(`→ ${OTHER}`);
    expect(w.find('[data-test=workflow-member-integrations-change]').exists()).toBe(false);
  });
});

describe('Add member with a workflow that names real integrations', () => {
  const document = {
    format: 'yawble-workflow/1', id: 'inbox-sms-alert', name: 'Inbox SMS alert', settings: [],
    requires: [{ name: SUPPORT, kind: 'email', actions: ['list'] }, { name: 'Twilio', kind: 'api', actions: ['CreateMessage'] }, { name: 'mail', kind: 'email', actions: ['get'] }],
  };

  it('uses each real name as it is unless replaced, and an alias still needs its integration', async () => {
    const calls: Calls = [];
    const api = fakeApi(calls, {
      'workflows.list': () => [{ id: 5, key: 'inbox-sms-alert', name: 'Inbox SMS alert', description: '', latestVersion: 1, runnableVersion: 1, draft: false, updatedAt: '', usedBy: [] }],
      'workflows.versions': () => [{ version: 1, ok: true, createdAt: '' }],
      'workflows.get': () => ({ id: 5, key: 'inbox-sms-alert', name: 'Inbox SMS alert', version: 1, ok: true, document }),
      'integrations.emailList': () => emails,
      'integrations.apiList': () => apis,
      'workflows.memberAdd': (p) => ({ ...plain, name: p.name }),
    });
    const w = keep(mount(WorkflowMemberAdd, { props: { api, teamId: 1, name: 'InboxWatcher' } }));
    await flushPromises();
    await w.get('select[data-test=workflow-member-workflow]').setValue('5');
    await flushPromises();
    const support = w.get(`[data-test="workflow-binding-${SUPPORT}"] select`);
    expect(support.findAll('option').map((o) => o.text())).toEqual([`Use ${SUPPORT}`, `Use ${OTHER} instead`]);
    expect((support.element as HTMLSelectElement).value).toBe('');
    // The alias has no binding yet: Add waits for it.
    expect((w.get('[data-test=workflow-member-add]').element as HTMLButtonElement).disabled).toBe(true);
    await w.get('[data-test=workflow-binding-mail] select').setValue(SUPPORT);
    await w.get('[data-test=workflow-member-add]').trigger('click');
    await flushPromises();
    expect(calls.find((c) => c.method === 'workflows.memberAdd')!.params.bindings).toEqual({ mail: SUPPORT });

    await support.setValue(OTHER);
    await w.get('[data-test=workflow-member-add]').trigger('click');
    await flushPromises();
    expect(calls.filter((c) => c.method === 'workflows.memberAdd').at(-1)!.params.bindings).toEqual({ [SUPPORT]: OTHER, mail: SUPPORT });
  });
});

// ---------------------------------------------------------------- schemas

/** The built-in EmailMessage as schemas.get gives it (its full JSON Schema). */
const EMAIL_MESSAGE: JsonSchema = {
  type: 'object', description: 'An email message, without its attachments.',
  properties: {
    id: { type: 'string', description: 'Opaque id of the message.' },
    threadId: { type: ['string', 'null'], description: 'The conversation it belongs to.' },
    from: { $ref: 'EmailAddress', description: 'The sender.' },
    to: { type: 'array', items: { $ref: 'EmailAddress' }, description: 'The To recipients.' },
    receivedAt: { type: 'string', format: 'date-time', description: 'When the server received it.' },
    hasAttachments: { type: 'boolean' },
  },
  required: ['id', 'from', 'receivedAt'],
};

/** A schema of the person's own, with every shape the designer keeps. */
const TRIAGE: JsonSchema = {
  type: 'object', description: 'A triage result.',
  properties: {
    category: { type: 'string', enum: ['order', 'proof', 'other'], description: 'What it is about.' },
    score: { type: 'number' },
    count: { type: 'integer', description: 'How many.' },
    due: { type: ['string', 'null'], format: 'date' },
    sender: { $ref: 'EmailAddress' },
    tags: { type: 'array', items: { type: 'string' } },
    lines: { type: 'array', items: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] } },
    extra: {},
  },
  required: ['category', 'score'],
};

describe('the schema model', () => {
  it('reads a built-in as a tree: types, required, descriptions, enum values and the schemas it refers to', () => {
    const tree = schemaTree(EMAIL_MESSAGE);
    expect(tree.map((f) => [f.name, f.type, f.required])).toEqual([
      ['id', 'string', true], ['threadId', 'string', false], ['from', 'EmailAddress', true], ['to', 'list<EmailAddress>', false],
      ['receivedAt', 'datetime', true], ['hasAttachments', 'bool', false],
    ]);
    expect(tree[1]!.nullable).toBe(true);
    expect(tree[2]!.ref).toBe('EmailAddress');
    expect(tree[3]!.ref).toBe('EmailAddress');
    expect(tree[2]!.description).toBe('The sender.');
    const fired = schemaTree({ type: 'object', properties: { kind: { type: 'string', enum: ['every', 'cron', 'once'] } } });
    expect(fired[0]!.enum).toEqual(['every', 'cron', 'once']);
    const nested = schemaTree(TRIAGE).find((f) => f.name === 'lines')!;
    expect(nested.type).toBe('list<object>');
    expect(nested.children!.map((c) => [c.name, c.type, c.required])).toEqual([['text', 'string', true]]);
  });

  it('round trips a schema through the designer unchanged, and writes each edit into it', () => {
    expect(schemaFromFields(TRIAGE, designFields(TRIAGE))).toEqual(TRIAGE);
    expect(JSON.stringify(schemaFromFields(TRIAGE, designFields(TRIAGE)))).toBe(JSON.stringify(TRIAGE)); // the same key order too
    expect(schemaFromFields(EMAIL_MESSAGE, designFields(EMAIL_MESSAGE))).toEqual(EMAIL_MESSAGE);

    let fields = designFields(TRIAGE);
    fields = [...fields, { ...newField(fields), name: 'recipients', type: 'list<EmailAddress>', required: true, description: 'Who.' }];
    fields = moveField(fields, fields.length - 1, -(fields.length - 1)); // to the top
    fields = fields.filter((f) => f.name !== 'extra');
    fields = fields.map((f) => (f.name === 'score' ? { ...f, type: 'int', required: false } : f));
    fields = fields.map((f) => (f.name === 'category' ? { ...f, enumValues: 'order, proof' } : f));
    const out = schemaFromFields(TRIAGE, fields);
    const props = out.properties as Record<string, JsonSchema>;
    expect(Object.keys(props)).toEqual(['recipients', 'category', 'score', 'count', 'due', 'sender', 'tags', 'lines']);
    expect(props.recipients).toEqual({ description: 'Who.', type: 'array', items: { $ref: 'EmailAddress' } });
    expect(props.score).toEqual({ type: 'integer' });
    expect(props.category!.enum).toEqual(['order', 'proof']);
    expect(props.due).toEqual(TRIAGE.properties && (TRIAGE.properties as Record<string, unknown>).due); // kept: nullable date
    expect(out.required).toEqual(['recipients', 'category']);
    expect(out.description).toBe('A triage result.');
    expect(moveField(['a', 'b'], 0, -1)).toEqual(['a', 'b']);
  });

  it('types and their schemas, and what keeps fields from being a schema', () => {
    for (const t of ['string', 'int', 'float', 'bool', 'date', 'datetime', 'duration', 'object', 'list<int>', 'list<list<EmailAddress>>', 'EmailAddress'])
      expect(typeText(typeNode(t))).toBe(t);
    expect(designProblems([
      { name: '', type: 'string', required: false, description: '', enumValues: '' },
      { name: 'a', type: 'Nope', required: false, description: '', enumValues: '' },
      { name: 'a', type: 'int', required: false, description: '', enumValues: 'x' },
      { name: '1x', type: 'list<EmailAddress>', required: false, description: '', enumValues: '' },
    ], ['EmailAddress'])).toEqual([
      'Field 1 needs a name.',
      "a: 'Nope' is not a type (a native type, list<…> or a schema's name).",
      "'a' is there twice.",
      'a: enum values are for a string field.',
      "'1x' is not a field name (letters, digits and _, not starting with a digit).",
    ]);
  });
});

function schemasApi(calls: Calls, over: Record<string, (p: Record<string, unknown>) => unknown> = {}) {
  return fakeApi(calls, {
    'schemas.list': () => [
      { name: 'EmailMessage', description: 'An email message.', latestVersion: 1, builtIn: true, updatedAt: null },
      { name: 'EmailAddress', description: 'A mailbox.', latestVersion: 1, builtIn: true, updatedAt: null },
      { name: 'Triage', description: 'A triage result.', latestVersion: 2, builtIn: false, updatedAt: '' },
    ],
    'schemas.get': (p) => p.name === 'Triage'
      ? { name: 'Triage', description: 'A triage result.', version: 2, builtIn: false, schema: TRIAGE, createdAt: '' }
      : p.name === 'EmailAddress'
        ? { name: 'EmailAddress', description: 'A mailbox.', version: 1, builtIn: true, schema: { type: 'object', properties: { address: { type: 'string' } }, required: ['address'] }, createdAt: null }
        : { name: 'EmailMessage', description: 'An email message.', version: 1, builtIn: true, schema: EMAIL_MESSAGE, createdAt: null },
    'schemas.usage': (p) => ({ workflows: p.name === 'EmailMessage' ? [{ id: 1, name: 'Inbox SMS alert', version: 4, variables: ['last'], steps: ['mark'] }] : [] }),
    'schemas.save': (p) => ({ name: p.name, version: 3, breaks: [] }),
    ...over,
  });
}

describe('the Schemas tab', () => {
  it('shows a built-in as a read-only tree, follows a reference, and lists where it is used', async () => {
    const calls: Calls = [];
    const w = keep(mount(SchemasTab, { props: { api: schemasApi(calls) } }));
    await flushPromises();
    await w.get('[data-test=schema-row][data-name=EmailMessage]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=schema-readonly]').exists()).toBe(true);
    expect(w.find('[data-test=schema-save]').exists()).toBe(false);
    expect(w.find('[data-test=schema-view-design]').exists()).toBe(false);
    const rows = w.findAll('[data-test=schema-tree-field]');
    expect(rows.map((r) => [r.attributes('data-name'), r.get('[data-test=schema-tree-type]').text()])).toEqual([
      ['id', 'string'], ['threadId', 'string or null'], ['from', 'EmailAddress'], ['to', 'list<EmailAddress>'], ['receivedAt', 'datetime'], ['hasAttachments', 'bool'],
    ]);
    expect(rows.filter((r) => r.find('[data-test=schema-tree-required]').exists()).map((r) => r.attributes('data-name'))).toEqual(['id', 'from', 'receivedAt']);
    expect(rows[2]!.get('[data-test=schema-tree-description]').text()).toBe('The sender.');
    const used = w.get('[data-test=schema-used-in]');
    expect(used.get('[data-test=schema-use]').text()).toContain('Inbox SMS alert');
    expect(used.get('[data-test=schema-use-variables]').text()).toContain('last');
    expect(used.get('[data-test=schema-use-steps]').text()).toContain('mark');
    expect(calls.find((c) => c.method === 'schemas.usage')!.params).toEqual({ name: 'EmailMessage' });

    // JSON is read-only too.
    await w.get('[data-test=schema-view-json]').trigger('click');
    expect(w.get('[data-test=json-text]').attributes('readonly')).toBeDefined();

    // A reference opens the schema it names.
    await w.get('[data-test=schema-view-tree]').trigger('click');
    await w.get('[data-test=schema-tree-field][data-name=from] [data-test=schema-tree-ref]').trigger('click');
    await flushPromises();
    expect(w.get('[data-test=schema-editor]').text()).toContain('EmailAddress');
    expect(w.findAll('[data-test=schema-tree-field]').map((r) => r.attributes('data-name'))).toEqual(['address']);
    expect(w.get('[data-test=schema-unused]').text()).toBe('No workflow uses it.');
  });

  it('your own schema: Design and JSON edit one schema, bad JSON keeps the form, and Save sends it', async () => {
    const calls: Calls = [];
    const w = keep(mount(SchemasTab, { props: { api: schemasApi(calls) } }));
    await flushPromises();
    await w.get('[data-test=schema-row][data-name=Triage]').trigger('click');
    await flushPromises();
    expect(w.find('[data-test=schema-design]').exists()).toBe(true);
    const names = () => w.findAll('[data-test=schema-field-name]').map((i) => (i.element as HTMLInputElement).value);
    expect(names()).toEqual(['category', 'score', 'count', 'due', 'sender', 'tags', 'lines', 'extra']);
    expect(w.findAll('[data-test=schema-field-type]').map((i) => (i.element as HTMLInputElement).value))
      .toEqual(['string', 'float', 'int', 'date', 'EmailAddress', 'list<string>', 'list<object>', 'any']);
    expect((w.get('[data-test=schema-field][data-index="0"] [data-test=schema-field-enum]').element as HTMLInputElement).value).toBe('order, proof, other');

    // Add a field, name and type it, move it up, remove one.
    await w.get('[data-test=schema-field-add]').trigger('click');
    const last = () => w.findAll('[data-test=schema-field]').at(-1)!;
    await last().get('[data-test=schema-field-name]').setValue('recipients');
    await last().get('[data-test=schema-field-type]').setValue('list<EmailAddress>');
    await last().get('[data-test=schema-field-required]').setValue(true);
    await last().get('[data-test=schema-field-description]').setValue('Who is told.');
    await last().get('[data-test=schema-field-up]').trigger('click');
    await w.get('[data-test=schema-field][data-index="6"] [data-test=schema-field-remove]').trigger('click'); // lines
    expect(names()).toEqual(['category', 'score', 'count', 'due', 'sender', 'tags', 'recipients', 'extra']);

    // The JSON tab shows the same schema.
    await w.get('[data-test=schema-view-json]').trigger('click');
    const area = () => w.get('[data-test=json-text]');
    const shown = JSON.parse((area().element as HTMLTextAreaElement).value) as JsonSchema;
    expect(Object.keys(shown.properties as object)).toEqual(['category', 'score', 'count', 'due', 'sender', 'tags', 'recipients', 'extra']);
    expect((shown.properties as Record<string, unknown>).recipients).toEqual({ description: 'Who is told.', type: 'array', items: { $ref: 'EmailAddress' } });
    expect(shown.required).toEqual(['category', 'score', 'recipients']);

    // A JSON edit that parses rewrites the form; one that does not is refused and the form stays.
    const edited = { ...shown, properties: { ...(shown.properties as object), note: { type: 'string', description: 'Free text.' } } };
    await area().setValue(JSON.stringify(edited, null, 2));
    await area().setValue('{ "type": "object", ');
    expect(w.get('[data-test=schema-json-problem]').text()).toContain('Not valid JSON');
    expect((w.get('[data-test=schema-save]').element as HTMLButtonElement).disabled).toBe(true);
    await w.get('[data-test=schema-view-design]').trigger('click');
    expect(w.find('[data-test=schema-design-stale]').exists()).toBe(true);
    expect(names()).toEqual(['category', 'score', 'count', 'due', 'sender', 'tags', 'recipients', 'extra', 'note']);

    // Back to a good schema (a form edit writes it), then Save sends that one schema.
    await w.get('[data-test=schema-field][data-index="8"] [data-test=schema-field-description]').setValue('Anything else.');
    await w.get('[data-test=schema-save]').trigger('click');
    await flushPromises();
    const saved = calls.find((c) => c.method === 'schemas.save')!.params;
    expect(saved.name).toBe('Triage');
    const schema = JSON.parse(saved.schema as string) as JsonSchema;
    expect(Object.keys(schema.properties as object)).toEqual(['category', 'score', 'count', 'due', 'sender', 'tags', 'recipients', 'extra', 'note']);
    expect((schema.properties as Record<string, unknown>).note).toEqual({ type: 'string', description: 'Anything else.' });
    expect((schema.properties as Record<string, unknown>).due).toEqual({ type: ['string', 'null'], format: 'date' });
    expect(w.get('[data-test=schema-saved]').text()).toBe('Saved Triage as version 3.');
  });

  it('a form with a problem is not saved and says why', async () => {
    const calls: Calls = [];
    const w = keep(mount(SchemasTab, { props: { api: schemasApi(calls) } }));
    await flushPromises();
    await w.get('[data-test=schema-new]').trigger('click');
    await w.get('[data-test=schema-name]').setValue('Order');
    await w.get('[data-test=schema-field-add]').trigger('click');
    await w.get('[data-test=schema-field-type]').setValue('Nope');
    expect(w.get('[data-test=schema-design-problems]').text()).toContain("'Nope' is not a type");
    expect((w.get('[data-test=schema-save]').element as HTMLButtonElement).disabled).toBe(true);
    await w.get('[data-test=schema-field-type]').setValue('EmailMessage');
    expect(w.find('[data-test=schema-design-problems]').exists()).toBe(false);
    await w.get('[data-test=schema-save]').trigger('click');
    await flushPromises();
    const schema = JSON.parse(calls.find((c) => c.method === 'schemas.save')!.params.schema as string);
    expect(schema).toEqual({ type: 'object', properties: { field1: { $ref: 'EmailMessage' } } });
  });
});
