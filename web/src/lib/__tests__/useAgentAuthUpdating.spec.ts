import { describe, expect, it } from 'vitest'
import { authStatus } from '../useAgentAuth'
import type { AgentAuthReport } from '../../api/types'

describe('authStatus while an update holds the CLI', () => {
  it("reads 'Updating' with the server's sentence, never not authenticated or not installed", () => {
    const sentence = 'Updating claude on worker-1; it is measured again when the update ends.'
    const status = authStatus({
      agent: 'claude-headless', command: 'claude', installed: null, authenticated: null,
      detail: sentence, referenced: true, updating: sentence,
    } as AgentAuthReport)
    expect(status).toEqual({ text: 'Updating', icon: 'sync', tone: 'unknown', detail: sentence })
  })
})
