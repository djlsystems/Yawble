import MarkdownIt from 'markdown-it';
import { commitParts, isCommitId } from './commitView';

// Agent-written markdown, rendered inside a window that can reach the app's API: raw HTML is escaped (html: false),
// markdown-it refuses javascript:, vbscript:, file: and most data: URLs, and links open outside the page. No
// typographer (smart quotes would corrupt commands the person copies). Linkify uses a short TLD list on purpose: the
// full list has country codes that are also file extensions (.md, .py, .rs, .sh), and "README.md" must stay text.
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

// An image is never loaded. Agent-written text may name a file on this computer (C:/Users/...), a share
// (\\server\share\x.png, which Windows fetches over SMB, handing the person's sign-in to that server) or a web address
// (a tracking pixel). Every image, inline or by reference, renders as its escaped alt text in a plain span: no <img>,
// no address. The Code browser's own filter (safeHtml.ts withoutImages) shows the same text for any other renderer.
md.renderer.rules.image = (tokens, idx, options, env, self) => {
  const alt = self.renderInlineAsText(tokens[idx].children ?? [], options, env).trim();
  return `<span class="md-image-alt" data-test="md-image">${md.utils.escapeHtml(alt ? `[image: ${alt}]` : '[image]')}</span>`;
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

// A commit ID in the text (or alone in inline code) opens the Commit view, but only once the core confirms it is
// a commit. It renders as a plain-text candidate, our own markup around the escaped ID (never a web link):
// <span data-commit-id="<id>">; the document's observer (commitResolve.ts installCommitResolve, App.vue) asks
// git.commitsResolve for every candidate on the screen in one call and turns a confirmed one into a [data-commit] link,
// which App.vue's click handler opens (installCommitLinks). Not a commit: it stays text.
// IDs inside a link, a code block or a longer inline code stay as they are.
const commitAnchor = (sha: string, inner: string) =>
  `<span class="commit-id" data-commit-id="${sha}">${inner}</span>`;
md.core.ruler.push('commit_ids', (state) => {
  for (const block of state.tokens) {
    if (block.type !== 'inline' || !block.children) continue;
    const out: typeof block.children = [];
    let inLink = 0;
    for (const t of block.children) {
      if (t.type === 'link_open') inLink++;
      if (t.type === 'link_close') inLink--;
      if (!inLink && t.type === 'code_inline' && isCommitId(t.content.trim()) && t.content.trim() === t.content.trim().toLowerCase()) {
        const sha = t.content.trim();
        const tok = new state.Token('html_inline', '', 0);
        tok.content = commitAnchor(sha, `<code>${sha}</code>`);
        out.push(tok);
        continue;
      }
      if (inLink || t.type !== 'text') { out.push(t); continue; }
      const parts = commitParts(t.content);
      if (!parts.some((p) => p.sha)) { out.push(t); continue; }
      for (const p of parts) {
        const tok = new state.Token(p.sha ? 'html_inline' : 'text', '', 0);
        tok.content = p.sha ? commitAnchor(p.sha, p.sha) : p.text;
        out.push(tok);
      }
    }
    block.children = out;
  }
});

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
