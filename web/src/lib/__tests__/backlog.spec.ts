import { describe, expect, it } from 'vitest'
import type { BacklogItemView } from '../../api/client'
import type { BacklogLanded } from '../../api/types'
import {
  LandedStates,
  REVIEW_OPTIONS,
  canDispatch,
  canDrag,
  canMarkImplemented,
  canReopen,
  canReview,
  dispatchedTeamId,
  dispatchedTeamLabel,
  dispatchedTeamOptions,
  efficiencyLine,
  inFlightText,
  inFlightTitle,
  itemLabel,
  landedMark,
  linkTitle,
  neighboursFor,
  newTeamNameFor,
  numbered,
  reviewCaption,
  teamLabel,
  teamOptions,
  tokensLine,
  whyDispatchDisabled,
  whyDragDisabled,
} from '../backlog'

function item(over: Partial<BacklogItemView> & { id: number }): BacklogItemView {
  return {
    team: null,
    teamName: null,
    teamGone: false,
    title: `item ${over.id}`,
    body: '',
    state: 'pending',
    archivedAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    updatedAt: '2026-09-01T00:00:00Z',
    createdBy: 'someone@example.com',
    inFlight: null,
    ...over,
  }
}

describe('inFlightText', () => {
  /**
   * THE TEAM AND THE WORKFLOW, in the vocabulary the rest of the product uses: `#1402` is what the
   * dispatch notification says and what `harness` takes. Short, because it sits under a title
   * on a phone.
   */
  it('names the team and the workflow number', () => {
    expect(inFlightText({ teamId: 'beta', teamName: 'Beta', correlation: 1402, running: true }))
      .toBe('Beta · #1402')
  })
})

describe('inFlightTitle', () => {
  /** Running now and open-but-idle are two facts, and the long form says which. */
  it('says whether anybody is running at this moment', () => {
    const flight = { teamId: 'beta', teamName: 'Beta', correlation: 1402, running: true }

    expect(inFlightTitle(flight)).toBe('In flight on Beta, workflow #1402. A member is running now.')
    expect(inFlightTitle({ ...flight, running: false }))
      .toBe('In flight on Beta, workflow #1402. Open, nobody running at the moment.')
  })
})

describe('teamLabel', () => {
  /**
   * THREE ANSWERS, NOT TWO. "no team" and "team deleted" are different facts and reading them as
   * one would lose the whole point of an item that outlives its team.
   */
  it('tells no team apart from a team that is gone', () => {
    expect(teamLabel(item({ id: 1 }))).toBe('(no team)')
    expect(teamLabel(item({ id: 2, team: 'alpha', teamName: 'Alpha', teamGone: false }))).toBe('Alpha')
    expect(teamLabel(item({ id: 3, team: 'alpha', teamName: null, teamGone: true }))).toBe('(team deleted)')
  })
})

/**
 * THE ID IS RENDERED IN CROCKFORD BASE 32, UPPERCASE, ZERO-PADDED TO FOUR CHARACTERS.
 *
 * The integer behind it is what the API sends; this is rendering only. The worked examples pin this implementation to the C#
 * `PlatformBacklogId.Format` that renders the same label on the server - if either side drifts,
 * one of these three fails.
 */
