import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

/**
 * THE CONCIERGE COULD NOT BE MOVED OR RESIZED ON AN IPAD, AND THE WHOLE FIX IS ONE CSS
 * DECLARATION PER HANDLE.
 *
 * The handlers were never the problem: they are Pointer Events, which iOS has supported for years,
 * and they work with a mouse. What happened on a touch device is that the browser claimed the
 * first move for panning the page and delivered `pointercancel` instead of `pointermove`. The drag
 * simply did not start. **Nothing throws, nothing logs, and no mounted test can see it** - happy-dom
 * has no layout engine and no touch-action semantics, so a simulated `pointerdown` followed by
 * `pointermove` passes with or without the rule.
 *
 * SO THIS SCANS THE SOURCE, which is the same answer `styles-match-templates.spec.ts` gives to the
 * same shape of problem: a fact that lives only in a stylesheet needs a reader that reads
 * stylesheets - and it reads the `<style>` block alone, never the template or the script. It is a weaker test than a real gesture on a real iPad and it is not pretending
 * otherwise - what it defends against is the declaration being removed as noise by somebody who
 * cannot see what it does, which is exactly how it came to be missing.
 *
 * THE HIT AREA IS THE OTHER HALF and is asserted too. A 6px handle is a mouse target; a fingertip
 * covers something like forty, so with the gesture working and the target unchanged the drag that
 * lands is the one that misses the handle and hits the terminal. Both halves are needed and
 * removing either leaves the feature unusable by hand, which is why neither is left to a comment.
 */
describe('the Concierge window can be dragged and resized by touch', () => {
  // THE STYLE BLOCK ONLY. Behaviour - including that a cancelled drag ends the drag - is asserted
  // by mounting, in `concierge-panel.mount.spec.ts`; what is left here is CSS, which has no DOM.
  const file = readFileSync(
    fileURLToPath(new URL('../ConciergePanel.vue', import.meta.url)),
    'utf8',
  )
  const source = /<style[^>]*>([\s\S]*)<\/style>/.exec(file)?.[1] ?? ''

  /** The block of a single CSS rule, so `touch-action` is read from the right one. */
  function ruleFor(selector: string): string {
    const at = source.indexOf(`${selector} {`)

    expect(at, `no \`${selector}\` rule in ConciergePanel.vue`).toBeGreaterThan(-1)

    return source.slice(at, source.indexOf('}', at))
  }

  /**
   * EVERY DRAGGABLE SURFACE, not just the one that was reported. The east edge is the one a person
   * reaches for first and it would have been the only one fixed if this were driven by the report.
   */
  const draggable = [
    '.concierge-bar',
    '.concierge-resize-e',
    '.concierge-resize-s',
    '.concierge-resize-se',
  ]

  it.each(draggable)('%s tells the browser not to claim the gesture', (selector) => {
    expect(ruleFor(selector)).toContain('touch-action: none')
  })

  /**
   * `pointer: coarse` AND NOT A WIDTH. An iPad is 1024px, which is exactly where Quasar's `md`
   * begins - so a width breakpoint answers "is this a touch device" wrongly for the single device
   * this was reported on. The compose and key bars above already learned this, and the comment
   * there records that `lt-md` hid both of them on an iPad for the same reason.
   */
  it('enlarges the handles for a finger by asking about the pointer, not the width', () => {
    expect(source).toContain('@media (pointer: coarse)')

    const at = source.indexOf('@media (pointer: coarse)')
    const block = source.slice(at, at + 400)

    for (const selector of ['.concierge-resize-e', '.concierge-resize-s', '.concierge-resize-se']) {
      expect(block).toContain(selector)
    }
  })

  /**
   * THE BOTTOM WAS TRUNCATED, AND `visualViewport` IS NOT WHAT MISSES IT. The shell's height is
   * measured from the visual viewport, which is right and handles the keyboard - but the home
   * indicator is drawn OVER the page rather than inset from it, so the last rows of a correctly
   * sized panel sit underneath it.
   *
   * PADDING AND NOT A SMALLER HEIGHT, so the dark ground still reaches the bottom of the screen;
   * shrinking the shell would show a strip of the console beneath a panel meant to cover it.
   */
  it('keeps its last rows clear of the home indicator', () => {
    expect(ruleFor('.concierge-shell')).toContain('padding-bottom: env(safe-area-inset-bottom')
  })
})
