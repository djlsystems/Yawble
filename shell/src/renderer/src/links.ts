// Plain text with its web addresses as links: the parts in order, each either text or an https/http address. A sentence's
// closing punctuation after an address stays text. Rendered with <a target="_blank">, which the window opens in the
// person's browser (only web and mail addresses; shell/src/main/index.ts).

export interface TextPart { text: string; href?: string }

const URL = /https?:\/\/[^\s<>"')]+/g;

export function linkParts(text: string): TextPart[] {
  const parts: TextPart[] = [];
  let at = 0;
  for (const m of text.matchAll(URL)) {
    let url = m[0];
    while (/[.,;:!?]$/.test(url)) url = url.slice(0, -1);
    const start = m.index ?? 0;
    if (start > at) parts.push({ text: text.slice(at, start) });
    parts.push({ text: url, href: url });
    at = start + url.length;
  }
  if (at < text.length) parts.push({ text: text.slice(at) });
  return parts;
}
