import { createRemoteApi } from './remoteApi';

// The remote view's entry: the window API over a WebSocket instead of Electron's preload bridge, then the very same
// screens as the desktop window (main.ts picks the main window or an agent's terminal window by the hash). main.ts
// loads the theme first; with window.yawble.remote set, it keeps this viewer's own choice (theme.ts REMOTE_THEME_KEY).
window.yawble = createRemoteApi();
void import('./main');
