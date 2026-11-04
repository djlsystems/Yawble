// THE TEAM PICTURE TEMPLATE PASSES THE SITE POLICY (docs/sites.md, "The site policy"): no inline
// script or style, nothing fetched outside its own files and `_api/`, no storage, and every fetched
// word drawn as text. Read as text from `templates/team-picture/`, the folder the Host publishes.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const folder = fileURLToPath(new URL('../../../templates/team-picture/', import.meta.url));
const files = readdirSync(folder);
const read = (name: string) => readFileSync(folder + name, 'utf8');
const scripts = files.filter((name) => name.endsWith('.js'));
const html = read('index.html');
const css = read('picture.css');

/** The script text without comments, so a remark naming a forbidden call does not trip a check. */
const code = (name: string) =>
  read(name)
    .replace(/\/\*[\s\S]*?\*\//g, '')
    .replace(/(^|[^:'"])\/\/.*$/gm, '$1');

describe('the Team picture template, under the site policy', () => {
  it('holds an index.html, its own files only, no _api and no dot-file', () => {
    expect(files).toContain('index.html');
    expect(files.sort()).toEqual(['draw.js', 'index.html', 'page.js', 'picture.css']);
    for (const name of files) {
      expect(name.startsWith('.')).toBe(false);
      expect(statSync(folder + name).isFile()).toBe(true);
    }
    expect(files).not.toContain('_api');
  });

  it('has no inline script: every script is a file of its own or the helper script', () => {
    const tags = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/gi)];
    expect(tags.length).toBe(3);
    for (const [, attributes, body] of tags) {
      expect(body!.trim()).toBe('');
      const src = /\bsrc="([^"]+)"/.exec(attributes!)?.[1];
      expect(src === '/sites/_sdk/site.js' || /^[a-z]+\.js$/.test(src ?? '')).toBe(true);
    }
    // No event-handler attribute and no javascript: URL anywhere.
    expect(html).not.toMatch(/\son[a-z]+\s*=/i);
    expect(html).not.toMatch(/javascript:/i);
  });

  it('has no inline style: no <style> element, no style attribute, and no script that sets one', () => {
    expect(html).not.toMatch(/<style\b/i);
    expect(html).not.toMatch(/\sstyle\s*=/i);
    for (const name of scripts) {
      const text = code(name);
      expect(text, name).not.toMatch(/\.style\b/);
      expect(text, name).not.toMatch(/setAttribute\(\s*['"]style['"]/);
      expect(text, name).not.toMatch(/createElement\(\s*['"]style['"]/);
    }
  });

  it('reaches nothing outside its own files and _api/: no fetch, no other network, no absolute URL', () => {
    const links = [...html.matchAll(/\b(?:href|src)="([^"]+)"/g)].map((m) => m[1]);
    expect(links.sort()).toEqual(['/sites/_sdk/site.js', 'draw.js', 'page.js', 'picture.css']);
    for (const name of scripts) {
      const text = code(name);
      expect(text, name).not.toMatch(/\bfetch\s*\(|XMLHttpRequest|WebSocket|EventSource|sendBeacon|\bimport\s*\(|importScripts/);
      // The SVG namespace is a name, not a request; nothing else absolute.
      const urls = [...text.matchAll(/https?:\/\/[^'"\s)]+/g)].map((m) => m[0]);
      expect(urls.filter((url) => url !== 'http://www.w3.org/2000/svg'), name).toEqual([]);
    }
    expect(css).not.toMatch(/@import|url\(/);
  });

  it('keeps no storage: no localStorage, sessionStorage, IndexedDB or cookie', () => {
    for (const name of scripts) {
      expect(code(name), name).not.toMatch(/localStorage|sessionStorage|indexedDB|document\.cookie/);
    }
  });

  it('draws fetched text as text: no markup is ever inserted', () => {
    for (const name of scripts) {
      expect(code(name), name).not.toMatch(/innerHTML|outerHTML|insertAdjacentHTML|document\.write|DOMParser|createContextualFragment|\beval\s*\(|new Function/);
    }
  });

  it('reads only through the helper script: the data calls are site.data.list, nothing written', () => {
    const page = code('page.js');
    expect(page).toMatch(/site\.data\.list\(/);
    expect(page).not.toMatch(/\.data\.(put|delete)\(|site\.action\(/);
  });

  it('stops every animation under prefers-reduced-motion', () => {
    const animated = [...css.matchAll(/([^{}]+)\{[^{}]*\banimation\s*:(?!\s*none)[^;]+;[^{}]*\}/g)]
      .flatMap((m) => m[1]!.split(',').map((s) => s.trim()))
      .filter((selector) => !selector.startsWith('@') && !/^\d|^from|^to/.test(selector));
    const reduced = /@media \(prefers-reduced-motion: reduce\)\s*\{\s*([^{]+)\{\s*animation:\s*none;\s*\}\s*\}/.exec(css);
    expect(reduced).not.toBeNull();
    const stopped = reduced![1]!.split(',').map((s) => s.trim());
    expect(animated.length).toBeGreaterThan(0);
    for (const selector of animated) expect(stopped, selector).toContain(selector);
  });
});
