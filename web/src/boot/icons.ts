import { defineBoot } from '#q-app'
import { symbolsIconMap } from '../lib/icons'
import { markIconsReadyWhenLoaded } from '../lib/iconsReady'

/**
 * Installs the Symbols Outlined map for every `q-icon`; the reason is in `lib/icons.ts`. Also
 * unhides icons once their font has loaded; the reason is in `lib/iconsReady.ts`.
 */
export default defineBoot(({ app }) => {
  app.config.globalProperties.$q.iconMapFn = symbolsIconMap
  void markIconsReadyWhenLoaded(typeof document !== 'undefined' ? document.fonts : undefined, 15_000)
})