describe('itemLabel', () => {
  /** Every id in 1..2100 - crosses 31/32 and 1023/1024, where a digit is added. */
  const range = Array.from({ length: 2100 }, (_, i) => i + 1)

  it('renders the worked examples from D1', () => {
    expect(itemLabel(17)).toBe('B000H')
    expect(itemLabel(24)).toBe('B000R')
    expect(itemLabel(25)).toBe('B000S')
  })

  /**
   * THE PROPERTY, NOT THREE HAND-PICKED EXAMPLES. Sorted as strings under the default sort, the
   * labels come out in numeric id order - which is the entire reason for padding and uppercase.
   */
  it('sorts as strings in the same order as the integers behind them', () => {
    const inIdOrder = range.map(itemLabel)
    const asStrings = [...inIdOrder].sort()

    expect(asStrings).toEqual(inIdOrder)
  })

  /**
   * THE CONTROL FIRST. Unpadded, `['B100', 'B17'].sort()` puts 100 first; the padded rendering
   * puts 17 first.
   */
  it('puts 17 before 100, where the unpadded label put 100 first', () => {
    expect(['B100', 'B17'].sort()[0]).toBe('B100')

    expect([itemLabel(100), itemLabel(17)].sort()[0]).toBe(itemLabel(17))
  })

  /**
   * UPPERCASE, AND NEVER I, L, O OR U. Lowercase sorts after `Z` in ASCII and would break the
   * property above; the four letters are the ones people misread, and are not in the alphabet.
   */
  it('never emits a lowercase letter or I, L, O, U', () => {
    for (const id of range) {
      const label = itemLabel(id)

      expect(label).toMatch(/^B[0-9A-HJKMNP-TV-Z]{4,}$/)
    }
  })

  it('takes a fifth character above 1,048,575 rather than truncating', () => {
    expect(itemLabel(1_048_575)).toBe('BZZZZ')
    expect(itemLabel(1_048_576)).toBe('B10000')
  })

  /**
   * THE SORT PROPERTY STOPS AT THE WIDTH BOUNDARY, and this pins it so the comment on
   * `LABEL_WIDTH` cannot drift back to claiming otherwise: `1` sorts before `Z`, so the five-
   * character label lands first. Accepted, because no id is near that size and truncating is worse.
   */
  it('does not sort across the four-to-five character boundary', () => {
    expect([itemLabel(1_048_575), itemLabel(1_048_576)].sort()).toEqual(['B10000', 'BZZZZ'])
  })
})

describe('canDrag', () => {
  /**
   * DRAGGING IS PERMITTED ONLY UNDER THE POSITION SORT, and the reason is given rather than the
   * control silently doing nothing.
   */
  it('is allowed under the position sort and refused with a reason under any other', () => {
    expect(canDrag({ by: 'position', descending: false })).toBe(true)
    expect(canDrag({ by: 'updated', descending: false })).toBe(false)
    expect(whyDragDisabled({ by: 'updated', descending: false })).toContain('Sort by #')
    expect(whyDragDisabled({ by: 'position', descending: false })).toBe('')
  })
})

describe('numbered', () => {
  /**
   * THE `#` FOLLOWS POSITION ORDER AND NOT THE DISPLAYED SORT. A person sorting by Modified still
   * wants to know an item is 3rd in the backlog - an index that renumbered with the sort would be a
   * third number on the row meaning nothing.
   */
  it('keeps the position index when the table is sorted by something else', () => {
    const items = [item({ id: 5 }), item({ id: 9 }), item({ id: 2 })]

    const rows = numbered(items, { by: 'id', descending: false })

    expect(rows.map((r) => r.id)).toEqual([2, 5, 9])
    expect(rows.find((r) => r.id === 5)!.index).toBe(1)
    expect(rows.find((r) => r.id === 9)!.index).toBe(2)
    expect(rows.find((r) => r.id === 2)!.index).toBe(3)
  })

  it('leaves the server order alone under the position sort', () => {
    const items = [item({ id: 5 }), item({ id: 9 }), item({ id: 2 })]

    const rows = numbered(items, { by: 'position', descending: false })

    expect(rows.map((r) => r.id)).toEqual([5, 9, 2])
    expect(rows.map((r) => r.index)).toEqual([1, 2, 3])
  })
})

describe('neighboursFor', () => {
  /**
   * BOTH MAY BE NULL AND NEITHER IS AN ERROR: the top has no `after` and the bottom has no
   * `before`. The off-by-one lives here, which is why it is tested without a DOM.
   */
  it('answers the neighbours in the list after removal', () => {
    const items = [item({ id: 1 }), item({ id: 2 }), item({ id: 3 })]

    expect(neighboursFor(items, 2, 0)).toEqual({ after: null, before: 1 })
    expect(neighboursFor(items, 0, 1)).toEqual({ after: 2, before: 3 })
    expect(neighboursFor(items, 0, 2)).toEqual({ after: 3, before: null })
  })

  it('answers both null for a list of one', () => {
    expect(neighboursFor([item({ id: 1 })], 0, 0)).toEqual({ after: null, before: null })
  })
})

