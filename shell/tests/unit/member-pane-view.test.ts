// @vitest-environment happy-dom
import { beforeEach, describe, expect, it } from 'vitest';
import { nextTick, watch } from 'vue';
import { focusMemberTerminal, getMemberView, memberFocus, resetMemberViews, setMemberView, takeMemberFocus } from '../../src/renderer/src/memberPaneView';

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
});
