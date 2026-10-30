// A TABLE COLUMN'S `format: "url"` (22750): a bare address is given https://, anything else is left
// exactly as written - the same rule as the Host's PluginTableColumn.WithScheme.
import { describe, expect, it } from 'vitest';
import type { PluginTableColumn, PluginTableShape } from '../../api/types';
import { withScheme, withSchemes } from '../pluginTable';

const column = (name: string, more: Partial<PluginTableColumn> = {}): PluginTableColumn => ({
  name,
  type: 'string',
  label: null,
  description: '',
  required: false,
  default: null,
  enum: null,
  min: null,
  max: null,
  integer: false,
  readOnly: false,
  masked: null,
  ...more,
});

const shape: PluginTableShape = {
  key: 'name',
  maxRows: 10,
  rowActions: [],
  columns: [
    column('name'),
    column('siteUrl', { format: 'url' }),
    column('signInUrl', { format: 'url' }),
    column('hosts'),
    column('status', { format: 'url', readOnly: true }),
  ],
};

describe('withScheme', () => {
  it.each([
    ['mysite.com', 'https://mysite.com'],
    ['www.mysite.com', 'https://www.mysite.com'],
    ['  mysite.com  ', 'https://mysite.com'],
    ['app.mysite.co.uk/login?next=/home', 'https://app.mysite.co.uk/login?next=/home'],
    ['mysite.com:8443/admin', 'https://mysite.com:8443/admin'],
    ['localhost:3000', 'https://localhost:3000'],
    ['127.0.0.1:8080', 'https://127.0.0.1:8080'],
  ])('gives the bare address %s https', (written, saved) => {
    expect(withScheme(written)).toBe(saved);
  });

  it.each([
    'https://mysite.com',
    'https://www.mysite.com/login',
    'http://mysite.com',
    'HTTP://MySite.com/',
    'javascript:alert(1)',
    'ftp://mysite.com',
    'mailto:a@mysite.com',
    'data:text/html,hi',
    'https://',
    'not a url',
    'foo bar.com',
    '/login',
    '//mysite.com',
    'dashboard',
    'mysite',
    '1.2.3',
    '',
    '   ',
  ])('leaves %j exactly as written', (written) => {
    expect(withScheme(written)).toBe(written);
  });
});

describe('withSchemes', () => {
  it('gives only the url columns a person writes a scheme, never Other hosts', () => {
    expect(
      withSchemes(shape, {
        name: 'a',
        siteUrl: 'mysite.com',
        signInUrl: 'www.mysite.com/login',
        hosts: 'login.mysite.com, https://sso.mysite.com/x',
        status: 'Verified',
      }),
    ).toEqual({
      name: 'a',
      siteUrl: 'https://mysite.com',
      signInUrl: 'https://www.mysite.com/login',
      hosts: 'login.mysite.com, https://sso.mysite.com/x',
      status: 'Verified',
    });
    expect(withSchemes(shape, { name: 'b', siteUrl: 'http://mysite.com', signInUrl: '/login' })).toEqual({
      name: 'b',
      siteUrl: 'http://mysite.com',
      signInUrl: '/login',
    });
  });
});