describe('teamOptions', () => {
  /**
   * "(no team)" COMES FIRST, FOR EVERYONE. Every person is an administrator, so there is nobody to
   * withhold it from.
   */
  it('offers the tenant option ahead of every team', () => {
    const teams = [{ id: 'alpha', name: 'Alpha' }]

    expect(teamOptions(teams).map((o) => o.value)).toEqual([null, 'alpha'])
  })
})

/** A dispatch that is still open, for the fixtures below. */
function flight(teamId: string, teamName: string) {
  return { teamId, teamName, correlation: 1402, running: false }
}

describe('canDispatch', () => {
  /**
   * ONLY `ready` GOES. `pending` means nobody has read the spec, which is the whole point of the
   * state, and `declared` and `implemented` are both past it - the two dispatch routes refuse all
   * three with a 409 and this is the client's copy of the same rule.
   *
   * `declared` IS NOT A REDISPATCHABLE STATE, and that is the server's own line: `RefuseUnlessReady`
   * admits only `ready`, so a declared item waits for a PERSON to move it back rather than for a
   * second dispatch to arrive by accident.
   */
  it('admits ready and refuses the other three', () => {
    expect(canDispatch(item({ id: 1, state: 'ready' }))).toBe(true)
    expect(canDispatch(item({ id: 2, state: 'pending' }))).toBe(false)
    expect(canDispatch(item({ id: 3, state: 'declared' }))).toBe(false)
    expect(canDispatch(item({ id: 4, state: 'implemented' }))).toBe(false)
  })
})

describe('whyDispatchDisabled', () => {
  /**
   * IT NAMES THE WAY OUT, not only the refusal. A disabled control whose explanation is "no" sends
   * a person looking for a setting they have not found - and this string is RENDERED, because a
   * disabled `<button>` fires no mouse events and the tooltip inside one never opens.
   */
  it('says what to do about it, and says nothing at all when there is nothing to say', () => {
    expect(whyDispatchDisabled(item({ id: 1, state: 'pending' })))
      .toBe('Not reviewed - mark it ready to dispatch.')
    expect(whyDispatchDisabled(item({ id: 2, state: 'implemented' })))
      .toBe('Implemented - mark it ready to dispatch it again.')
    expect(whyDispatchDisabled(item({ id: 3, state: 'ready' }))).toBe('')
  })

  /**
   * THIS IS THE `declared` READING ON THE ROW, and it is the reason the sentence is not allowed to
   * fall through to the `pending` arm. "Not reviewed" on an item a Manager has just declared is a
   * flat contradiction of the row above it, and it is what a fourth state added to a three-armed
   * conditional produces by default.
   */
  it('does not call a declared item unreviewed', () => {
    const why = whyDispatchDisabled(item({ id: 4, state: 'declared' }))

    expect(why).not.toBe('Not reviewed - mark it ready to dispatch.')
    expect(why).toContain('Manager')
    expect(why).toContain('mark it ready')
  })
})

