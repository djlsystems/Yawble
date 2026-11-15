import { createRemoteApi } from './remoteApi';
import { loadRemoteAccess } from './remoteAccess';

// The remote view's entry: the window API over a WebSocket instead of Electron's preload bridge, then the very same
// screens as the desktop window (main.ts picks the main window or an agent's terminal window by the hash). main.ts
// loads the theme first; with window.yawble.remote set, it keeps this viewer's own choice (theme.ts REMOTE_THEME_KEY).
// The viewer's access (full, or read for a Visitor) comes from core.getInfo on each connect (the socket opens after
// this listener is added): the screens mount at once and the chip and disabled buttons follow (remoteAccess.ts).
const api = createRemoteApi();
window.yawble = api;
api.onEvent((e) => { if (e.event === 'remote.connected') void loadRemoteAccess(api.getInfo); });
void import('./main');
