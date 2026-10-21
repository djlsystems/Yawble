// Configuration for your app
// https://v2.quasar.dev/quasar-cli-vite/quasar-config-file

import { defineConfig } from '#q-app';
import { describeDevProxyTarget, resolveDevProxyTarget } from './src/lib/devProxyTarget';
import { buildInfo } from '../scripts/version.mjs';
import { monitorSymbols } from './scripts/monitor-symbols.mjs';

// Which Host this dev server proxies to: HARNESS_URL, minted per container, names the exact Host
// the tree belongs to, and the line printed below keeps the answer visible rather than assumed.
//
// THE DECISION LIVES IN `src/lib/devProxyTarget.ts` SO THAT IT CAN BE TESTED - this file is loaded
// by the Quasar CLI and cannot be driven from a spec. What stays here is the side effects: reading
// the real environment, printing, and failing the process.
const target = resolveDevProxyTarget(process.env);

export default defineConfig((ctx) => {
  // Only in dev, and only because the proxy exists only in dev - a production build serves the
  // bundle FROM the Host, same-origin, and printing a proxy target there would name a host nothing
  // is talking to.
  //
  // The line names the SOURCE as well as the target, so a wrong instance is told from a right one.
  if (ctx.dev) {
    console.log(describeDevProxyTarget(target));
  }

  return {
    // https://v2.quasar.dev/quasar-cli-vite/prefetch-feature
    // preFetch: true,

    // app boot file (/src/boot)
    // --> boot files are part of "main.js"
    // https://v2.quasar.dev/quasar-cli-vite/boot-files
    boot: [
      'icons',
      'numberWheel',
    ],

    // https://v2.quasar.dev/quasar-cli-vite/quasar-config-file#css
    css: [
      // The two families, SELF-HOSTED through @fontsource rather than linked from Google. This app
      // is local-first: it runs on localhost, its agents are child processes, and its whole premise
      // is that nothing has to leave the machine. A font CDN would mean no typeface when the network
      // is down and a request to Google on every load of a tool that otherwise never phones home.
      //
      // Only the weights actually used. Each one is a file the browser fetches, and shipping the
      // full 100-700 range of two families to render four weights is nine downloads nobody asked
      // for.
      '~@fontsource/ibm-plex-sans/400.css',
      '~@fontsource/ibm-plex-sans/500.css',
      '~@fontsource/ibm-plex-sans/600.css',
      '~@fontsource/ibm-plex-mono/400.css',
      '~@fontsource/ibm-plex-mono/500.css',
      // A face a person can choose in Themes, designed for low vision. Declared only: the browser
      // fetches it when somebody picks it, never for anyone else.
      '~@fontsource/atkinson-hyperlegible/400.css',
      '~@fontsource/atkinson-hyperlegible/700.css',
      // The icon font: a subset of Material Symbols Outlined, in place of the `extras` entry below.
      'material-symbols.scss',
      'app.scss',
      // The type scale and the token utility classes. After `app.scss` because it reads its tokens.
      'type.scss',
      // The dialog scale: one class per dialog card. Its own file; the tokens it reads are app.scss's.
      'dialog-scale.scss',
      // The tile grid Agents, Plugins and Documents share.
      'tiles.scss',
      // The Documents explorer's selection and drop states, shared by its views, tree and crumbs.
      'documents.scss'
    ],

    // https://github.com/quasarframework/quasar/tree/dev/extras
    extras: [
      // 'ionicons-v4',
      // 'mdi-v7',
      // 'fontawesome-v7',
      // 'eva-icons',
      // 'themify',
      // 'line-awesome',
      // 'roboto-font-latin-ext', // this or either 'roboto-font', NEVER both!

      // Roboto is not loaded - the faces come from @fontsource above. Icons are Material SYMBOLS
      // Outlined and nothing else: `boot/icons.ts` maps every bare name to `sym_o_<name>`. The
      // filled Material Icons font is not loaded, so an Icons-only name (`error_outline`,
      // `help_outline`) renders as its own letters at the wrong width with nothing logged -
      // `icon-names.spec.ts` checks every literal name against the Symbols list.
      //
      // Symbols is NOT listed here: this entry would serve the full ~4 MB font. `css` above loads a
      // subset of it instead - see `css/material-symbols.scss`.
    ],

    // https://v2.quasar.dev/quasar-cli-vite/quasar-config-file#build
    build: {
      target: {
        // browser: 'baseline-widely-available',
        // node: 'node22'
      },

      typescript: {
        strict: true,
        vueShim: true
        // extendTsConfig (tsConfig) {}
      },

      // https://v2.quasar.dev/quasar-cli-vite/page-routing-with-vue-router#filename-based-routing
      // filenameBasedRouting: true,

      vueRouterMode: 'hash', // available values: 'hash', 'history'
      // vueRouterBase,

      // publicPath: '/',

      // WHICH BUILD THIS IS (B000N), from the git tag through `scripts/version.mjs` - the rule the
      // .NET build asks too - or from HARNESS_VERSION / HARNESS_COMMIT when the container build
      // hands them in. `package.json` keeps a placeholder: npm accepts only three-part semver.
      // Read by `src/lib/buildInfo.ts`.
      define: {
        __HARNESS_BUILD__: JSON.stringify(buildInfo()),
      },
      // defineEnv: {}
      // ignorePublicFolder: true,
      // minify: false,
      // distDir

      // extendViteConf (viteConf) {},
      // viteVuePluginOptions: {},

      // THE MONITOR'S FULL ICON FONT and its name -> codepoint map, as `virtual:monitor-symbols`:
      // its own asset, fetched only when the Monitor first opens. See the file.
      vitePlugins: [monitorSymbols()],
    },

    // https://v2.quasar.dev/quasar-cli-vite/quasar-config-file#devserver
    devServer: {
      // vueDevtools: true,
      // https: true,
      open: false, // the agent starts this headlessly; opening a window is noise

      // Dev mode is the SPA on its own port talking to a real Host - the one named by
      // HARNESS_URL, or by HARNESS_PORT, or the built-in fallback, in that order
      // and printed on start. Production is the built bundle served BY the Host from
      // wwwroot, so these paths are same-origin there and the proxy does not apply.
      proxy: {
        // ws is on BOTH, and for different reasons. The console's PTY socket is at
        // /api/teams/{team}/concierge/ws - a raw WebSocket living under /api - so a
        // proxy without ws here would leave the console dead in dev while every
        // plain fetch kept working, which reads as a console bug rather than a
        // proxy one.
        '/api': { target: target.host, changeOrigin: true, ws: true },

        // And SignalR negotiates up to a WebSocket, so the hub needs it too.
        '/hub': { target: target.host, changeOrigin: true, ws: true }
      }
    },

    // https://v2.quasar.dev/quasar-cli-vite/quasar-config-file#framework
    framework: {
      config: {},

      // Quasar's own glyphs (select arrows, expansion chevrons) in the same font as ours.
      iconSet: 'material-symbols-outlined',
      // lang: 'en-US', // Quasar language pack

      // For special cases outside of where the auto-import strategy can have an impact
      // (like functional components as one of the examples),
      // you can manually specify Quasar components/directives to be available everywhere:
      //
      // components: [],
      // directives: [],

      // Quasar plugins
      //
      // Notify must be listed here or $q.notify is undefined at runtime while the
      // types still resolve - a failure that compiles cleanly and only shows up
      // when something goes wrong, which is exactly when the message matters.
      //
      // Dialog is deliberately NOT here. The Interactive Agent's reload confirmation
      // is a q-dialog in the panel's own template instead, because $q.dialog()'s
      // `class` option lands on the inner CARD and not on the dialog root - so there
      // is no way to lift it above the panel's z-index of 7000, and the confirmation
      // for a destructive action rendered behind the terminal.
      //
      // Dark is here for the same reason Notify is: unregistered, `Dark.set` and `$q.dark` still
      // type-check and do nothing, so the theme toggle would be a menu item that changes nothing.
      // The viewer's choice is applied by App.vue, which watches `theme` on `stores/display.ts`.
      plugins: ['Notify', 'Dark']
    },

    // animations: 'all', // --- includes all animations
    // https://v2.quasar.dev/options/animations
    animations: [],

    // https://v2.quasar.dev/quasar-cli-vite/quasar-config-file#sourcefiles
    // sourceFiles: {
    //   rootComponent: 'src/App.vue',
    //   router: 'src/router/index',
    //   store: 'src/store/index',
    //   pwaRegisterServiceWorker: 'src-pwa/register-sw',
    //   pwaServiceWorker: 'src-pwa/sw/custom-sw',
    //   pwaManifestFile: 'src-pwa/manifest.json',
    //   electronMain: 'src-electron/electron-main',
    //   electronPreload: 'src-electron/electron-preload'
    //   bexManifestFile: 'src-bex/manifest.json
    // },

    // https://v2.quasar.dev/quasar-cli-vite/developing-ssr/configuring-ssr
    ssr: {
      /**
       * The default port that the production server should use
       * (gets superseded if process.env.PORT is specified at runtime)
       */
      prodPort: 3000,
      middlewares: [
        'render' // keep this as last one
      ],

      // clientSideRenderingRoutes: [],
      // noPreloadTagRoutes: [],
      // manualStoreSerialization: true,
      // manualStoreSsrContextInjection: true,
      // manualStoreHydration: true,
      // manualPostHydrationTrigger: true,
      // prodScriptNamedExport: false,

      // extendSSRPackageJson (pkgJson) {},
      // extendSSRManifestJson (json) {},
      // extendSSRWebserverConf (rolldownConf) {},

      // pwa: true,
      // pwaOfflineHtmlFilename: 'offline.html', // do NOT use index.html as name!
      // extendSSRGenerateSWOptions (cfg) {},
      // extendSSRInjectManifestOptions (cfg) {},
    },

    // https://v2.quasar.dev/quasar-cli-vite/developing-ssg/configuring-ssg
    ssg: {
      // onSsgRendererError: 'abort',
      // ssgRendererConcurrency: 1,
      // ssgRendererRetryCount: 0,
      // ssgRendererRetryDelay: 1000,
      // ssgRendererDirectoryIndexes: true,
      // error404HtmlFilename: '404.html',
      // clientSideRenderingHtmlFilename: 'csr.html',
      // clientSideRenderingRoutes: [],
      // noPreloadTagRoutes: []

      // extendSSGRendererConf (rolldownConf) {},
      // extendSSGManifestJson (json) {},

      // manualStoreSerialization: true,
      // manualStoreSsrContextInjection: true,
      // manualStoreHydration: true,
      // manualPostHydrationTrigger: true,

      // pwa: true,
      // pwaOfflineHtmlFilename: 'offline.html',
      // extendSSGGenerateSWOptions (cfg) {},
      // extendSSGInjectManifestOptions (cfg) {},
    },

    // https://v2.quasar.dev/quasar-cli-vite/developing-pwa/configuring-pwa
    pwa: {
      workboxMode: 'GenerateSW' // 'GenerateSW' or 'InjectManifest'
      // swFilename: 'sw.js',
      // manifestFilename: 'manifest.json',
      // extendPWAManifestJson (json) {},
      // useCredentialsForManifestTag: true,
      // injectPWAMetaTags: false,
      // extendPWACustomSWConf (rolldownConf) {},
      // extendPWAGenerateSWOptions (cfg) {},
      // extendPWAInjectManifestOptions (cfg) {},
      // extendPWASwTsConfig (tsConfig) {}
    },

    // https://v2.quasar.dev/quasar-cli-vite/developing-cordova-apps/configuring-cordova
    cordova: {},

    // https://v2.quasar.dev/quasar-cli-vite/developing-capacitor-apps/configuring-capacitor
    capacitor: {
      hideSplashscreen: true
    },

    // https://v2.quasar.dev/quasar-cli-vite/developing-electron-apps/configuring-electron
    electron: {
      // extendElectronMainConf (rolldownConf) {},
      // extendElectronPreloadConf (rolldownConf) {},
      // extendElectronPackageJson (pkgJson) {},

      // Electron preload scripts (if any) from /src-electron, WITHOUT file extension
      preloadScripts: [ 'electron-preload' ],

      // specify the debugging port to use for the Electron app when running in development mode
      inspectPort: 5858,

      bundler: 'packager', // 'packager' or 'builder'

      packager: {
        // https://github.com/electron-userland/electron-packager/blob/master/docs/api.md#options

        // OS X / Mac App Store
        // appBundleId: '',
        // appCategoryType: '',
        // osxSign: '',
        // protocol: 'myapp://path',

        // Windows only
        // win32metadata: { ... }
      },

      builder: {
        // https://www.electron.build/configuration

        appId: 'harness-web'
      }
    },

    // https://v2.quasar.dev/quasar-cli-vite/developing-browser-extensions/configuring-bex
    bex: {
      // extendBexScriptsConf (rolldownConf) {},
      // extendBexManifestJson (json) {},

      /**
       * The list of extra scripts (js/ts) not in your bex manifest that you want to
       * compile and use in your browser extension. Maybe dynamic use them?
       *
       * Each entry in the list should be a relative filename to /src-bex/
       *
       * @example [ 'my-script.ts', 'sub-folder/my-other-script.js' ]
       */
      extraScripts: []
    }
  }
});
