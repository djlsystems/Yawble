// The module `scripts/monitor-symbols.mjs` serves: the full icon font's URL, and the codepoint each
// icon name's glyph sits at. Only `lib/monitor/font.ts` imports it.
declare module 'virtual:monitor-symbols' {
  export const fontUrl: string
  export const codepoints: Record<string, number>
}
