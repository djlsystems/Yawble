import MarkdownIt from 'markdown-it';

// Agent-written markdown, rendered inside a window that can reach the app's API: raw HTML is NOT allowed (html: false
// escapes it), markdown-it's own link validation refuses javascript:, vbscript:, file: and most data: URLs, and links
// open outside the page (the desktop's main process opens only http, https and mailto in the person's browser).
// No typographer (smart quotes would corrupt commands the person copies).
//
// Linkify turns text that looks like a web address into a link: anything with http:// or https://, www.-addresses, and
// bare domains ending in a common web TLD. The TLD list is short on purpose: the full list has country codes that
// are also file extensions (.md, .py, .rs, .sh), and "README.md" must stay text. Text inside code is never linked.
const md = new MarkdownIt({ html: false, linkify: true, typographer: false, breaks: true });
md.linkify
  .set({ fuzzyEmail: false, fuzzyIP: false })
  .tlds(['com', 'org', 'net', 'io', 'dev', 'ai', 'app', 'gov', 'edu', 'cloud', 'info', 'tech', 'blog', 'news', 'me', 'uk', 'us', 'ca'], false);

const renderLink = md.renderer.rules.link_open ?? ((tokens, idx, options, _env, self) => self.renderToken(tokens, idx, options));
md.renderer.rules.link_open = (tokens, idx, options, env, self) => {
  tokens[idx].attrSet('target', '_blank');
  tokens[idx].attrSet('rel', 'noopener noreferrer');
  return renderLink(tokens, idx, options, env, self);
};

// Code blocks (commands, console output, files) get a copy button. The button is our own markup around markdown-it's
// escaped <pre>; ChatMessage handles its click (copyCodeFrom) and copies the block's text, not its HTML.
const COPY_ICON = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M9 9h11v11H9Z M5 15H4V4h11v1"/></svg>';
const DONE_ICON = '<svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path d="M5 12l5 5L20 7"/></svg>';
for (const rule of ['fence', 'code_block'] as const) {
  const inner = md.renderer.rules[rule]!;
  md.renderer.rules[rule] = (tokens, idx, options, env, self) => {
    const info = rule === 'fence' ? md.utils.escapeHtml(tokens[idx].info.trim().split(/\s+/)[0] ?? '') : '';
    return `<div class="codeblock">`
      + `<div class="codebar"><span class="lang">${info}</span>`
      + `<button type="button" class="copy" data-copy data-test="copy-code" title="Copy" aria-label="Copy">`
      + `<span class="i-copy">${COPY_ICON}</span><span class="i-done">${DONE_ICON}</span><span class="copy-label">Copy</span></button></div>`
      + inner(tokens, idx, options, env, self)
      + `</div>`;
  };
}

export function renderMarkdown(text: string): string {
  return md.render(text);
}

/** Copies text, with a fallback for pages where navigator.clipboard is not available (a non-secure http origin). */
export async function copyText(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(text);
      return true;
    }
  } catch { /* fall back below */ }
  try {
    const area = document.createElement('textarea');
    area.value = text;
    area.setAttribute('readonly', '');
    area.style.position = 'fixed';
    area.style.opacity = '0';
    document.body.appendChild(area);
    area.select();
    const ok = document.execCommand('copy');
    area.remove();
    return ok;
  } catch {
    return false;
  }
}

/** Handles a click inside rendered markdown: if it was a code block's copy button, copies that block's text. */
export async function copyCodeFrom(event: MouseEvent): Promise<void> {
  const button = (event.target as Element | null)?.closest?.('[data-copy]') as HTMLElement | null;
  if (!button) return;
  event.preventDefault();
  const code = button.closest('.codeblock')?.querySelector('pre code, pre');
  // Copy without the trailing newline markdown-it keeps, so a pasted command does not run on its own.
  const text = (code?.textContent ?? '').replace(/\n$/, '');
  const ok = await copyText(text);
  button.classList.toggle('copied', ok);
  const label = button.querySelector('.copy-label');
  if (label) label.textContent = ok ? 'Copied' : 'Copy failed';
  setTimeout(() => {
    button.classList.remove('copied');
    if (label) label.textContent = 'Copy';
  }, 1600);
}
