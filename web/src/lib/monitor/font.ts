/**
 * THE MONITOR'S ICON FONT: the full Material Symbols Outlined, loaded by the Monitor only.
 *
 * The app serves a subset of this font (`css/material-symbols.scss`) holding the icons its own
 * screens use. A plugin's monitor hint may name any icon, so the Monitor asks for the whole font -
 * once, when it first opens - under a family of its own, so the app's subset is never replaced.
 * The file is the one `@quasar/extras` ships, built as its own asset (`scripts/monitor-symbols.mjs`)
 * and cached by the browser.
 *
 * Resolves to each icon name's codepoint, or null when the font could not be loaded: the Monitor
 * then keeps drawing a plain dot where an icon would be, and says nothing is wrong with the event.
 */
export const MonitorSymbolsFamily = 'Monitor Symbols'

export type Codepoints = Readonly<Record<string, number>>

let loading: Promise<Codepoints | null> | null = null

export function loadMonitorSymbols(): Promise<Codepoints | null> {
  loading ??= (async () => {
    try {
      const { fontUrl, codepoints } = await import('virtual:monitor-symbols')
      const face = new FontFace(MonitorSymbolsFamily, `url(${JSON.stringify(fontUrl)})`, { display: 'block' })
      await face.load()
      document.fonts.add(face)
      return codepoints
    } catch {
      // Asked again the next time the Monitor opens.
      loading = null
      return null
    }
  })()
  return loading
}

/** The character that draws `name`, or null when the font is not loaded or has no such icon. */
export function glyphOf(codepoints: Codepoints | null, name: string): string | null {
  if (!codepoints || !Object.hasOwn(codepoints, name)) return null
  return String.fromCodePoint(codepoints[name]!)
}

/** Forget a loaded font. For tests. */
export function resetMonitorSymbols() {
  loading = null
}
