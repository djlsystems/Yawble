/**
 * Tests for the Git dialog re-read behavior.
 * 
 * The Git dialog should fetch fresh repo status when it opens, rather than rendering
 * the cached status from when the active team was last set. What the card and the dialog do after
 * a ladder action is mounted in `repo-card-ladder.mount.spec.ts` and `team-git-dialog.mount.spec.ts`.
 */

import { beforeEach, describe, expect, it, vi } from 'vitest';
import { createPinia, setActivePinia } from 'pinia';
import type { Overview, Team, TeamRepoStatus, RepoStatus, Prerequisite } from '../../api/types';
import { asMemberId, asTeamId, type TeamId } from '../../api/types';

const container = () => ({
  team: asTeamId('TestTeam'),
  name: 'Manager',
  agent: 'echo',
  state: 'Idle' as const,
  queueDepth: 0,
  ceiling: 16,
  subscribes: [],
  currentCorrelation: null,
  sinceSeq: 0,
  id: asMemberId('Manager'),
});

const team = (id: TeamId, overrides: Partial<Team> = {}): Team => ({
  id,
  name: id,
  containers: [container()],
  concierge: 'claude',
  memberAgents: null,
  additionalInstructions: null,
  root: null,
  ...overrides,
});

const overview: Overview = {
  teams: [team(asTeamId('TestTeam'))],
  managerName: asMemberId('Manager'),
};

const mockRepoStatus: RepoStatus = {
  name: 'test-repo',
  clonePath: '/repos/test-repo/main',
  mainSha: '1234567890abcdef1234567890abcdef12345678',
  mainAhead: 0,
  mainBehind: 0,
  dirty: false,
  headCheckout: 'main',
  teamBranch: 'team/TestTeam',
  teamSha: '1234567890abcdef1234567890abcdef12345678',
  teamPushed: false,
  teamPushedFrom: null,
  teamMergedToMain: false,
  cloneMainOnTeamBranch: false,
  teamCommitsNotOnMain: null,
  originCheckedAt: '2026-09-14T20:00:00Z',
  originReachable: true,
  originUnreachableReason: null,
  worktrees: [],
};

const mockPrerequisite: Prerequisite = {
  command: 'git',
  resolves: true,
  message: '',
  usedBy: 'platform',
};

const mockTeamRepoStatus: TeamRepoStatus = {
  git: mockPrerequisite,
  gh: mockPrerequisite,
  repos: [mockRepoStatus],
};

const getMessages = vi.fn();
const getOverview = vi.fn(async () => overview);
const emptyTokens = {
  available: true,
  tokensIn: 0,
  tokensOut: 0,
  partial: false,
  runsWithUsage: 0,
  runsWithoutUsage: 0,
  missing: null,
  members: [],
};
const getTeamTokens = vi.fn(async (_team: string) => emptyTokens);
const setCurrentTeam = vi.fn(async (_team: string | null) => new Response(null, { status: 204 }));
const getTeamsRollup = vi.fn(async () => ({ teams: [] }));
const getRepoStatus = vi.fn(async (_team: string, _refresh?: boolean) => mockTeamRepoStatus);

vi.mock('../../api/client', () => ({
  getOverview: (...args: unknown[]) => getOverview(...(args as [])),
  getMessages: (...args: unknown[]) => getMessages(...(args as [number])),
  getTeamTokens: (...args: unknown[]) => getTeamTokens(...(args as [string])),
  setCurrentTeam: (...args: unknown[]) => setCurrentTeam(...(args as [string | null])),
  getTeamsRollup: (...args: unknown[]) => getTeamsRollup(...(args as [])),
  getRepoStatus: (...args: unknown[]) => getRepoStatus(...(args as [string, boolean | undefined])),
  Unauthorized: class Unauthorized extends Error {},
}));

const joinTeam = vi.fn();
const leaveTeam = vi.fn();

