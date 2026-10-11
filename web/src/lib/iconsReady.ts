/**
 * ICONS ARE HIDDEN UNTIL THEIR FONT HAS LOADED.
 *
 * Every icon is a ligature: the element's text is the icon's NAME ("groups", "arrow_drop_down"),
 * and the Material Symbols font turns that word into a glyph. `font-display: block` hides text for
 * only about 3s, so on a slow link the names would show as words across the ribbon until the font
 * arrived. `app.scss` keeps icons invisible and clipped to their box until this class is on <html>.
 *
 * The font is a ~100 KB SUBSET (`css/material-symbols.scss`, `scripts/icon-font.mjs`), so the
 * wait is short; the hiding stays for the first paint before any font can have arrived.
 *
 * A TIMEOUT MARKS THE PAGE READY ANYWAY, so a font that never arrives costs words on screen rather
 * than icons missing for good.
 */
export const IconsReadyClass = 'icons-ready'

type FontLoader = { load(font: string, text?: string): Promise<unknown> }

export function markIconsReadyWhenLoaded(fonts: FontLoader | undefined, timeoutMs: number): Promise<void> {
  const mark = () => document.documentElement.classList.add(IconsReadyClass)

  if (!fonts) {
    mark()
    return Promise.resolve()
  }

  const loaded = fonts.load('24px "Material Symbols Outlined"', 'home').then(() => undefined, () => undefined)
  const timedOut = new Promise<void>((resolve) => setTimeout(resolve, timeoutMs))

  return Promise.race([loaded, timedOut]).then(mark)
}
