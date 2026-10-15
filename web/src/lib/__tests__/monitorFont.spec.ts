// @vitest-environment happy-dom
//
// THE MONITOR'S FULL ICON FONT. Only the Monitor asks for it, once, when it first opens; the rest of
// the app keeps its subset (`css/material-symbols.scss`). Icons are drawn by the codepoint the font
// itself gives each name, never by typing the ligature name into the canvas.
import { readFileSync, readdirSync, statSync } from 'node:fs'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { MonitorSymbolsFamily, glyphOf, loadMonitorSymbols, resetMonitorSymbols } from '../monitor/font'

const web = join(__dirname, '../../..')
const FullFont = 'kJEhBvYX7BgnkSrUwT8OhrdQw4oELdPIeeII9v6oFsLjBuVY.woff2'

/** The app's own source files, tests excluded. */
function appSources(): { path: string; text: string }[] {
  const walk = (dir: string): string[] =>
    readdirSync(dir).flatMap((entry) => {
      const path = join(dir, entry)
      if (statSync(path).isDirectory()) return entry === '__tests__' || entry === 'test' ? [] : walk(path)
      return /\.(vue|ts|scss|css|html)$/.test(entry) ? [path] : []
    })
  return [...walk(join(web, 'src')), join(web, 'index.html')].map((path) => ({ path: path.slice(web.length + 1).replaceAll('\\', '/'), text: readFileSync(path, 'utf8') }))
}

function stubFonts(load: () => Promise<unknown>) {
  const made: { family: string; source: string }[] = []
  const added: unknown[] = []
  class FakeFontFace {
    constructor(
      public family: string,
      public source: string,
    ) {
      made.push({ family, source })
    }
    load = load
  }
  vi.stubGlobal('FontFace', FakeFontFace)
  Object.defineProperty(document, 'fonts', { value: { add: (face: unknown) => added.push(face) }, configurable: true })
  return { made, added }
}

afterEach(() => {
  vi.unstubAllGlobals()
  resetMonitorSymbols()
})

describe('the Monitor icon font', () => {
  it('is asked for by the Monitor and by nothing else in the app', () => {
    const sources = appSources()
    const asking = sources.filter((file) => file.text.includes('virtual:monitor-symbols') && !file.path.endsWith('.d.ts'))
    const loading = sources.filter((file) => /\bloadMonitorSymbols\(/.test(file.text) && !file.path.endsWith('lib/monitor/font.ts'))

    expect(asking.map((file) => file.path)).toEqual(['src/lib/monitor/font.ts'])
    expect(loading.map((file) => file.path)).toEqual(['src/components/MonitorView.vue'])
    // Nothing names the full font file or its stylesheet: the app's own icons stay on the subset.
    expect(sources.filter((file) => file.text.includes(FullFont) || file.text.includes('@quasar/extras/material-symbols-outlined'))).toEqual([])
    expect(readFileSync(join(web, 'src/css/material-symbols.scss'), 'utf8')).toContain('material-symbols-outlined-subset.woff2')
    const extras = /extras:\s*\[([\s\S]*?)\]/.exec(readFileSync(join(web, 'quasar.config.ts'), 'utf8'))![1]!
    expect(extras.replace(/\/\/.*$/gm, '')).not.toContain('material-symbols')
  })

  it('loads the full font file @quasar/extras ships, under a family of its own, once', async () => {
    const { made, added } = stubFonts(() => Promise.resolve())

    const first = await loadMonitorSymbols()
    const again = await loadMonitorSymbols()

    expect(made).toHaveLength(1)
    expect(made[0]!.family).toBe(MonitorSymbolsFamily)
    expect(MonitorSymbolsFamily).not.toBe('Material Symbols Outlined')
    expect(made[0]!.source).toContain(FullFont)
    expect(added).toHaveLength(1)
    expect(again).toBe(first)
  })

  it("maps names to the font's own codepoints, so the canvas draws a character, not a ligature name", async () => {
    stubFonts(() => Promise.resolve())

    const codepoints = await loadMonitorSymbols()

    expect(codepoints!['settings']).toBe(0xe8b8)
    expect(codepoints!['database']).toBe(0xf20e)
    expect(Object.keys(codepoints!).length).toBeGreaterThan(3000)
    expect(glyphOf(codepoints, 'database')).toBe(String.fromCodePoint(0xf20e))
    expect(glyphOf(codepoints, 'not_an_icon')).toBeNull()
    expect(glyphOf(codepoints, 'constructor')).toBeNull()
    expect(glyphOf(null, 'database')).toBeNull()
  })

  it('answers null when the font fails, and asks again next time', async () => {
    const { made } = stubFonts(() => Promise.reject(new Error('network')))

    expect(await loadMonitorSymbols()).toBeNull()
    expect(await loadMonitorSymbols()).toBeNull()
    expect(made).toHaveLength(2)
  })
})
