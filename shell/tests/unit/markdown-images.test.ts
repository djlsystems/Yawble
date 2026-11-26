import { describe, expect, it } from 'vitest';
import { renderMarkdown } from '../../src/renderer/src/markdown';

// Agent-written markdown never becomes an image the window would fetch: every image is its alt text.
describe('images in rendered markdown', () => {
  const cases: [string, string][] = [
    ['a scheme-relative address (a share, on a file page)', '![x](//host/share/x.png)'],
    ['a UNC share', String.raw`![x](\\host\share\x.png)`],
    ['a local file', '![x](C:/a.png)'],
    ['a web address', '![x](https://example.com/a.png)'],
    ['a reference-style image', '![x][pic]\n\n[pic]: //host/share/x.png "t"'],
    ['a collapsed reference', '![x][]\n\n[x]: https://example.com/a.png'],
    ['an image inside a link', '[![x](//host/share/x.png)](https://example.com)'],
    ['an image with a title', '![x](https://example.com/a.png "title")'],
  ];
  for (const [name, text] of cases) {
    it(`${name} renders as its alt text, with no <img> and no address to fetch`, () => {
      const html = renderMarkdown(text);
      expect(html).not.toContain('<img');
      expect(html).not.toMatch(/\bsrc(set)?=/);
      expect(html).toContain('<span class="md-image-alt" data-test="md-image">[image: x]</span>');
      expect(html).not.toContain('x.png');
      expect(html).not.toContain('a.png');
    });
  }

  it('the alt text is escaped, and an image without one is "[image]"', () => {
    const html = renderMarkdown('![<b onclick="x">&"quoted"](//h/s.png) and ![](//h/t.png)');
    expect(html).not.toContain('<img');
    expect(html).not.toContain('<b');
    expect(html).toContain('[image: &lt;b onclick=&quot;x&quot;&gt;&amp;&quot;quoted&quot;]');
    expect(html).toContain('<span class="md-image-alt" data-test="md-image">[image]</span>');
  });

  it('raw image HTML in the text stays escaped text', () => {
    const html = renderMarkdown('<img src="//host/share/x.png">');
    expect(html).not.toContain('<img');
    expect(html).toContain('&lt;img');
  });
});
