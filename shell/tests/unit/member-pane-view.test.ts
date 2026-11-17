// @vitest-environment happy-dom
import { beforeEach, describe, expect, it } from 'vitest';
import { nextTick, watch } from 'vue';
import { closePhoneTerminals, focusMemberTerminal, getMemberView, memberFocus, resetMemberViews, setMemberView, takeMemberFocus } from '../../src/renderer/src/memberPaneView';

// The shared per-member view of the team page's panes (B061): Activity by default, Terminal when chosen, kept in
// storage; focusMemberTerminal switches to Terminal and leaves a request for that team's page.
describe('memberPaneView', () => {
  beforeEach(() => { resetMemberViews(); localStorage.clear(); });

  it('is Activity by default and remembers Terminal per member', () => {
    expect(getMemberView('payments', 'DeveloperMarlo')).toBe('activity');
    setMemberView('payments', 'DeveloperMarlo', 'terminal');
    expect(getMemberView('payments', 'DeveloperMarlo')).toBe('terminal');
    expect(getMemberView('payments', 'TesterWren')).toBe('activity');
    expect(getMemberView('other', 'DeveloperMarlo')).toBe('activity');
    expect(JSON.parse(localStorage.getItem('yawble.memberPaneView')!)).toEqual({ 'payments/DeveloperMarlo': 'terminal' });
    setMemberView('payments', 'DeveloperMarlo', 'activity');
    expect(getMemberView('payments', 'DeveloperMarlo')).toBe('activity');
    expect(localStorage.getItem('yawble.memberPaneView')).toBeNull();
  });

  it('is reactive', async () => {
    const seen: string[] = [];
    const stop = watch(() => getMemberView('payments', 'TesterWren'), (v) => seen.push(v));
    setMemberView('payments', 'TesterWren', 'terminal');
    await nextTick();
    setMemberView('payments', 'TesterWren', 'activity');
    await nextTick();
    stop();
    expect(seen).toEqual(['terminal', 'activity']);
  });

  it('focusMemberTerminal switches to Terminal and leaves a request only that team takes', () => {
    focusMemberTerminal('payments', 'TesterWren');
    expect(getMemberView('payments', 'TesterWren')).toBe('terminal');
    expect(memberFocus()).toMatchObject({ team: 'payments', member: 'TesterWren' });
    expect(takeMemberFocus('other')).toBeNull();
    const first = takeMemberFocus('payments');
    expect(first?.member).toBe('TesterWren');
    expect(memberFocus()).toBeNull();
    focusMemberTerminal('payments', 'TesterWren');
    expect(memberFocus()!.seq).toBeGreaterThan(first!.seq);
  });

  // B064 (card 310): a phone's own, unstored choice - one terminal at a time, closed when its team page closes.
  it('on a phone: one terminal at a time, apart from the remembered (desktop) choice, not stored', () => {
    setMemberView('payments', 'DeveloperMarlo', 'terminal');
    expect(getMemberView('payments', 'DeveloperMarlo', true)).toBe('activity');
    setMemberView('payments', 'TesterWren', 'terminal', true);
    setMemberView('payments', 'DeveloperMarlo', 'terminal', true);
    expect(getMemberView('payments', 'DeveloperMarlo', true)).toBe('terminal');
    expect(getMemberView('payments', 'TesterWren', true)).toBe('activity');
    // Setting another member to Activity leaves the open one; its own Activity closes it.
    setMemberView('payments', 'TesterWren', 'activity', true);
    expect(getMemberView('payments', 'DeveloperMarlo', true)).toBe('terminal');
    // The remembered choice is untouched, and only it is stored.
    expect(getMemberView('payments', 'TesterWren')).toBe('activity');
    expect(JSON.parse(localStorage.getItem('yawble.memberPaneView')!)).toEqual({ 'payments/DeveloperMarlo': 'terminal' });
    setMemberView('payments', 'DeveloperMarlo', 'activity', true);
    expect(getMemberView('payments', 'DeveloperMarlo', true)).toBe('activity');
    expect(getMemberView('payments', 'DeveloperMarlo')).toBe('terminal');
  });

  it("closePhoneTerminals closes only that team's phone terminal; focusMemberTerminal opens it on a phone too", () => {
    focusMemberTerminal('payments', 'TesterWren');
    expect(getMemberView('payments', 'TesterWren', true)).toBe('terminal');
    closePhoneTerminals('other');
    expect(getMemberView('payments', 'TesterWren', true)).toBe('terminal');
    closePhoneTerminals('payments');
    expect(getMemberView('payments', 'TesterWren', true)).toBe('activity');
    expect(getMemberView('payments', 'TesterWren')).toBe('terminal');
  });
});
