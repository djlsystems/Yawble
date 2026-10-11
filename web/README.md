# Yawble web app

The board, the Concierge terminal and every dialog in Yawble: a Vue 3 and Quasar single-page
app. The Host serves the production build; in development it talks to a running Host.

```sh
npm ci              # install the dependencies
npm run dev         # dev server with hot reload (quasar dev)
npm test            # the unit and component tests (vitest)
npm run typecheck   # vue-tsc
npm run build       # production build (quasar build)
```

The brand appears in `src/presentation/product.ts` and the `productName` in `package.json`, and
nowhere else; `BrandLeakTests` in the .NET suite checks that. How the app fits with the Host:
[../docs/architecture.md](../docs/architecture.md).
