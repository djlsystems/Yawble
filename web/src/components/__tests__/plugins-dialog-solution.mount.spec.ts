// @vitest-environment happy-dom
//
// ADMIN > PLUGINS > INSTALL FROM A FOLDER, FOR A SOLUTION PACKAGE. The folder is checked first
// (`POST /api/solutions/check`, without `from`): a folder holding solution.json opens the solution
// wizard instead - with its plan, or with every refusal ("file field: reason") when the package
// fails its check - and no plugin install is sent. Only the check's "no solution.json" refusal goes
// on to the plain plugin install, as before.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';

import PluginsDialog from '../PluginsDialog.vue';
import type { PluginList } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle, type } from '../../test/formProbe';
import { Folder, fakeHost, okCheck, reply, sent, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';

const list: PluginList = { plugins: [], refused: [], versions: [] };

let calls: Call[] = [];

function serve(check: Route) {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) => (call.method === 'GET' && call.url === '/api/plugins' ? reply(200, list) : undefined),
          check,
          (call) =>
            call.url === '/api/plugins/install'
              ? reply(200, { id: 'sample-echo', version: '0.2.0', installed: true, replaced: false, reason: null })
              : undefined,
          ...wizardRoutes(),
        ],
        calls,
      ),
    ),
  );
}

beforeEach(() => setActivePinia(createPinia()));

afterEach(() => {
  vi.unstubAllGlobals();
  resetBody();
});

async function installFrom(folder: string) {
  await mountDialog(PluginsDialog, {}, { pinia: false });
  await settle();
  button('Install from a folder…').click();
  await settle();
  await type('Folder', folder);
  button('Install').click();
  await settle();
  await settle();
}

const checkRoute = (answer: unknown): Route => (call) =>
  call.method === 'POST' && call.url === '/api/solutions/check' ? reply(200, answer) : undefined;

describe('Plugins > Install from a folder, for a solution package', () => {
  it('opens the solution wizard for a folder holding solution.json, and installs no plugin', async () => {
    serve(checkRoute(okCheck()));

    await installFrom(Folder);

    expect(sent(calls, 'POST', '/api/solutions/check')[0]!.body).toEqual({ folder: Folder });
    expect(bodyFind('[data-solution-wizard]')).not.toBeNull();
    expect(bodyFind('[data-step="team"]')).not.toBeNull();
    expect(bodyText()).toContain('Job Tracker 1.1.0');
    expect(sent(calls, 'POST', '/api/plugins/install')).toHaveLength(0);
    // The wizard reuses the check it was handed rather than asking again.
    expect(sent(calls, 'POST', '/api/solutions/check')).toHaveLength(1);
  });

  it("shows a package's refusals, each as file field: reason, on the wizard's first screen", async () => {
    serve(
      checkRoute({
        ok: false,
        folder: Folder,
        plan: null,
        refusals: [
          { file: 'solution.json', field: 'triggers[1].member', reason: 'Name a member the package has.' },
          { file: 'skills/job-search-playbook.md', field: 'roles', reason: 'List the roles it is for.' },
        ],
      }),
    );

    await installFrom(Folder);

    const refusals = [...document.body.querySelectorAll('[data-refusal]')].map((item) => item.textContent);
    expect(refusals).toEqual([
      'solution.json triggers[1].member: Name a member the package has.',
      'skills/job-search-playbook.md roles: List the roles it is for.',
    ]);
    expect(sent(calls, 'POST', '/api/plugins/install')).toHaveLength(0);
    expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
  });

  it('a folder with no solution.json goes on to the plain plugin install', async () => {
    serve(
      checkRoute({
        ok: false,
        folder: '/data/plugins-src/sample-echo',
        plan: null,
        refusals: [{ file: 'solution.json', field: '(file)', reason: 'The folder holds no solution.json.' }],
      }),
    );

    await installFrom('/data/plugins-src/sample-echo');

    expect(sent(calls, 'POST', '/api/plugins/install')[0]!.body).toEqual({ path: '/data/plugins-src/sample-echo', replace: false });
    expect(bodyFind('[data-solution-wizard]')).toBeNull();
    expect(bodyFind('[data-install-verdict]')!.textContent).toContain('Installed sample-echo 0.2.0');
  });
});
