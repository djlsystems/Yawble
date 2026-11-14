// Sets data-theme on <html> before the stylesheet paints anything, so a dark choice (or a dark OS) never shows a white
// first frame. A classic script from 'self' (the CSP allows it; an inline one it does not), run before the page's
// stylesheet and its deferred module script. The page's key is the script tag's data-key: yawble.theme for the
// desktop window, yawble.remoteTheme for a remote view. theme.ts (createTheme) then takes over with the same rule.
(function () {
  var root = document.documentElement;
  var theme = 'light';
  try {
    var script = document.currentScript;
    var key = (script && script.getAttribute('data-key')) || 'yawble.theme';
    var kept = null;
    try { kept = window.localStorage.getItem(key); } catch (e) { kept = null; } // storage blocked: follow the OS
    if (kept === 'light' || kept === 'dark') theme = kept;
    else if (typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches) theme = 'dark';
  } catch (e) { theme = 'light'; }
  try {
    root.setAttribute('data-theme', theme);
    root.style.colorScheme = theme;
  } catch (e) { /* leave it to theme.ts */ }
})();