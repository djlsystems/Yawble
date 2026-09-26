import { describe, expect, it } from 'vitest'
import { choicesFor, requestFrom, wouldDoSomething, type ResetChoices } from '../reset'

const choices = (over: Partial<ResetChoices> = {}): ResetChoices => ({
  ...choicesFor(['Manager', 'Digger']),
  members: { Manager: true, Digger: false },
  ...over,
})

describe('the reset request a dialog builds', () => {
  it('opens with every member ticked and memory being deleted', () => {
    // What a reset MEANS, so it is the state the dialog opens in - anything narrower makes the
    // common case a sequence of clicks, and makes the default reset a no-op.
    const fresh = choicesFor(['Manager', 'Digger'])

    expect(fresh.members).toEqual({ Manager: true, Digger: true })
    expect(fresh.deleteMemory).toBe(true)
    expect(fresh.clearWorkspaces).toBe(false)
  })

  it('sends only the ticked members', () => {
    expect(requestFrom(choices()).members).toEqual(['Manager'])
  })

  it('fans "delete memory" out to the three flags the server understands', () => {
    // ONE control over three server flags. `forgetHistory` is LEFT OUT rather than sent as true:
    // absent already means true there, and sending the redundant one invites a reader to think it
    // is doing something the default is not.
    expect(requestFrom(choices())).toEqual({
      members: ['Manager'],
      purge: true,
      clearTranscripts: true,
    })
  })

  it('sends forgetHistory FALSE when memory is being kept, because absence means true', () => {
    // The one flag emitted in the negative, and the only one whose server-side default is true.
    // Unticking "Delete memory" leaves the folder clears on the table: clear the folder, keep the
    // log. A checkbox that cannot be unticked is a lie.
    expect(requestFrom(choices({ deleteMemory: false, clearWorkspaces: true }))).toEqual({
      members: ['Manager'],
      forgetHistory: false,
      clearWorkspaces: true,
    })
  })

  it('omits every other flag that was not ticked rather than sending false', () => {
    // The server reads an absent flag as false, so the two are equivalent today. They stop being
    // equivalent the moment a flag grows a third state, which `systemPrompt` already has on two
    // other routes in this API.
    expect(requestFrom(choices({ deleteMemory: false }))).toEqual({
      members: ['Manager'],
      forgetHistory: false,
    })
  })

  it('carries the team-wide options through when they are ticked', () => {
    expect(
      requestFrom(choices({ clearSharedDocuments: true })),
    ).toEqual({
      members: ['Manager'],
      purge: true,
      clearTranscripts: true,
      clearSharedDocuments: true,
    })
  })
})

describe('whether a reset would do anything', () => {
  it('is false with no member ticked and nothing team-wide', () => {
    expect(wouldDoSomething(choices({ members: {} }))).toBe(false)
  })

  it('is false when members are ticked but every per-member option is off', () => {
    // Not the same question as "is a member ticked". This asks the server to floor nothing and
    // clear nothing, and a reset that answers "nothing moved" is a worse answer than a control that
    // will not fire.
    expect(wouldDoSomething(choices({ deleteMemory: false }))).toBe(false)
  })

  it('is true for a team-wide option even with no member ticked', () => {
    // Shared docs are team-wide, so this still does work with no members ticked.
    expect(wouldDoSomething(choices({ members: {}, clearSharedDocuments: true }))).toBe(true)
    expect(wouldDoSomething(choices({ members: {} }))).toBe(false)
  })

  it('is true for the ordinary case', () => {
    expect(wouldDoSomething(choices())).toBe(true)
  })
})
