import type { AppVersion } from '../../shared/api';

// What vite.config.mts put in at build time (scripts/build-version.mjs). The About section shows the core's version
// (app.version); this is its fallback when the core cannot say (an older core, or the call failed).
declare const __YAWBLE_BUILD__: AppVersion | undefined;

export const shellBuild: AppVersion = typeof __YAWBLE_BUILD__ !== 'undefined' && __YAWBLE_BUILD__
  ? __YAWBLE_BUILD__
  : { version: '0.0.0+dev.unknown', commit: 'unknown', dirty: false, released: false };
