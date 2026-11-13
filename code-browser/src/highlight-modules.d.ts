// highlight.js's package exports give its lib/core and lib/languages/* entry points no types; these say what they are,
// from its main types (resolved by the shell's tsconfig paths to shell/node_modules, or by this package's own install).
declare module 'highlight.js/lib/core' {
  import hljs from 'highlight.js';
  export default hljs;
}
declare module 'highlight.js/lib/languages/*' {
  import type { LanguageFn } from 'highlight.js';
  const language: LanguageFn;
  export default language;
}