describe('the review control', () => {
  /** `implemented` is the other axis and the platform sets it. Two options, deliberately. */
  it('moves between pending and ready and offers nothing else', () => {
    expect(REVIEW_OPTIONS.map((o) => o.value)).toEqual(['pending', 'ready'])
  })

  /**
   * TWO STATES THE TOGGLE CANNOT SHOW, NOT ONE. Neither `declared` nor `implemented` is one of the
   * toggle's two values, so on either it would render with nothing selected and read as broken -
   * which is why it is absent rather than disabled. `declared` joins `implemented` here for exactly
   * the reason `implemented` was already here, and nothing about the reason is new.
   */
  it('does not apply to an item already declared or implemented', () => {
    expect(canReview(item({ id: 1, state: 'pending' }))).toBe(true)
    expect(canReview(item({ id: 2, state: 'ready' }))).toBe(true)
    expect(canReview(item({ id: 3, state: 'declared' }))).toBe(false)
    expect(canReview(item({ id: 4, state: 'implemented' }))).toBe(false)
  })

  /**
   * A PERSON MAY STILL SAY `implemented` BY HAND, AND ON A `declared` ITEM THAT IS THE WHOLE POINT.
   * The derivation never writes that state - a Manager's declaration stops at `declared` - so
   * the only thing that can ever move an item the rest of the way is a person, and the control
   * they do it with has to be on the screen in front of them while they read the landing.
   *
   * IT IS NOT THE REVIEW TOGGLE. Two unrelated decisions on one control is the defect to avoid;
   * this is a separate button and {@link canReview} still refuses to offer `implemented`.
   */
  it('keeps "mark implemented" reachable everywhere except an item already there', () => {
    expect(canMarkImplemented(item({ id: 1, state: 'pending' }))).toBe(true)
    expect(canMarkImplemented(item({ id: 2, state: 'ready' }))).toBe(true)
    expect(canMarkImplemented(item({ id: 3, state: 'declared' }))).toBe(true)
    expect(canMarkImplemented(item({ id: 4, state: 'implemented' }))).toBe(false)
  })

  /**
   * THE WAY BACK, OFFERED ONLY WHERE THE TOGGLE IS NOT. On `pending` and `ready` the toggle already
   * carries Pending, and a second control for the same move would be two buttons for one decision.
   */
  it('offers "reopen as pending" on the two states the toggle cannot show', () => {
    expect(canReopen(item({ id: 1, state: 'pending' }))).toBe(false)
    expect(canReopen(item({ id: 2, state: 'ready' }))).toBe(false)
    expect(canReopen(item({ id: 3, state: 'declared' }))).toBe(true)
    expect(canReopen(item({ id: 4, state: 'implemented' }))).toBe(true)
  })

  it('says what the state it is showing means', () => {
    expect(reviewCaption(item({ id: 1, state: 'pending' }))).toContain('nobody has reviewed this')
    expect(reviewCaption(item({ id: 2, state: 'ready' }))).toContain('somebody has read this')
    expect(reviewCaption(item({ id: 4, state: 'implemented' }))).toContain('Reopen it as pending')
  })

  /**
   * THE SENTENCE IN THE DETAIL PANEL. `declared` must read as a CLAIM SOMEBODY MADE and must not
   * read as shipped - an item can be declared while the only copy of the work is on one disk.
   */
  it('reads `declared` as a Manager saying so, and never as the work having landed', () => {
    const caption = reviewCaption(item({ id: 3, state: 'declared' }))

    expect(caption).toContain('Manager')
    expect(caption).toContain('not')

    // IT DOES NOT CLAIM A LANDING. The landing is the other fact and is rendered beside it.
    expect(caption).not.toContain('is in the product')
  })

  /**
   * `implemented` IS NOT SOMETHING THE PLATFORM WRITES, and a caption that said it did would
   * teach people exactly the confusion the `declared` state exists to prevent.
   */
  it('never tells a person that a completed workflow sets `implemented`', () => {
    expect(reviewCaption(item({ id: 4, state: 'implemented' })))
      .not.toContain('set for you when a dispatched workflow completes')
  })
})

/**
 * WHERE THE WORK ACTUALLY IS, WHICH IS NOT WHAT ANY STATE SAYS.
 *
 * DERIVED BY THE SERVER AND NEVER STORED - the same discipline `inFlight` follows - and it is the
 * other half of the state split: a person reading the Backlog has to be able to tell *a Manager
 * says this is done* from *this is on main*, and one word cannot carry both.
 *
 * THE FOUR READINGS ARE FOUR DIFFERENT FACTS and the important boundary is between the last two:
 * `local` is A NEGATIVE ANSWER - somebody looked and the work is on one disk - and `unknown` is NO
 * ANSWER AT ALL. This codebase already holds that rule for spend (`tokensLine` renders `(unknown)`
 * rather than `0`), and rendering a missing answer as a negative one is how a screen invents a
 * measurement nobody made.
 */
