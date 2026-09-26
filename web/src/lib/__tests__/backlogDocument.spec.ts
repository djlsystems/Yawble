import { describe, expect, it } from 'vitest'
import { itemFromDocument, MAXIMUM_DOCUMENT_BYTES } from '../backlog'
import { MAXIMUM_BACKLOG_TITLE_LENGTH } from '../rules'

// A DOCUMENT BECOMES AN ITEM: the spec's heading is the title and the spec, in full, is the body.
// The person checks both in the form before anything is saved, so this only has to make a good
// first draft and refuse what is not a text document.
describe('itemFromDocument', () => {
  it('takes the title from the first level-one heading and keeps the whole text as the body', () => {
    const text = '# Be able to watch headless output\n\n**Status: PROPOSAL.**\n\n## Problem\n\nA member...\n'

    const draft = itemFromDocument('2026-09-25-watch.md', text)

    expect(draft).toEqual({ title: 'Be able to watch headless output', body: text })
  })

  it('skips a heading inside a fenced code block', () => {
    const text = '```sh\n# not a title\n```\n\n# The real title\n'

    expect(itemFromDocument('x.md', text)).toMatchObject({ title: 'The real title' })
  })

  it('uses the file name, without its extension, when there is no level-one heading', () => {
    expect(itemFromDocument('notes-for-the-team.txt', '## Only a subheading\ntext')).toMatchObject({
      title: 'notes-for-the-team',
    })
  })

  it('writes Windows line endings and a byte-order mark as the board stores text', () => {
    const draft = itemFromDocument('a.md', '﻿# Title\r\n\r\nLine one\r\nLine two\r\n')

    expect(draft).toEqual({ title: 'Title', body: '# Title\n\nLine one\nLine two\n' })
  })

  it('cuts a title longer than the backlog allows, leaving the body whole', () => {
    const heading = 'x'.repeat(MAXIMUM_BACKLOG_TITLE_LENGTH + 50)

    const draft = itemFromDocument('a.md', `# ${heading}\n`)

    expect('title' in draft && draft.title.length).toBe(MAXIMUM_BACKLOG_TITLE_LENGTH)
    expect('body' in draft && draft.body).toBe(`# ${heading}\n`)
  })

  it('refuses a file that is not text, naming it', () => {
    const draft = itemFromDocument('diagram.pdf', '%PDF-1.7\u0000\u0001binary')

    expect(draft).toEqual({ error: 'diagram.pdf is not a text document. Upload a Markdown or plain-text file.' })
  })

  it('refuses an empty file', () => {
    expect(itemFromDocument('empty.md', '  \n')).toEqual({ error: 'empty.md is empty.' })
  })

  it('names the size limit', () => {
    expect(MAXIMUM_DOCUMENT_BYTES).toBe(1024 * 1024)
  })
})