vi.mock('../../lib/hub', () => ({
  joinTeam: (...args: unknown[]) => joinTeam(...(args as [])),
  leaveTeam: (...args: unknown[]) => leaveTeam(...(args as [])),
}));

const storage = new Map<string, string>();
vi.stubGlobal('localStorage', {
  getItem: (key: string) => storage.get(key) ?? null,
  setItem: (key: string, value: string) => {
    storage.set(key, value);
  },
  removeItem: (key: string) => {
    storage.delete(key);
  },
  clear: () => {
    storage.clear();
  },
});

const { useConsoleStore } = await import('../console');

describe('git dialog re-read behavior', () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    getMessages.mockResolvedValue([]);
    getOverview.mockResolvedValue(overview);
    getRepoStatus.mockResolvedValue(mockTeamRepoStatus);
    getRepoStatus.mockClear();
  });

  /**
   * RED TEST: Verify that pullRepoStatus uses plain getRepoStatus.
   * This is the key behavior that allows dialog-open re-reads without network touch.
   */
  it('calls getRepoStatus without refresh parameter', async () => {
    const board = useConsoleStore();
    board.teams = [team(asTeamId('TestTeam'))];
    board.setActiveTeam(asTeamId('TestTeam'));
    
    // Wait for initial setup to complete
    await new Promise(resolve => setTimeout(resolve, 50));
    
    // Call pullRepoStatus (this is what the dialog-open handler will do)
    await board.pullRepoStatus();
    
    // Verify plain getRepoStatus was called, not with refresh parameter
    expect(getRepoStatus).toHaveBeenCalledWith('TestTeam');
    
    // Verify no call was made with refresh=true
    const hasRefreshCall = getRepoStatus.mock.calls.some(call => call[1] === true);
    expect(hasRefreshCall).toBe(false);
  });

  /**
   * A failed re-read leaves the previous figures and their age.
   * 
   * When pullRepoStatus encounters a failed getRepoStatus, it should preserve
   * the previous repoStatus and originCheckedAt (the cached figures and age).
   */
  it('preserves repoStatusTeam on failed re-read', async () => {
    const board = useConsoleStore();
    const testTeamId = asTeamId('TestTeam');
    board.teams = [team(testTeamId)];
    board.setActiveTeam(testTeamId);
    
    // Wait for initial setup
    await new Promise(resolve => setTimeout(resolve, 50));
    
    // First successful fetch to establish cached values
    await board.pullRepoStatus();
    expect(getRepoStatus).toHaveBeenCalledTimes(1);
    
    // Capture the cached repoStatus and originCheckedAt
    const cachedRepoStatus = board.repoStatus?.repos[0];
    const cachedOriginCheckedAt = cachedRepoStatus?.originCheckedAt;
    const cachedRepoStatusTeam = board.repoStatusTeam;
    
    expect(cachedRepoStatus).toBeDefined();
    expect(cachedOriginCheckedAt).toBeDefined();
    expect(cachedRepoStatusTeam).toBe(testTeamId);
    
    // Mock the next getRepoStatus to fail
    getRepoStatus.mockRejectedValueOnce(new Error('Network error'));
    
    // Call pullRepoStatus again (simulating a re-read after an action or dialog open)
    await board.pullRepoStatus();
    
    // Verify that the previous repoStatus object is preserved
    const afterFailedRepoStatus = board.repoStatus?.repos[0];
    const afterFailedOriginCheckedAt = afterFailedRepoStatus?.originCheckedAt;
    const afterFailedRepoStatusTeam = board.repoStatusTeam;
    
    // The figures and age should remain the same
    expect(afterFailedRepoStatus?.mainAhead).toBe(cachedRepoStatus?.mainAhead);
    expect(afterFailedRepoStatus?.mainBehind).toBe(cachedRepoStatus?.mainBehind);
    expect(afterFailedOriginCheckedAt).toBe(cachedOriginCheckedAt);
    
    // The same repoStatusTeam should still be in place
    expect(afterFailedRepoStatusTeam).toBe(cachedRepoStatusTeam);
  });
});
