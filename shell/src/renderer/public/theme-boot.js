// Sets data-theme on <html> before the stylesheet paints anything, so a dark choice (or a dark OS) never shows a white
// first frame. A classic script from 'self' (the CSP allows it; an inline one it does not), run before the page's
// stylesheet and its deferred module script. The page's key is the script tag's data-key: yawble.theme for the
// desktop window, yawble.remoteTheme for a remote view. theme.ts (createTheme) then takes over with the same rule.
// The remote view embedded in the cloud page (?embed=1) never reads the kept choice: the cloud's ?theme=light|dark
// is shown, else the OS's (theme.ts pageThemeEnv, the same rule).
(function () {
  var root = document.documentElement;
  var theme = 'light';
  try {
    var script = document.currentScript;
    var key = (script && script.getAttribute('data-key')) || 'yawble.theme';
    var embed = false;
    var given = null;
    try {
      var q = new URLSearchParams(window.location.search);
      embed = q.get('embed') === '1';
      given = q.get('theme');
    } catch { embed = false; } // no location: not embedded
    var kept = null;
    if (embed) kept = given;
    else {
      try { kept = window.localStorage.getItem(key); } catch { kept = null; } // storage blocked: follow the OS
    }
    if (kept === 'light' || kept === 'dark') theme = kept;
    else if (typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches) theme = 'dark';
  } catch { theme = 'light'; }
  try {
    root.setAttribute('data-theme', theme);
    root.style.colorScheme = theme;
  } catch { /* leave it to theme.ts */ }
})();
