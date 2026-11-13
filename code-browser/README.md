# code-browser

The read-only Code browser of a member's clone: Vue 3 + TypeScript components the shell shows in its
Code dialog, on the desktop and in the cloud viewer. It browses folders, previews files by type, downloads a file, and
shows what the agent is editing now and has edited recently. It never writes anything and never runs anything from a
file.

Everything comes from the core over the `code.*` calls and the `code.changed` push (`src/types.ts`). The core owns every safety rule (paths under the clone, no links followed, `.git` and the deny
list, size limits); this package only shows what the core sends.

## What is here

| File | What it does |
| --- | --- |
| `src/CodeBrowser.vue` | The browser: breadcrumb, back and forward, the folder listing, the preview, the live updates, Follow edits, Download. |
| `src/FileList.vue` | One folder's listing: folders first, size, "edited 2 min ago", the pulsing, solid and hollow marker dots. |
| `src/CodeLines.vue` | A file's lines: numbers, highlighted text, fading recent-change highlights, branch gutter marks, removed-line markers. |
| `src/highlight.ts` | highlight.js core with 25 common languages; splits the escaped HTML into lines. |
| `src/markers.ts` | Pure rules: marker strength, highlight fading, Next / Previous stops, sizes and ages. |
| `src/history.ts` | Back and forward inside the dialog. |
| `src/types.ts` | The contract's shapes, and `CodeBrowserApi` (`call` and `onEvent`, which the shell's `window.yawble` already is). |

## Using it

```vue
<CodeBrowser :api="api" team="payments" member="DeveloperAda" :render-markdown="renderMarkdown"
  :loading-note="LoadingNote" :busy-label="BusyLabel" />
```

- `renderMarkdown` must be safe: the shell passes its markdown-it setup with `html: false`.
- The host owns the dialog around it (Escape, the backdrop, one dialog at a time). The shell's is
  `shell/src/renderer/src/components/CodeDialog.vue`.
- `now`, `tickMs` and `save` exist for tests (a fixed clock, no timer, a fake download).

## How the shell builds it

The shell imports `src/` directly as `@yawble/code-browser`. There is no build step and no npm workspace:

- `shell/vite.config.mts`: an alias to `../code-browser/src/index.ts`, and `resolve.dedupe` for `vue` and
  `highlight.js`, so they always come from `shell/node_modules` (one Vue in the page), even when this folder has its
  own `node_modules` for its tests.
- `shell/tsconfig.renderer.json`: the same alias in `paths`, `vue` and `highlight.js` mapped to `shell/node_modules`,
  and this folder's `src/` in `include`, so `vue-tsc` checks it with the shell.
- `shell/package.json` has `highlight.js` (the same exact version as `peerDependencies` here).
- A renderer-only build needs `shell/` and `code-browser/` side by side, as in the checkout.

## Tests

```
cd code-browser
npm ci
npm test          # vitest, happy-dom, a fake core only
npm run typecheck # vue-tsc
```

The shell's own tests of the card icon, the dialog and the Cloud Link setting are in
`shell/tests/unit/code-browser.test.ts`.

## Why highlight.js

- It is synchronous and small when only its core and chosen languages are imported (the whole feature adds about
  50 kB gzipped to the screens).
- It needs no WebAssembly, no worker and no theme files at run time, so it works in the desktop window and the cloud
  viewer under their strict Content-Security-Policy.
- It escapes the text it highlights, so its output is only its own `<span class="hljs-...">` markup.
- Shiki would give richer colours, but its grammars and themes are several times larger, it loads asynchronously, and
  it needs its Oniguruma WebAssembly or a JavaScript regex engine.
