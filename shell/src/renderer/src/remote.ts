import { createRemoteApi } from './remoteApi';

// The remote view's entry: the window API over a WebSocket instead of Electron's preload bridge, then the very same
// screens as the desktop window (main.ts picks the main window or an agent's terminal window by the hash).
window.yawble = createRemoteApi();
void import('./main');
