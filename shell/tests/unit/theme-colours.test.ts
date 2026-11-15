// @vitest-environment happy-dom
// B029: the window's colours come from the theme variables in styles.css (light on :root, dark on
// [data-theme="dark"] on <html>). These tests keep it that way: no hard-coded colour in a component's <style>,
// a dark value for every variable, and the Monitor reading the same values in both themes.
import { readdirSync, readFileSync } from 'node:fs';
import { join, relative, resolve, sep } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { StatusColor } from '../../src/renderer/src/board/cards';

const shellSrc = resolve(__dirname, '../../src');
const codeBrowserSrc = resolve(__dirname, '../../../code-browser/src');
const stylesCss = readFileSync(join(shellSrc, 'renderer/src/styles.css'), 'utf8');
const codeBrowserCss = readFileSync(join(codeBrowserSrc, 'theme.css'), 'utf8');
const terminalCss = readFileSync(join(shellSrc, 'renderer/src/terminal-theme.css'), 'utf8');

/** .vue files whose <style> may keep literal colours, each with its reason. */
const AllowList: Record<string, string> = {
  // The member avatar's ring is cut with a mask; #000 there is the mask's opacity, not a colour on screen.
  'shell/src/renderer/src/faces/MemberAvatar.vue': 'mask alpha',
};
/** The Monitor keeps its own dark look in both themes: its files are not swept. */
const isMonitor = (rel: string) => rel.startsWith('shell/src/renderer/src/monitor/');