describe('landedMark', () => {
  function landing(state: string, detail = ''): BacklogLanded {
    return { state, teamId: 'alpha', detail }
  }

  it('mirrors the server vocabulary so a grep for a state finds both halves', () => {
    expect(Object.values(LandedStates)).toEqual(['landed', 'pushed', 'local', 'unknown', 'in-review', 'declined'])
  })

  // Contributor mode's two words, from the recorded pull request's state.
  it('says in review and declined for a pull request upstream, and declined is not landed', () => {
    expect(landedMark(landing('in-review'))?.text).toBe('Pull request in review')
    expect(landedMark(landing('declined'))?.text).toBe('Pull request declined')
    expect(landedMark(landing('declined'))?.state).toBe('declined')
    expect(landedMark(landing('declined'))?.title).toContain('has not landed')
  })

  it('says on main, pushed, only in the clone, and cannot tell - in four different sentences', () => {
    expect(landedMark(landing('landed'))?.text).toBe('On the default branch')
    expect(landedMark(landing('pushed'))?.text).toBe('Pushed, not merged')
    expect(landedMark(landing('local'))?.text).toBe("Only in the team's clone")
    expect(landedMark(landing('unknown'))?.text).toBe('Cannot tell')

    const texts = ['landed', 'pushed', 'local', 'unknown'].map((s) => landedMark(landing(s))?.text)

    expect(new Set(texts).size).toBe(4)
  })

  /**
   * THE LANDING REQUIREMENT, ASSERTED AS A DIFFERENCE RATHER THAN AS A STRING. `unknown` and
   * `local` must not be one rendering: the word, the marker the screen keys off and the icon all
   * differ, so neither a reader nor a test can mistake one for the other.
   */
  it('renders `unknown` as a different thing from `local`, not as a quieter version of it', () => {
    const unknown = landedMark(landing('unknown'))!
    const local = landedMark(landing('local'))!

    expect(unknown.state).not.toBe(local.state)
    expect(unknown.text).not.toBe(local.text)
    expect(unknown.icon).not.toBe(local.icon)

    // AND IT SAYS SO IN WORDS. "Nobody has answered" is the fact; "not landed" is a different one.
    expect(unknown.title).toContain('Nobody has')
    expect(unknown.title).not.toContain('one disk')
  })

  /**
   * NULL IS NOT `unknown`. The field is absent on an item no dispatch has ever touched, and there
   * is no landing question to answer about one - exactly as `inFlight: null` renders no mark rather
   * than "not in flight". `unknown` is the answer when the question WAS asked and could not be
   * answered.
   */
  it('renders nothing at all when the server sent no landing', () => {
    expect(landedMark(null)).toBeNull()
    expect(landedMark(undefined)).toBeNull()
  })

  /**
   * A WORD THIS CLIENT HAS NEVER HEARD IS A MISSING ANSWER, NOT A BAD ONE. The server may grow a
   * fifth reading before this screen does, and the safe default is the one that claims nothing -
   * falling through to `local` would have an older client telling people their work was on one disk
   * on no evidence whatsoever.
   */
  it('reads a state it does not recognise as `unknown` and never as a negative', () => {
    const mark = landedMark(landing('something-new'))!

    expect(mark.state).toBe('unknown')
    expect(mark.text).toBe('Cannot tell')
  })

  /**
   * THE SERVER'S OWN SENTENCE SURVIVES. `detail` is the short human line the server puts on the
   * wire - which branch, which remote, how far behind - and dropping it would leave the screen
   * with four words where the server sent an explanation.
   */
  it('carries the server detail into the long form', () => {
    const mark = landedMark(landing('local', 'No branch on origin; 5 commits in the clone.'))!

    expect(mark.title).toContain('No branch on origin; 5 commits in the clone.')
  })

  /**
   * THE TEAM ID IS CARRIED AND NOT DRAWN, AND THAT IS A DECISION RATHER THAN AN OMISSION.
   * `landed.teamId` is on the wire because the server sends it and these types are the wire - but
   * it names the SAME team the row's Team column already shows, under the name rather than the id,
   * and the server's `detail` names the repository and branch it actually looked at. The mark is
   * the reading plus that sentence, and nothing else.
   *
   * ASSERTED WITH A DETAIL THAT DOES NOT MENTION THE TEAM, because the server's own sentence is
   * free to - `team/alpha (2 commits)` is a branch name - and what it writes there is its copy to
   * make. What this pins is that the CLIENT adds no second, less readable copy of its own.
   */
  it('carries the team id without drawing it - the Team column already names that team', () => {
    const mark = landedMark(
      { state: 'local', teamId: 'unmistakable-team-id', detail: 'Two commits on no remote.' })!

    expect(mark.text).not.toContain('unmistakable-team-id')
    expect(mark.title).not.toContain('unmistakable-team-id')

    // And the reading itself is untouched by carrying it.
    expect(mark.state).toBe('local')
    expect(mark.title).toContain('Two commits on no remote.')
  })

  it('holds its own long form together when the server sent no detail', () => {
    const mark = landedMark(landing('landed'))!

    expect(mark.title.length).toBeGreaterThan(0)
    expect(mark.title.trim()).toBe(mark.title)
  })
})

