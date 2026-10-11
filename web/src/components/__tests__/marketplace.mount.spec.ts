// @vitest-environment happy-dom
//
// MARKETPLACE: the ribbon's button and the dialog it opens say Marketplace, never Solutions. The
// dialog's tabs are Browse (the package catalog), Installed (each installed package, and an Update
// where the catalog lists a newer version) and Advanced (Install from a folder, as it was).
//
// SEEN TO FAIL: before the rename the ribbon and the title said Solutions, the tabs were Installed
// and Get started, Get started had no filter, an installed tile offered no Update, and Install from a
// folder sat in the title row of Installed.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import { enableAutoUnmount, flushPromises, mount } from '@vue/test-utils';

vi.mock('../../lib/useAgentInstallations', () => ({
  useAgentInstallations: () => ({ badge: { value: null } }),
  refreshAgentInstallations: vi.fn(),
}));

import RibbonBar from '../RibbonBar.vue';
import SolutionsLauncher from '../SolutionsLauncher.vue';
import SolutionWizard from '../SolutionWizard.vue';
import InstallFromFolderDialog from '../InstallFromFolderDialog.vue';
import { Ribbon, SolutionsAction } from '../../lib/ribbon';
import type { InstalledSolution, MarketplaceCatalog, MarketplacePackage } from '../../api/types';
import { bodyFind, bodyText, mountDialog, resetBody } from '../../test/mountQuasar';
import { button, settle } from '../../test/formProbe';
import { fakeHost, installedRow, reply, sent, updatePreview, wizardRoutes, type Call, type Route } from '../../test/solutionFixtures';

const Root = '/data/documents';

function pkg(fields: Partial<MarketplacePackage> & Pick<MarketplacePackage, 'id' | 'name'>): MarketplacePackage {
  return {
    kind: 'solution',
    summary: `${fields.name} does its one job.`,
    description: '',
    version: '1.0.0',
    needs: [],
    catalogNeeds: { connections: [], secrets: [], inputs: [], runtimes: [] },
    installed: false,
    installedVersion: null,
    installedOn: [],
    updateAvailable: false,
    ...fields,
  };
}

const Mail = pkg({
  id: 'mail',
  name: 'Mail',
  version: '1.2.0',
  summary: 'Reads your mailbox and drafts replies.',
  description: 'Sorts the inbox by sender and writes a first answer to each.',
  needs: ['Needs a mailbox account connected (a mailbox by app password, a Microsoft account or a Google account).'],
  catalogNeeds: { connections: [{ slot: 'mailbox', providers: ['imap', 'microsoft', 'google'], required: true }], secrets: [], inputs: [], runtimes: [] },
});
const JobTracker = pkg({
  id: 'job-tracker',
  name: 'Job Tracker',
  version: '1.1.0',
  summary: 'Finds job postings that match your keywords.',
  description: 'Tracks them on a page, and drafts a cover letter when you press Apply.',
  needs: ['Needs a file in Resume.'],
  catalogNeeds: { connections: [], secrets: [], inputs: [{ name: 'Resume', kind: 'documents', required: true }], runtimes: [] },
  installed: true,
  installedVersion: '1.0.0',
  installedOn: ['job-tracker'],
  updateAvailable: true,
});
const WhoAmI = pkg({
  id: 'whoami',
  name: 'WhoAmI',
  kind: 'plugin',
  version: '0.3.0',
  summary: 'Answers with the member it runs as.',
  description: 'A small test plugin.',
  catalogNeeds: { connections: [], secrets: [], inputs: [], runtimes: ['python3'] },
});

const read = (packages: MarketplacePackage[]): MarketplaceCatalog => ({ checked: true, reason: null, checkedAt: '2026-10-09T08:00:00Z', packages });

let calls: Call[] = [];

const updateOfJobTracker: Route = (call) =>
  call.url === '/api/solutions/preview' && (call.body as { team?: string }).team === 'job-tracker'
    ? reply(200, updatePreview('job-tracker'))
    : undefined;

function serve(installed: InstalledSolution[], packages: MarketplacePackage[] = [JobTracker, Mail, WhoAmI]) {
  calls = [];
  vi.stubGlobal(
    'fetch',
    vi.fn(
      fakeHost(
        [
          (call) => (call.method === 'GET' && call.url === '/api/marketplace' ? reply(200, read(packages)) : undefined),
          (call) =>
            call.method === 'POST' && call.url === '/api/marketplace/job-tracker/fetch'
              ? reply(200, { id: 'job-tracker', version: '1.1.0', kind: 'solution', folder: 'Marketplace/job-tracker-1.1.0' })
              : undefined,
          (call) => (call.method === 'GET' && call.url === '/api/documents' ? reply(200, { folders: [], root: Root }) : undefined),
          updateOfJobTracker,
          ...wizardRoutes({ installed }),
        ],
        calls,
      ),
    ),
  );
}

enableAutoUnmount(afterEach);

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
  resetBody();
});

async function marketplace(installed: InstalledSolution[] = [], packages?: MarketplacePackage[]) {
  serve(installed, packages);
  const wrapper = await mountDialog(SolutionsLauncher);
  await settle();
  return wrapper;
}

async function openTab(name: string) {
  bodyFind(`[data-solutions-tab="${name}"]`)!.click();
  await settle();
}

const tabLabels = () => [...document.body.querySelectorAll('[data-solutions-tab]')].map((tab) => tab.textContent?.trim());

