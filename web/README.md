# Quasar App (harness-web)

## Install the dependencies

```bash
pnpm install
# or: yarn/npm/bun install
```

### Start the app in development mode (HMR, error reporting, etc.)

```bash
quasar dev
```

### Build the app for production

```bash
quasar build
```

### Regenerate the icon font after adding an icon
The app serves a subset of Material Symbols Outlined holding only the icons it uses
(`src/assets/fonts/material-symbols-outlined-subset.woff2`, ~100 KB instead of ~4 MB). After adding
an icon name, or upgrading `@quasar/extras` or `quasar`, rewrite it and commit the result:
```bash
npm run icons:subset
```
`src/lib/__tests__/icon-names.spec.ts` fails, naming the icon, until you do. Which names go in and
why is in `scripts/icon-font.mjs`.

### Customize the configuration

See [Configuring quasar.config.js](https://v2.quasar.dev/quasar-cli-vite/quasar-config-file).