/**
 * THE TEAM COLUMN, WHICH IS NOT THE LINK.
 *
 * `team` says who may SEE an item and is never rewritten by a dispatch; this says who was GIVEN
 * the work. The two can differ and are deliberately not merged - the fixture below carries both
 * and they disagree, which is the case a screen merging them would contradict itself on.
 */
describe('dispatchedTeamLabel', () => {
  it('names the team an item was dispatched to, not the team that can see it', () => {
    expect(dispatchedTeamLabel(item({ id: 1, team: 'alpha', teamName: 'Alpha', inFlight: flight('beta', 'Beta') })))
      .toBe('Beta')
  })

  /** An empty cell, never `(no team)` - which is an answer about the link and means something else. */
  it('says nothing at all about an item nobody has been given', () => {
    expect(dispatchedTeamLabel(item({ id: 1 }))).toBe('')
    expect(dispatchedTeamLabel(item({ id: 2, team: 'alpha', teamName: 'Alpha' }))).toBe('')
  })

  /**
   * THE PERSISTENT FIELD WINS WHERE IT EXISTS. `inFlight` is dropped by the server the moment the
   * workflow closes, so it answers this question only while somebody is still working; the owed
   * `dispatchedTeam` answers it afterwards too, and this pins that the client already takes it.
   */
  it('prefers the dispatch that outlives its workflow', () => {
    expect(dispatchedTeamLabel(item({ id: 1, dispatchedTeam: 'beta', dispatchedTeamName: 'Beta' })))
      .toBe('Beta')
    expect(dispatchedTeamId(item({ id: 2, dispatchedTeam: 'beta', inFlight: flight('gamma', 'Gamma') })))
      .toBe('beta')
  })

  /** A name the server did not send is not a reason to show nothing - the id is what addresses it. */
  it('falls back to the id rather than to a blank', () => {
    expect(dispatchedTeamLabel(item({ id: 1, dispatchedTeam: 'beta' }))).toBe('beta')
  })
})

describe('dispatchedTeamOptions', () => {
  /**
   * AN OPTION THAT CAN ONLY EVER RETURN NOTHING MUST NOT BE OFFERED. Building the dropdown from
   * `teamOptions` - every team on the board - while the filter narrows on `item.team` would, on a
   * backlog of tenant items, make every option but `All` empty the table while the rows visibly
   * named the teams working them.
   */
  it('offers the teams the items carry and nothing else', () => {
    const options = dispatchedTeamOptions([
      item({ id: 1, team: 'alpha', teamName: 'Alpha' }),
      item({ id: 2, inFlight: flight('beta', 'Beta') }),
      item({ id: 3, dispatchedTeam: 'beta', dispatchedTeamName: 'Beta' }),
    ])

    // Beta once, not twice; Alpha not at all - it holds a LINK and is working nothing.
    expect(options).toEqual([
      { label: 'Beta', value: 'beta' },
      { label: 'Not dispatched', value: null },
    ])
  })

  /** Sorted by what a person reads, which is the label rather than the id. */
  it('orders by label', () => {
    const options = dispatchedTeamOptions([
      item({ id: 1, dispatchedTeam: 'z', dispatchedTeamName: 'Aardvark' }),
      item({ id: 2, dispatchedTeam: 'a', dispatchedTeamName: 'Zebra' }),
    ])

    expect(options.map((o) => o.label)).toEqual(['Aardvark', 'Zebra'])
  })

  /** `Not dispatched` is subject to the same rule as every other option. */
  it('does not offer "Not dispatched" when every item has been', () => {
    const options = dispatchedTeamOptions([item({ id: 1, inFlight: flight('beta', 'Beta') })])

    expect(options.map((o) => o.value)).toEqual(['beta'])
  })

  it('offers nothing at all for an empty backlog', () => {
    expect(dispatchedTeamOptions([])).toEqual([])
  })
})

