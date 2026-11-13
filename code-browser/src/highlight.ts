// Syntax highlighting: highlight.js's core with the common languages only (each is a few kB; the full bundle is ~1 MB).
// highlight.js escapes the text it is given, so its output holds only its own <span class="hljs-..."> markup; plain
// text that no language claims is escaped here. Nothing from a file is ever inserted as HTML any other way.
import hljs from 'highlight.js/lib/core';
import bash from 'highlight.js/lib/languages/bash';
import c from 'highlight.js/lib/languages/c';
import cpp from 'highlight.js/lib/languages/cpp';
import csharp from 'highlight.js/lib/languages/csharp';
import css from 'highlight.js/lib/languages/css';
import diff from 'highlight.js/lib/languages/diff';
import dockerfile from 'highlight.js/lib/languages/dockerfile';
import go from 'highlight.js/lib/languages/go';
import ini from 'highlight.js/lib/languages/ini';
import java from 'highlight.js/lib/languages/java';
import javascript from 'highlight.js/lib/languages/javascript';
import json from 'highlight.js/lib/languages/json';
import kotlin from 'highlight.js/lib/languages/kotlin';
import markdown from 'highlight.js/lib/languages/markdown';
import php from 'highlight.js/lib/languages/php';
import powershell from 'highlight.js/lib/languages/powershell';
import python from 'highlight.js/lib/languages/python';
import ruby from 'highlight.js/lib/languages/ruby';
import rust from 'highlight.js/lib/languages/rust';
import scss from 'highlight.js/lib/languages/scss';
import sql from 'highlight.js/lib/languages/sql';
import swift from 'highlight.js/lib/languages/swift';
import typescript from 'highlight.js/lib/languages/typescript';
import xml from 'highlight.js/lib/languages/xml';
import yaml from 'highlight.js/lib/languages/yaml';

const LANGUAGES = {
  bash, c, cpp, csharp, css, diff, dockerfile, go, ini, java, javascript, json, kotlin, markdown, php, powershell, python, ruby, rust, scss,
  sql, swift, typescript, xml, yaml,
};
for (const [name, lang] of Object.entries(LANGUAGES)) hljs.registerLanguage(name, lang);

/** The registered language names (plus 'plaintext', which needs no grammar). */
export const LANGUAGE_NAMES = Object.keys(LANGUAGES);

const BY_EXTENSION: Record<string, string> = {
  sh: 'bash', bash: 'bash', zsh: 'bash',
  c: 'c', h: 'c',
  cpp: 'cpp', cc: 'cpp', cxx: 'cpp', hpp: 'cpp', hh: 'cpp',
  cs: 'csharp', csx: 'csharp',
  css: 'css', scss: 'scss', less: 'scss',
  diff: 'diff', patch: 'diff',
  go: 'go',
  ini: 'ini', toml: 'ini', cfg: 'ini', conf: 'ini', editorconfig: 'ini', gitattributes: 'ini', gitignore: 'ini',
  java: 'java', kt: 'kotlin', kts: 'kotlin',
  js: 'javascript', mjs: 'javascript', cjs: 'javascript', jsx: 'javascript',
  ts: 'typescript', mts: 'typescript', cts: 'typescript', tsx: 'typescript',
  json: 'json', jsonc: 'json', json5: 'json',
  md: 'markdown', markdown: 'markdown',
  php: 'php', ps1: 'powershell', psm1: 'powershell', psd1: 'powershell',
  py: 'python', rb: 'ruby', rs: 'rust', sql: 'sql', swift: 'swift',
  xml: 'xml', html: 'xml', htm: 'xml', svg: 'xml', vue: 'xml', csproj: 'xml', props: 'xml', targets: 'xml', slnx: 'xml', xaml: 'xml', resx: 'xml',
  yml: 'yaml', yaml: 'yaml',
};

/** The language to highlight a file in: the core's hint when it is one we have, else by the file's name; else plaintext. */
export function languageFor(path: string, hint?: string | null, type?: string): string {
  const h = hint?.toLowerCase();
  if (h && hljs.getLanguage(h)) return h; // a name or alias highlight.js knows ("ts", "cs", "html")
  if (type === 'json' || type === 'yaml' || type === 'xml' || type === 'markdown') return type;
  const name = path.split('/').pop()!.toLowerCase();
  if (name === 'dockerfile' || name.endsWith('.dockerfile')) return 'dockerfile';
  if (name === 'makefile') return 'bash';
  const ext = name.includes('.') ? name.slice(name.lastIndexOf('.') + 1) : '';
  return BY_EXTENSION[ext] ?? 'plaintext';
}

export function escapeHtml(text: string): string {
  return text.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

/** The file as one HTML string per line (no trailing newline line), highlighted when the language is known. */
export function highlightLines(text: string, language: string): string[] {
  const body = text.replace(/\r\n?/g, '\n');
  const html = language !== 'plaintext' && hljs.getLanguage(language)
    ? hljs.highlight(body, { language, ignoreIllegals: true }).value
    : escapeHtml(body);
  const lines = splitHtmlLines(html);
  if (lines.length > 1 && lines[lines.length - 1] === '') lines.pop();
  return lines;
}

/**
 * Splits highlighted HTML at newlines. A span may cross lines (a block comment), so each line closes the spans still
 * open at its end and the next line opens them again. Only highlight.js's own <span class="..."> and </span> occur.
 */
export function splitHtmlLines(html: string): string[] {
  const out: string[] = [];
  const open: string[] = [];
  let line = '';
  const re = /(<span[^>]*>)|(<\/span>)|(\n)|([^<\n]+)|(<)/g;
  let m: RegExpExecArray | null;
  while ((m = re.exec(html))) {
    if (m[1]) { open.push(m[1]); line += m[1]; }
    else if (m[2]) { open.pop(); line += m[2]; }
    else if (m[3]) {
      out.push(line + '</span>'.repeat(open.length));
      line = open.join('');
    } else line += m[0];
  }
  out.push(line + '</span>'.repeat(open.length));
  return out;
}
