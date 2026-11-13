// Agent-written markdown may name an image anywhere: a local file (C:/Users/..., file:), a UNC share
// (\\server\share\x.png, which Windows would fetch over SMB and hand the person's credentials to), or a web address
// (a tracking pixel). In the Code browser none of them is loaded: every image becomes a plain, link-free placeholder
// with its alt text, and anything else that could fetch (picture, video, audio, embeds, srcset, poster) is dropped.
// The host's renderer already keeps raw HTML out; this holds whatever renderer the host passes.

// (Inline SVG stays: the host's own icons, such as a code block's copy button, are SVG; what in SVG can fetch goes.)
const FETCHING = 'picture, source, video, audio, track, iframe, frame, object, embed, image, use, feImage, feimage, link, meta, base, input[type=image]';
const URL_ATTRS = ['src', 'srcset', 'poster', 'background', 'lowsrc', 'dynsrc', 'data', 'xlink:href', 'formaction', 'ping'];

/** The rendered markdown with no image and nothing else that would load a resource. */
export function withoutImages(html: string): string {
  if (!html) return html;
  // A <template>'s content is inert: parsing it loads nothing.
  const t = document.createElement('template');
  t.innerHTML = html;
  for (const img of Array.from(t.content.querySelectorAll('img'))) {
    const alt = (img.getAttribute('alt') ?? '').trim();
    const note = document.createElement('span');
    note.className = 'cb-img-alt';
    note.setAttribute('data-test', 'code-md-image');
    note.textContent = alt ? `[image: ${alt}]` : '[image]';
    img.replaceWith(note);
  }
  for (const el of Array.from(t.content.querySelectorAll(FETCHING))) el.remove();
  for (const el of Array.from(t.content.querySelectorAll('*'))) {
    for (const name of URL_ATTRS) el.removeAttribute(name);
    // An inline style could fetch (background: url(...)): markdown never makes one, so none is kept.
    el.removeAttribute('style');
  }
  return t.innerHTML;
}