describe('linkTitle', () => {
  /** Two teams on one row is exactly where a bare name misleads, so the marker says what it is. */
  it('says the link decides visibility rather than where the item runs', () => {
    const title = linkTitle(item({ id: 1, team: 'alpha', teamName: 'Alpha' }))

    expect(title).toContain('Visible to Alpha')
    expect(title).toContain('never rewritten by a dispatch')
  })
})

describe('tokensLine', () => {
  /**
   * A TOTAL ALONE LIES BY OMISSION. The count of runs that contributed nothing is rendered beside
   * it, and freezing a bare total would make that lie permanent.
   */
  it('names the runs that contributed nothing', () => {
    expect(tokensLine({ tokens: 5000, runsWithUsage: 2, runsWithoutUsage: 1 }))
      .toBe('5,000 tokens · 1 run reported nothing')

    expect(tokensLine({ tokens: 5000, runsWithUsage: 2, runsWithoutUsage: 0 }))
      .toBe('5,000 tokens')
  })

  /** `(unknown)` IS A REAL STATE. Showing 0 would be inventing a number. */
  it('says unknown rather than zero when nothing was measured', () => {
    expect(tokensLine({ tokens: 0, runsWithUsage: 0, runsWithoutUsage: 3 }))
      .toContain('(unknown)')
  })

  it('says so when there were no runs at all', () => {
    expect(tokensLine({ tokens: 0, runsWithUsage: 0, runsWithoutUsage: 0 })).toBe('(no runs)')
  })
})

describe('efficiencyLine', () => {
  /** PINS THE SUB-HOUR FORMATTING - the hour bucket added below must not touch this shape. */
  it('puts the clock beside the cost', () => {
    expect(efficiencyLine({ elapsedSeconds: 157, tokens: 5000, runsWithUsage: 2, runsWithoutUsage: 0 }))
      .toBe('2m 37s · 5,000 tokens')
  })

  /**
   * AN HOUR BUCKET, NOT THREE-DIGIT MINUTES. This platform's runs are bounded by an idle clock
   * rather than a wall clock, so a multi-hour workflow is an ordinary shape - `"125m 30s"` reads
   * as a bug where `"2h 5m 30s"` reads as what happened.
   */
  it('adds an hour bucket for a multi-hour workflow', () => {
    expect(efficiencyLine({ elapsedSeconds: 7530, tokens: 5000, runsWithUsage: 2, runsWithoutUsage: 0 }))
      .toBe('2h 5m 30s · 5,000 tokens')
  })

  /** ABSENT IS NOT ZERO - a workflow still running has no elapsed time. */
  it('omits the clock rather than showing zero when nothing finished', () => {
    expect(efficiencyLine({ elapsedSeconds: null, tokens: 5000, runsWithUsage: 2, runsWithoutUsage: 0 }))
      .toBe('5,000 tokens')
  })

  /** It carries what is missing from the total, exactly as `tokensLine` does. */
  it('keeps the unmeasured count', () => {
    expect(efficiencyLine({ elapsedSeconds: 30, tokens: 5000, runsWithUsage: 2, runsWithoutUsage: 1 }))
      .toContain('1 run reported nothing')
  })
})

/**
 * THE NAME A NEW TEAM GETS WHEN AN ITEM IS DISPATCHED INTO ONE.
 *
 * DERIVED ON THE CLIENT AND SENT, never derived twice. The dialog has to PREFILL the field so a
 * person can edit it before committing, so the rule has to run here - and a second implementation
 * on the server would be two stores of one fact, free to disagree the first time either moved.
 * The server's job is to refuse a name `ContainerId.IsLegalName` rejects, which is a different
 * question from what a good default looks like.
 *
 * THE ID LEADS, because a team outlives the conversation that made it and `b0002-` is what ties it
 * back to the item. The rest is the title with its stopwords dropped: a team name is read in a tab
 * strip at a glance, and "a", "the" and "is" cost characters against a 32-character ceiling without
 * telling anybody anything.
 */
