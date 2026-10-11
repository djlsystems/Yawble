// @vitest-environment happy-dom
//
// The front door shows the logo for the theme: dark lettering on the light page, white lettering
// on the dark one. The login form itself is covered elsewhere and stubbed here.
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { mount } from '@vue/test-utils';
import { createPinia, setActivePinia } from 'pinia';
import { Dark, Quasar } from 'quasar';
import { createMemoryHistory, createRouter } from 'vue-router';
import LandingPage from '../LandingPage.vue';
import { productLogoDark, productLogoLight } from '../../presentation/product';
import { bundleBuild, releaseLabel } from '../../lib/buildInfo';

async function mountDoor() {
  setActivePinia(createPinia());
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: LandingPage }] });
  await router.push('/');
  await router.isReady();

  return mount(LandingPage, {
    global: { plugins: [router, [Quasar, { plugins: { Dark } }]], stubs: { AuthForm: true } },
  });
}

beforeEach(() => {
  vi.stubGlobal('fetch', vi.fn(() => new Promise(() => {})));
});

afterEach(() => {
  vi.unstubAllGlobals();
  Dark.set(false);
});

describe('the front door logo', () => {
  it('uses the dark-lettered logo in the light theme', async () => {
    Dark.set(false);
    const door = await mountDoor();

    expect(door.find('.wordmark img').attributes('src')).toBe(productLogoLight);
  });

  it('uses the white-lettered logo in the dark theme', async () => {
    Dark.set(true);
    const door = await mountDoor();

    expect(door.find('.wordmark img').attributes('src')).toBe(productLogoDark);
  });
});

describe('the front door version', () => {
  /** Below the logo, the same text the top bar shows. */
  it('shows this build under the logo', async () => {
    const door = await mountDoor();

    const version = door.find('.door-version');
    expect(version.exists()).toBe(true);
    expect(version.text()).toBe(releaseLabel(bundleBuild.version));
    expect(door.html().indexOf('wordmark')).toBeLessThan(door.html().indexOf('door-version'));
  });
});

