/**
 * Every bare icon name in this app is a MATERIAL SYMBOLS OUTLINED name.
 *
 * Quasar renders a bare `<q-icon name="settings">` with the `material-icons` class, which is the
 * filled Material Icons font. That font is not loaded, so without this map every bare name
 * would render as its own letters. Rewriting a bare name to `sym_o_<name>` is how Quasar spells
 * "this ligature, in the Symbols Outlined font", and doing it in one map rather than at ~200 call
 * sites keeps the templates readable and a new icon correct by default.
 *
 * Only a bare ligature name is rewritten. Anything carrying a prefix (`sym_r_`, `mdi-`, `img:`,
 * `svguse:`, an SVG path) is already explicit and passes through untouched.
 */
const BareLigature = /^[a-z0-9_]+$/

export function symbolsIconMap(name: string): { icon: string } | undefined {
  if (!BareLigature.test(name) || name.startsWith('sym_')) return undefined

  return { icon: `sym_o_${name}` }
}
