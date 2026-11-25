// @vitest-environment happy-dom
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { OUTPUT_MIN, followWindowHeight, outputHeight, setOutputHeight } from '../../src/renderer/src/agentOutputHeight';

// The remote view fits the screen after a rotation on iOS Safari. Playwright's WebKit keeps 100vh equal to the
// visible height, so what only iOS does is checked here in the sources: every full-height frame says 100dvh after its
// 100vh (iOS's 100vh is the height with its toolbars hidden, taller than what shows), and the page keeps its text size
// in landscape. The output boxes' limit (70% of the window's height) follows a resize.
const src = (rel: string) => readFileSync(path.resolve(__dirname, '../../src/renderer/src', rel), 'utf8');
const rule = (css: string, selector: string) => {
  const i = css.indexOf(`\n${selector} {`);
  expect(i, selector).toBeGreaterThanOrEqual(0);
  return css.slice(i, css.indexOf('}', i));
};

describe('full-height frames use the visible height (dvh), with vh as the fallback', () => {
  for (const [file, selector] of [['App.vue', '.app'], ['TerminalApp.vue', '.term-window'], ['components/Modal.vue', '.modal.full']] as const) {
    it(`${file} ${selector}`, () => {
      const r = rule(src(file), selector);
      const vh = r.search(/height: (calc\()?100vh/);
      const dvh = r.search(/height: (calc\()?100dvh/);
      expect(vh, r).toBeGreaterThanOrEqual(0);
      expect(dvh, r).toBeGreaterThan(vh);
    });
  }
  it('Modal.vue .modal: max-height in dvh after vh', () => {
    const r = rule(src('components/Modal.vue'), '.modal');
    expect(r.indexOf('max-height: calc(100dvh')).toBeGreaterThan(r.indexOf('max-height: calc(100vh'));
  });
  it("App.vue's page column never grows past the window, nor its row past the window's height (minmax(0, 1fr))", () => {
    expect(rule(src('App.vue'), '.app')).toContain('grid-template-columns: 268px minmax(0, 1fr)');
    expect(rule(src('App.vue'), '.app')).toContain('grid-template-rows: minmax(0, 1fr)');
    // ... and a sidebar taller than a short window (a phone in landscape at a desktop width) scrolls inside it.
    expect(rule(src('components/Sidebar.vue'), '.side')).toContain('overflow-y: auto');
    expect(rule(src('App.vue'), '.app.collapsed')).toContain('grid-template-columns: 56px minmax(0, 1fr)');
  });
  it('no scoped style is a bare 100vh height without its dvh', () => {
    for (const file of ['App.vue', 'TerminalApp.vue', 'components/Modal.vue']) {
      for (const line of src(file).split('\n').filter((l) => /height: (calc\()?100vh/.test(l))) expect(line, file).toMatch(/100dvh/);
    }
  });
});

describe('text size in landscape', () => {
  it('styles.css sets text-size-adjust: 100% on html', () => {
    const r = rule(src('styles.css'), 'html');
    expect(r).toContain('-webkit-text-size-adjust: 100%');
    expect(r).toContain(' text-size-adjust: 100%');
  });
  it("remote.html sets the -webkit- one on <body> itself (the build's CSS minifier drops it from styles.css)", () => {
    const html = readFileSync(path.resolve(__dirname, '../../src/renderer/remote.html'), 'utf8');
    expect(html).toMatch(/<body [^>]*style="[^"]*-webkit-text-size-adjust: 100%/);
  });
});

describe('the output boxes follow the window height', () => {
  const tall = window.innerHeight;
  afterEach(() => { Object.defineProperty(window, 'innerHeight', { value: tall, configurable: true }); setOutputHeight(120); });

  it('a shorter window clamps the height, a taller one gives back the chosen height, and the kept value is not changed', () => {
    Object.defineProperty(window, 'innerHeight', { value: 1000, configurable: true });
    setOutputHeight(600);
    expect(outputHeight.value).toBe(600);
    Object.defineProperty(window, 'innerHeight', { value: 400, configurable: true });
    window.dispatchEvent(new Event('resize'));
    expect(outputHeight.value).toBe(280);
    expect(localStorage.getItem('yawble.agentOutput')).toBe('600');
    Object.defineProperty(window, 'innerHeight', { value: 1000, configurable: true });
    followWindowHeight();
    expect(outputHeight.value).toBe(600);
  });
  it('never below the minimum', () => {
    Object.defineProperty(window, 'innerHeight', { value: 20, configurable: true });
    followWindowHeight();
    expect(outputHeight.value).toBe(OUTPUT_MIN);
  });
});
