import { describe, expect, it } from 'vitest';
import type { PluginConfigField, PluginTableColumn, PluginTableShape } from '../../api/types';
import { saveBody, settingsBody } from '../pluginSettings';
import {
  duplicateRow,
  rowActionHref,
  rowActionLinks,
  rowActionsFor,
  rowForSave,
  rowProblems,
  tableProblem,
  tableSecretValuesBody,
  withRowSecrets,
} from '../pluginTable';

const column = (name: string, type: PluginTableColumn['type'], more: Partial<PluginTableColumn> = {}): PluginTableColumn => ({
  name,
  type,
  label: null,
  description: '',
  required: false,
  default: null,
  enum: null,
  min: null,
  max: null,
  integer: false,
  readOnly: false,
  masked: type === 'secret' ? true : null,
  ...more,
});

const table: PluginTableShape = {
  key: 'name',
  maxRows: 3,
  columns: [
    column('name', 'string', { required: true }),
    column('method', 'enum', { enum: ['password', 'token', 'interactive'], required: true }),
    column('verifyEvery', 'number', { min: 1, integer: true, default: 24 }),
    column('active', 'bool', { default: true }),
    column('password', 'secret'),
    column('status', 'string', { readOnly: true }),
  ],
  rowActions: [{ action: 'sign-in', label: 'Sign in', column: 'method', equals: 'interactive' }],
};

const field: PluginConfigField = {
  type: 'table', description: '', required: false, default: [], enum: null, setBy: 'person', table,
};

describe('a table row is held to the Host rules', () => {
  it('takes a good row', () => {
    expect(rowProblems(table, { name: 'prod-admin', method: 'password', verifyEvery: '6', active: true }, [])).toEqual({});
  });

  it('refuses a bad key, a duplicate key ignoring case, a missing required cell, a bad enum and a number out of bounds', () => {
    expect(rowProblems(table, { name: '-x', method: 'password' }, [])).toHaveProperty('name');
    expect(rowProblems(table, { name: 'Prod', method: 'password' }, [{ name: 'prod' }]).name).toMatch(/already named/);
    expect(rowProblems(table, { name: 'a' }, [])).toHaveProperty('method');
    expect(rowProblems(table, { name: 'a', method: 'carrier-pigeon' }, [])).toHaveProperty('method');
    expect(rowProblems(table, { name: 'a', method: 'token', verifyEvery: '0' }, [])).toHaveProperty('verifyEvery');
    expect(rowProblems(table, { name: 'a', method: 'token', verifyEvery: '1.5' }, [])).toHaveProperty('verifyEvery');
  });

  it('holds a table with too many rows', () => {
    const rows = ['a', 'b', 'c', 'd'].map((name) => ({ name, method: 'token' }));
    expect(tableProblem(table, rows)).toMatch(/at most 3/);
  });
});

describe('what a save sends', () => {
  it('never sends a read-only or secret cell, and sends a number as a number', () => {
    const row = rowForSave(table, { name: 'a', method: 'token', verifyEvery: '12', active: false, password: 'x', status: 'Verified' });
    expect(row).toEqual({ name: 'a', method: 'token', verifyEvery: 12, active: false });
  });

  it('puts the rows under config and secret cells under tableSecretValues, never in config', () => {
    const config = { entries: [{ name: 'a', method: 'token', status: 'Verified' }] };
    const draft = withRowSecrets({}, 'entries', 'a', { password: 'hunter22', other: '' });
    const body = saveBody({ config: { entries: field }, secrets: {} }, config, {}, {}, undefined, draft);

    expect(body.config).toEqual({ entries: [{ name: 'a', method: 'token' }] });
    expect(JSON.stringify(body.config)).not.toContain('hunter22');
    expect(body.tableSecretValues).toEqual({ entries: { a: { password: 'hunter22' } } });
  });

  it('leaves an empty table out, its default', () => {
    expect(settingsBody({ config: { entries: field }, secrets: {} }, { entries: [] }, {}).config).toEqual({});
  });

  it('sends a cleared cell as null and nothing for a row with nothing typed', () => {
    expect(tableSecretValuesBody({ entries: { a: { password: null }, b: {} } })).toEqual({ entries: { a: { password: null } } });
    expect(tableSecretValuesBody({ entries: { b: { password: '' } } })).toBeUndefined();
  });
});

describe('duplicate and row actions', () => {
  it('copies only the writable cells under a free name', () => {
    const rows = [{ name: 'a', method: 'token', status: 'Verified' }, { name: 'a-copy', method: 'token' }];
    expect(duplicateRow(table, rows[0]!, rows)).toEqual({ name: 'a-copy-2', method: 'token' });
  });

  it('offers a row action only on a saved row whose column matches, and links it only once a page registers it', () => {
    const row = { name: 'a', method: 'interactive' };
    expect(rowActionsFor(table, row, false)).toEqual([]);
    expect(rowActionsFor(table, { ...row, method: 'token' }, true)).toEqual([]);
    expect(rowActionsFor(table, row, true).map((a) => a.action)).toEqual(['sign-in']);

    const context = { team: 't', member: 'm', table: 'entries', row: 'a' };
    expect(rowActionHref('sign-in', context)).toBeNull();
    rowActionLinks['sign-in'] = (c) => `/sign-in/${c.team}/${c.member}/${c.table}/${c.row}`;
    try {
      expect(rowActionHref('sign-in', context)).toBe('/sign-in/t/m/entries/a');
    } finally {
      delete rowActionLinks['sign-in'];
    }
  });
});
