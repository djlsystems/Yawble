import { readFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';

/**
 * A class a component STYLES but never USES.
 *
 * The failure is silent: rename a class in a template and leave the selector in its own `<style>`
 * block, and a rule carrying `position: fixed` and `z-index: 7000` matches nothing. A panel then
 * renders as an ordinary static block one viewport BELOW the fold - it opens correctly, connects
 * its socket, spawns its agent, and is simply not on screen. Nothing throws, nothing logs, and the
 * server looks perfectly healthy.
 *
 * That is the shape this guards: a dangling selector is not a style that looks slightly wrong, it
 * is a rule that does not run at all, and the more load-bearing the rule the more completely the
 * component disappears.
 */
const componentsDirectory = join(import.meta.dirname, '..');

/** Every `.vue` file beside this spec's parent directory. */
function components(): string[] {
  return readdirSync(componentsDirectory)
    .filter((name) => name.endsWith('.vue'))
    .map((name) => join(componentsDirectory, name));
}

/**
 * Comments are stripped FIRST, in both halves.
 *
 * Without it a selector merely NAMED in a comment - `/* Same reason as RibbonBar's
 * .ribbon-item-wrap ... *\/` - reads as a definition, and a class mentioned in an HTML comment
 * reads as a use. Both produce false results, in opposite directions.
 */
function withoutComments(source: string): string {
  return source.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/<!--[\s\S]*?-->/g, ' ');
}

function section(source: string, tag: 'template' | 'style'): string {
  const match = source.match(new RegExp(`<${tag}[^>]*>([\\s\\S]*)</${tag}>`));

  return match ? withoutComments(match[1]!) : '';
}

/**
 * Class selectors this component's own style block defines.
 *
 * `:deep(...)` contents are removed rather than collected: a deep selector deliberately reaches
 * into a CHILD component's markup, so its classes are not expected in this template and flagging
 * them would train everyone to ignore this spec.
 */
function styled(style: string): string[] {
  const own = style.replace(/:deep\([^)]*\)/g, ' ');

  return [...new Set([...own.matchAll(/\.([a-zA-Z][\w-]*)/g)].map((m) => m[1]!))];
}

describe('every styled class is a class the template actually uses', () => {
  for (const file of components()) {
    const source = readFileSync(file, 'utf8');
    const template = section(source, 'template');
    const style = section(source, 'style');

    if (template === '' || style === '') continue;

    const name = file.split(/[\\/]/).pop()!;

    for (const cls of styled(style)) {
      it(`${name} uses .${cls}`, () => {
        // Word-boundary rather than a bare substring: `.console-host` must not be satisfied by a
        // template that only mentions `console-host-wrapper`.
        expect(new RegExp(`\\b${cls}\\b`).test(template)).toBe(true);
      });
    }
  }
});