describe('newTeamNameFor', () => {
  /**
   * `two` AND `commands` ARE DROPPED, AND THAT IS THE FIVE-CHARACTER LEAD SHOWING. Behind
   * `b0002` the budget stops at `central`, because `-two` would take it to 33. The words that do
   * not fit are dropped WHOLE.
   */
  it('leads with the item id and drops stopwords', () => {
    expect(newTeamNameFor({ id: 2, title: 'A guard the rules say is central is in two commands' }))
      .toBe('b0002-guard-rules-say-central')
  })

  /**
   * PUNCTUATION GOES, AND SO DOES WHAT IT LEAVES BEHIND. Splitting an apostrophe turns "doesn't"
   * into `doesn` and `t`; the stray letter is dropped because a single character in an identifier
   * reads as damage rather than as a word. `what` is a stopword, so it goes too.
   */
  it('strips punctuation rather than carrying it into an identifier', () => {
    expect(newTeamNameFor({ id: 3, title: "Cloning: what it carries, and what it doesn't" }))
      .toBe('b0003-cloning-carries-doesn')
  })

  /**
   * A TITLE WITH NOTHING DERIVABLE STILL PRODUCES A LEGAL NAME. `ContainerId.DeriveName` answers
   * null for a name with no ASCII and the platform generates one rather than refusing - the same
   * reasoning applies here, and the id alone is always legal and always unique.
   */
  it('falls back to the id alone when the title yields nothing', () => {
    expect(newTeamNameFor({ id: 9, title: '。。。' })).toBe('b0009')
    expect(newTeamNameFor({ id: 9, title: 'the of and is' })).toBe('b0009')
  })

  /**
   * ONE FUNCTION OWNS THE SPELLING, AND THIS IS THE TEST THAT SAYS SO.
   *
   * The lead is `itemLabel` lowercased and nothing else. Asserting it AGAINST `itemLabel` rather
   * than against the literal `b000q` is the point: a second store of the spelling - a constant, a
   * regex, a copy of the alphabet - would satisfy a literal and would be free to drift the first
   * time either copy moved. This comparison cannot be satisfied by a copy that has drifted.
   *
   * The literal is asserted too, because a relationship alone would also hold if BOTH functions
   * were wrong together.
   */
  it('leads with the id spelled the way the board spells it, lowercased', () => {
    const name = newTeamNameFor({ id: 23, title: 'Documents live under a team' })

    expect(name.startsWith(itemLabel(23).toLowerCase())).toBe(true)
    expect(name).toBe('b000q-documents-live-under-team')
  })

  /**
   * THE LEAD IS FIVE CHARACTERS WHERE IT WAS THREE, SO THE CEILING BITES TWO WORDS EARLIER - and
   * the behaviour that has to survive that is WHOLE-WORD dropping. A name cut mid-word reads as
   * corruption rather than as a default somebody can edit, and the extra two characters are
   * exactly the pressure that would produce one if the loop ever stopped checking before it
   * appended.
   *
   * `b000h-something-extraordinarily` is 31; `-verbose` would take it to 39, so the loop stops on
   * a word boundary and the remaining words are dropped ENTIRELY rather than trimmed.
   */
  it('still drops whole words when the five-character lead eats into the budget', () => {
    const title = 'Something extraordinarily verbose about everything at once'
    const name = newTeamNameFor({ id: 17, title })

    expect(name).toBe('b000h-something-extraordinarily')
    expect(name.length).toBeLessThanOrEqual(32)

    // NOT A PREFIX OF A WORD - a whole one. This is the assertion a mid-word cut fails.
    const titleWords = new Set(title.toLowerCase().split(/[^a-z0-9]+/).filter(Boolean))

    for (const segment of name.split('-').slice(1)) {
      expect(titleWords.has(segment)).toBe(true)
    }
  })

  /**
   * `ContainerId.IsLegalName` STILL HAS TO PASS, and the client cannot call it - so the rule is
   * restated over a spread of ids that crosses the label's digit boundaries (single digit, the
   * letters, and past 32 where the second base-32 digit appears).
   */
  it('produces a legal ContainerId name at every id', () => {
    for (const id of [0, 1, 9, 17, 23, 31, 32, 1_000, 1_048_576]) {
      const name = newTeamNameFor({ id, title: 'Something extraordinarily verbose about everything at once' })

      expect(name.length).toBeLessThanOrEqual(32)
      expect(name).toMatch(/^[a-z0-9][a-z0-9_-]*$/)
      expect(name.endsWith('-')).toBe(false)
    }
  })
})