const Colour = /#[0-9a-fA-F]{3,8}\b|\b(?:rgba?|hsla?|hwb|lab|lch|oklab|oklch)\(/;
/** The CSS named colours (CSS Color 4) and the system colours. transparent, currentColor and inherit are allowed. */
const NamedColours = (
  'aliceblue antiquewhite aqua aquamarine azure beige bisque black blanchedalmond blue blueviolet brown burlywood '
  + 'cadetblue chartreuse chocolate coral cornflowerblue cornsilk crimson cyan darkblue darkcyan darkgoldenrod darkgray '
  + 'darkgreen darkgrey darkkhaki darkmagenta darkolivegreen darkorange darkorchid darkred darksalmon darkseagreen '
  + 'darkslateblue darkslategray darkslategrey darkturquoise darkviolet deeppink deepskyblue dimgray dimgrey dodgerblue '
  + 'firebrick floralwhite forestgreen fuchsia gainsboro ghostwhite gold goldenrod gray green greenyellow grey honeydew '
  + 'hotpink indianred indigo ivory khaki lavender lavenderblush lawngreen lemonchiffon lightblue lightcoral lightcyan '
  + 'lightgoldenrodyellow lightgray lightgreen lightgrey lightpink lightsalmon lightseagreen lightskyblue lightslategray '
  + 'lightslategrey lightsteelblue lightyellow lime limegreen linen magenta maroon mediumaquamarine mediumblue '
  + 'mediumorchid mediumpurple mediumseagreen mediumslateblue mediumspringgreen mediumturquoise mediumvioletred '
  + 'midnightblue mintcream mistyrose moccasin navajowhite navy oldlace olive olivedrab orange orangered orchid '
  + 'palegoldenrod palegreen paleturquoise palevioletred papayawhip peachpuff peru pink plum powderblue purple '
  + 'rebeccapurple red rosybrown royalblue saddlebrown salmon sandybrown seagreen seashell sienna silver skyblue '
  + 'slateblue slategray slategrey snow springgreen steelblue tan teal thistle tomato turquoise violet wheat white '
  + 'whitesmoke yellow yellowgreen '
  + 'canvas canvastext linktext visitedtext activetext buttonface buttontext buttonborder field fieldtext highlight '
  + 'highlighttext selecteditem selecteditemtext mark marktext graytext accentcolor accentcolortext'
).split(' ');
/** A named colour as a word of its own: not part of a longer name, a variable (--red), a class (.gold) or an id. */
const NamedColour = new RegExp(`(?<![\\w.#-])(?:${NamedColours.join('|')})(?![\\w-])`, 'i');
/** A declaration's value ("prop: value"), at the start of a line or after "{" or ";"; a selector line starts with ".", "#" or ":" instead. */
const Declaration = /(?:^|[{;])\s*[\w-]+\s*:\s*([^;{}]*)/g;

/** Each line of a .vue file's <style> blocks that has a literal colour (comments ignored). */
function literalColours(vue: string): { line: number; text: string }[] {
  const hits: { line: number; text: string }[] = [];
  let inStyle = false;
  vue.split(/\r?\n/).forEach((raw, i) => {
    if (/<style\b/.test(raw)) inStyle = true;
    const text = raw.replace(/\/\*.*?\*\//g, '').replace(/<\/?style[^>]*>/g, '');
    const named = [...text.matchAll(Declaration)].some((m) => NamedColour.test(m[1]!));
    if (inStyle && (Colour.test(text) || named)) hits.push({ line: i + 1, text: raw.trim() });
    if (/<\/style>/.test(raw)) inStyle = false;
  });
  return hits;
}

function vueFiles(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap((e) =>
    e.isDirectory() ? vueFiles(join(dir, e.name)) : e.name.endsWith('.vue') ? [join(dir, e.name)] : []);
}
const repoRoot = resolve(__dirname, '../../..');
const allVue = [...vueFiles(shellSrc), ...vueFiles(codeBrowserSrc)].map((abs) => ({ abs, rel: relative(repoRoot, abs).split(sep).join('/') }));

/** The custom properties declared in each rule of a stylesheet, by selector. */
function rules(css: string): { selector: string; vars: Map<string, string> }[] {
  const out: { selector: string; vars: Map<string, string> }[] = [];
  for (const m of css.replace(/\/\*[\s\S]*?\*\//g, '').matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    const vars = new Map<string, string>();
    for (const d of m[2]!.matchAll(/(--[\w-]+)\s*:\s*([^;]+);?/g)) vars.set(d[1]!, d[2]!.trim());
    out.push({ selector: m[1]!.trim().replace(/\s+/g, ' '), vars });
  }
  return out;
}
const isDark = (selector: string) => selector.includes('[data-theme="dark"]') && !selector.includes('.monitor');
/** Variables that are not colours, so they have no dark value. */
const NotColours = new Set(['--dialog-top']);

function lightAndDark(css: string) {
  const light = new Map<string, string>(); const dark = new Map<string, string>();
  for (const r of rules(css)) {
    const parts = r.selector.split(',').map((s) => s.trim());
    if (parts.includes(':root')) r.vars.forEach((v, k) => light.set(k, v));
    else if (parts.every(isDark)) r.vars.forEach((v, k) => dark.set(k, v));
  }
  return { light, dark };
}

describe('no hard-coded colours in components', () => {
  it('finds a literal colour in a <style>, and ignores the template and comments', () => {
    const vue = '<template><div style="color: #123456" /></template>\n<style scoped>\n/* was #fff */\n.a { color: var(--ink); }\n.b { background: #fff; }\n'
      + '.c { box-shadow: 0 0 0 1px rgba(0, 0, 0, .1); }\n.d { color: white; }\n.e { --white-ish: var(--surface); }\n</style>';
    expect(literalColours(vue).map((h) => h.line)).toEqual([5, 6, 7]);
  });

  it('finds CSS named colours in values, and keeps transparent, currentColor, inherit, variables and class names', () => {
    const vue = '<style scoped>\n.a { background: whitesmoke; }\n.b { color: gray; }\n.c {\n  border: 1px solid Tomato;\n}\n'
      + '.d { outline-color: canvastext; }\n.e { background: transparent; color: currentColor; fill: inherit; }\n'
      + '.tag.red, .gold:hover { color: var(--red); }\n.f { --gray-line: var(--line); }\n.g { grid-area: main; }\n'
      + '.h { background: color-mix(in srgb, var(--ink), var(--surface)); }\n</style>';
    expect(literalColours(vue).map((h) => h.line)).toEqual([2, 3, 5, 7]);
  });

  it('allow-lists only the avatar mask (the menu bar and the terminals are scanned like every other component)', () => {
    expect(Object.keys(AllowList)).toEqual(['shell/src/renderer/src/faces/MemberAvatar.vue']);
  });

  it('every .vue outside monitor/ and the allow-list takes its colours from the theme variables', () => {
    expect(allVue.length).toBeGreaterThan(40); // the scan sees the components
    const found = allVue.filter((f) => !isMonitor(f.rel) && !(f.rel in AllowList))
      .flatMap((f) => literalColours(readFileSync(f.abs, 'utf8')).map((h) => `${f.rel}:${h.line}: ${h.text}`));
    expect(found, 'use a variable from styles.css (add a semantic one there if none fits)').toEqual([]);
  });

  it('every variable a component reads is defined (no fallback hides a missing one)', () => {
    const defined = new Set([...`${stylesCss}\n${codeBrowserCss}\n${terminalCss}`.matchAll(/(--[\w-]+)\s*:/g)].map((m) => m[1]));
    const missing = allVue.filter((f) => !isMonitor(f.rel) && !(f.rel in AllowList)).flatMap((f) => {
      const src = readFileSync(f.abs, 'utf8');
      const local = new Set([...src.matchAll(/['"]?(--[\w-]+)['"]?\s*:/g)].map((m) => m[1])); // set by the component itself
      const styles = (src.match(/<style[^>]*>[\s\S]*?<\/style>/g) ?? []).join('\n');
      return [...styles.matchAll(/var\((--[\w-]+)\s*\)/g)].map((m) => m[1]!).filter((n) => !defined.has(n) && !local.has(n)).map((n) => `${f.rel}: ${n}`);
    });
    expect([...new Set(missing)]).toEqual([]);
  });

  it('the Kanban status colours are theme variables', () => {
    const { light, dark } = lightAndDark(stylesCss);
    for (const c of Object.values(StatusColor)) {
      const name = /^var\((--status-[\w-]+)\)$/.exec(c)?.[1];
      expect(name, c).toBeTruthy();
      expect(light.has(name!) && dark.has(name!), name).toBe(true);
    }
  });
});

describe('the dark palette', () => {
  it('gives every :root variable in styles.css a dark value', () => {
    const { light, dark } = lightAndDark(stylesCss);
    expect(light.size).toBeGreaterThan(40);
    expect([...light.keys()].filter((k) => !NotColours.has(k) && !dark.has(k))).toEqual([]);
    expect([...dark.keys()].filter((k) => !light.has(k))).toEqual([]); // nothing only dark
  });

  it('gives every code-browser highlight colour a dark value, and they differ from the light ones', () => {
    const { light, dark } = lightAndDark(codeBrowserCss);
    expect(light.size).toBeGreaterThanOrEqual(8);
    for (const k of light.keys()) expect(dark.get(k), k).toBeTruthy();
    for (const [k, v] of light) expect(dark.get(k), k).not.toBe(v);
  });

  it('the code browser\'s highlight.js classes take their colours from that palette, and CodeBrowser loads it', () => {
    const { light } = lightAndDark(codeBrowserCss);
    const vue = readFileSync(join(codeBrowserSrc, 'CodeBrowser.vue'), 'utf8');
    expect(vue).toContain("import './theme.css';");
    const rules = vue.split(/\r?\n/).filter((l) => l.includes(':deep(.hljs-') && /\bcolor:/.test(l));
    expect(rules.length).toBeGreaterThanOrEqual(8);
    for (const r of rules) {
      const name = /color:\s*var\((--cb-hl-[\w-]+)\);/.exec(r)?.[1];
      expect(name && light.has(name), r).toBe(true);
    }
  });

  it('sets color-scheme so form controls and scrollbars follow the theme', () => {
    expect(stylesCss).toMatch(/:root\s*\{[^}]*color-scheme:\s*light dark/);
    expect(stylesCss).toMatch(/:root\[data-theme="dark"\]\s*\{\s*color-scheme:\s*dark/);
    expect(stylesCss).toMatch(/:root\[data-theme="light"\][^{]*\{\s*color-scheme:\s*light/);
  });
});

describe('the Monitor does not change with the theme', () => {
  afterEach(() => { document.head.innerHTML = ''; document.body.innerHTML = ''; document.documentElement.removeAttribute('data-theme'); });

  function page(theme: 'light' | 'dark') {
    document.head.innerHTML = `<style>${stylesCss}</style>`;
    document.documentElement.setAttribute('data-theme', theme);
    document.body.innerHTML = '<div id="app"><div class="monitor" data-test="monitor"></div></div>';
    const read = (el: Element, names: string[]) => Object.fromEntries(names.map((n) => [n, getComputedStyle(el).getPropertyValue(n).trim()]));
    return { read, html: document.documentElement, body: document.body, monitor: document.querySelector('.monitor')! };
  }

  const { light } = lightAndDark(stylesCss);
  const names = [...light.keys()].filter((k) => !NotColours.has(k));
  const states = names.filter((n) => n.startsWith('--state-'));

  it('the theme does change the rest of the window (the check below is not vacuous)', () => {
    // (happy-dom does not inherit custom properties, so each is read where it is declared: <html>, <body>, .monitor)
    const l = page('light'); const lightSurface = l.read(l.html, ['--surface']); const lightState = l.read(l.html, ['--state-running']);
    const d = page('dark'); const darkSurface = d.read(d.html, ['--surface']); const darkState = d.read(d.body, ['--state-running']);
    expect(lightSurface['--surface']).toBe(light.get('--surface'));
    expect(darkSurface['--surface']).not.toBe(lightSurface['--surface']);
    expect(darkState['--state-running']).toMatch(/^#/);
    expect(darkState['--state-running']).not.toBe(lightState['--state-running']);
  });

  it('.monitor (and so everything inside it) reads every variable at its light value in the dark theme', () => {
    const l = page('light'); const atLight = l.read(l.html, names);
    const d = page('dark'); const atDark = d.read(d.monitor, names);
    expect(atLight['--ink']).toBe(light.get('--ink'));
    expect(atDark).toEqual(atLight);
    expect(d.read(d.monitor, ['color-scheme'])['color-scheme']).toBe('light');
  });

  it('the canvas palette (scene.ts reads --state-* from <html>) is the same in both themes', () => {
    const l = page('light'); const atLight = l.read(l.html, states);
    const d = page('dark'); const atDark = d.read(d.html, states);
    expect(states.length).toBe(6);
    expect(atDark).toEqual(atLight);
    expect(atLight['--state-running']).toBe(light.get('--state-running'));
  });

  it('the Monitor sources carry no theme switch of their own', () => {
    const dir = join(shellSrc, 'renderer/src/monitor');
    for (const f of readdirSync(dir)) expect(readFileSync(join(dir, f), 'utf8'), f).not.toMatch(/data-theme|prefers-color-scheme/);
  });
});
