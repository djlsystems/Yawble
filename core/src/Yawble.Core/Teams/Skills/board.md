---
name: board
description: How a Manager uses the board well - plan cards before telling, one card per member piece, keep cards current, and act on the person's moves, edits and comments.
roles: [manager]
---
# The board

The board is how the person watches the team: one card per member per workflow, in To Do, In Progress, Needs
You and Done. Cards follow what you and the members do (a tell, a hand-back, a block, a question); you shape
them with the `board` tool.

## The actions

- `board` `action: show`: every card, with its lane, member and trail.
- `board` `action: plan` (`title`, `body`, `item` when it comes from a backlog item): a card in To Do,
  unassigned.
- `board` `action: edit` (`card`, `title`, `body`): retitle or rescope a card when the work turns out different.
- `board` `action: move` (`card`, `lane`, `note`): place a card. It stays where you put it until a member
  fails or is blocked on it.
- `board` `action: comment` (`card`, `text`): add a line to a card's trail.

## Plan before you tell

- Plan the cards first, then `tell` each member with `card: <id>`. A tell without `card` makes a second card,
  and the planned one sits in To Do forever.
- One card per member piece. Three cards a person can read beat nine that restate a spec's headings.
- A title says what will exist when it is done ("CSV export of the order list"), not who does it. The body is
  the card itself: what to build or change, which files, how to check it, and to commit on the member's branch.
- A backlog item dispatched to your team is a spec: read it with `backlog` (`item: B007`), then plan its
  pieces with `item: B007`.

## Keep cards current

- Cards move by themselves as members work. Move one yourself when the board would otherwise mislead: work you
  are leaving undone on purpose, a card you are holding back. Give a `note` saying why.
- Comment rather than move silently. A decision, a changed plan, a fault the tester found and who is fixing it:
  one line in the card's trail saves the person reading the whole thread.
- Before `workflow_complete`, every card is Done or carries a comment saying why it is not.

## When the person touches a card

The person moving, editing or commenting on a card wakes you, and arrives as an instruction about that card.
The edit IS the message:

- Read the card (`board` `action: show`) when you need more than the wake says.
- Act on it: tell the member what changed, move the card back if the move was a mistake you can explain, or
  ask with `needs_decision` if it is unclear.
- Acknowledge with `board` `action: comment`, saying what you did. Never drop it silently.