describe('Marketplace', () => {
  describe('its name', () => {
    beforeEach(() => setActivePinia(createPinia()));

    it('the ribbon names it Marketplace: its block, its button and the button tooltip', async () => {
      const tab = Ribbon.tabs.find((candidate) => candidate.items.some((item) => item.action === SolutionsAction))!;
      const item = tab.items.find((candidate) => candidate.action === SolutionsAction)!;
      expect(tab.label).toBe('Marketplace');
      expect(item.label).toBe('Marketplace');
      expect(item.tooltip).toMatch(/^Marketplace: /);

      vi.spyOn(HTMLElement.prototype, 'clientWidth', 'get').mockImplementation(function (this: HTMLElement) {
        return this.classList.contains('ribbon') ? 10_000 : 0;
      });
      const bar = mount(RibbonBar, { attachTo: document.body });
      await flushPromises();
      expect(bar.text()).toContain('Marketplace');
      expect(bar.text()).not.toContain('Solutions');
    });

    it('the dialog is titled Marketplace, with the tabs Browse, Installed and Advanced', async () => {
      await marketplace();
      expect(bodyFind('[data-solutions-launcher] .os-dialog-title')!.textContent?.trim()).toBe('Marketplace');
      expect(tabLabels()).toEqual(['Browse', 'Installed', 'Advanced']);
      expect(bodyText()).not.toContain('Solutions');
      expect(bodyText()).not.toContain('Get started');
    });
  });

  describe('Browse', () => {
    const shown = () => [...document.body.querySelectorAll('[data-catalog-package]')].map((tile) => tile.getAttribute('data-catalog-package'));

    async function filter(text: string) {
      const input = document.body.querySelector<HTMLInputElement>('input[data-catalog-filter], [data-catalog-filter] input')!;
      input.value = text;
      input.dispatchEvent(new Event('input', { bubbles: true }));
      await settle();
    }

    async function browse() {
      await marketplace();
      await openTab('get-started');
    }

    it('lists the catalog until a filter is typed', async () => {
      await browse();
      expect(shown()).toEqual(['job-tracker', 'mail', 'whoami']);
    });

    it.each([
      ['the name', 'WHOAMI', ['whoami']],
      ['the summary', 'Drafts REPLIES', ['mail']],
      ['the description', 'cover LETTER', ['job-tracker']],
      ['the words for what it needs (a connection)', 'gmail', ['mail']],
      ['the words for what it needs (a folder)', 'file in resume', ['job-tracker']],
      ['the words for what it needs (a runtime)', 'PYTHON 3', ['whoami']],
      ["the Host's sentences for what it needs", 'app password', ['mail']],
    ])('filters by %s, case-insensitively, as you type', async (_field, text, ids) => {
      await browse();
      await filter(text);
      expect(shown()).toEqual(ids);
    });

    it('an empty result says so in a sentence, and clearing the filter brings the cards back', async () => {
      await browse();
      await filter('spreadsheet');
      expect(shown()).toEqual([]);
      expect(bodyFind('[data-catalog-none-match]')!.textContent?.trim()).toBe('No package in the catalog matches “spreadsheet”.');

      await filter('');
      expect(shown()).toEqual(['job-tracker', 'mail', 'whoami']);
      expect(bodyFind('[data-catalog-none-match]')).toBeNull();
    });
  });

  describe('Installed', () => {
    it('says a newer catalog version is listed and offers Update; an up-to-date package has neither', async () => {
      await marketplace([installedRow('job-tracker', 'Job Tracker', '1.0.0'), installedRow('news', 'News desk', '0.3.0', 'news')]);
      await openTab('installed');

      const tracker = bodyFind('[data-solution-tile="job-tracker"]')!;
      expect(tracker.querySelector('[data-tile-update-available]')?.textContent?.trim()).toBe('Update available: 1.1.0');
      expect(tracker.querySelector('[data-tile-update]')).not.toBeNull();

      const news = bodyFind('[data-solution-tile="news"]')!;
      expect(news.querySelector('[data-tile-update-available]')).toBeNull();
      expect(news.querySelector('[data-tile-update]')).toBeNull();
    });

    it('offers no Update when the installed version is already the catalog one', async () => {
      await marketplace([installedRow('job-tracker', 'Job Tracker', '1.1.0')]);
      await openTab('installed');
      expect(bodyFind('[data-solution-tile="job-tracker"] [data-tile-update]')).toBeNull();
    });

    it('Update fetches the newer version and opens the wizard on "Update an existing team" for that team', async () => {
      const wrapper = await marketplace([installedRow('job-tracker', 'Job Tracker', '1.0.0')]);
      await openTab('installed');

      (bodyFind('[data-solution-tile="job-tracker"] [data-tile-update]') as HTMLElement).click();
      await settle();
      await settle();

      expect(sent(calls, 'POST', '/api/marketplace/job-tracker/fetch')).toHaveLength(1);
      const wizard = wrapper.findComponent(SolutionWizard);
      expect(wizard.props('modelValue')).toBe(true);
      expect(wizard.props('folder')).toBe(`${Root}/Marketplace/job-tracker-1.1.0`);
      expect(wizard.props('team')).toBe('job-tracker');
      expect(bodyFind('[data-mode-update]')?.getAttribute('aria-checked')).toBe('true');
      expect(bodyFind('[data-update-team="job-tracker"] .q-radio')?.getAttribute('aria-checked')).toBe('true');
      expect(sent(calls, 'POST', '/api/solutions/install')).toHaveLength(0);
      expect(sent(calls, 'POST', '/api/solutions/update')).toHaveLength(0);
    });
  });

  describe('Advanced', () => {
    it('holds Install from a folder, which opens the shared install dialog', async () => {
      const wrapper = await marketplace([installedRow('job-tracker', 'Job Tracker', '1.0.0')]);
      expect(bodyFind('[data-install-from-folder]')).toBeNull();

      await openTab('advanced');
      button('Install from a folder').click();
      await settle();

      expect(wrapper.findComponent(InstallFromFolderDialog).props('modelValue')).toBe(true);
    });
  });
});
