// First: the theme (data-theme on <html>) is set before any style is applied or anything is mounted, so the window
// never shows the other theme first.
import './theme';
import { createApp } from 'vue';
import '@xterm/xterm/css/xterm.css';
import './styles.css';
import './terminal-theme.css';
import App from './App.vue';
import TerminalApp from './TerminalApp.vue';

// One page, two kinds of window: the main window, and an agent's terminal window (#terminal/<memberId>).
const terminal = /^#terminal\/(\d+)$/.exec(window.location.hash);
if (terminal) createApp(TerminalApp, { memberId: Number(terminal[1]) }).mount('#app');
else createApp(App).mount('#app');
