import { describe, expect, it } from 'vitest'
import { rebuildAgentDefinition } from '../agentDefinitionDraft'

describe('rebuildAgentDefinition', () => {
  it('rebuilds the full persisted Agent shape, including tags', () => {
    const rebuilt = rebuildAgentDefinition({
      name: ' copilot-headless ',
      mode: 'Headless',
      fileName: ' copilot ',
      args: '--run\n{userPromptFile}',
      systemPromptArguments: '--append-system-prompt-file\n{systemPromptFile}',
      instructionsFile: ' AGENTS.md ',
      usageFormat: ' copilot-usage-file ',
      env: 'FOO=one\nBAR=two',
      timeout: '900',
      tags: 'Developer\nresearch\nDEVELOPER',
      installUrl: ' https://example.invalid/copilot ',
      installHint: ' npm install -g copilot ',
      languageModel: false,
    })

    expect(Object.keys(rebuilt).sort()).toEqual([
      'env',
      'install',
      'launch',
      'mode',
      'name',
      'tags',
      'timeoutSeconds',
    ])

    expect(Object.keys(rebuilt.launch ?? {}).sort()).toEqual([
      'arguments',
      'fileName',
      'instructionsFile',
      'languageModel',
      'systemPromptArguments',
      'usageFormat',
    ])

    expect(rebuilt).toEqual({
      name: 'copilot-headless',
      mode: 'Headless',
      launch: {
        fileName: 'copilot',
        arguments: ['--run', '{userPromptFile}'],
        systemPromptArguments: ['--append-system-prompt-file', '{systemPromptFile}'],
        instructionsFile: 'AGENTS.md',
        usageFormat: 'copilot-usage-file',
        languageModel: false,
      },
      env: { FOO: 'one', BAR: 'two' },
      timeoutSeconds: 900,
      tags: ['Developer', 'research'],
      install: { url: 'https://example.invalid/copilot', hint: 'npm install -g copilot' },
    })
  })

  it('writes nulls for empty optional fields', () => {
    expect(
      rebuildAgentDefinition({
        name: 'echo',
        mode: 'Headless',
        fileName: 'echo',
        args: '',
        systemPromptArguments: '',
        instructionsFile: '  ',
        usageFormat: '',
        env: '',
        timeout: '',
        tags: '',
        installUrl: '',
        installHint: '',
        languageModel: true,
      }),
    ).toMatchObject({
      launch: {
        instructionsFile: null,
        usageFormat: null,
        systemPromptArguments: null,
        arguments: [],
      },
      env: null,
      timeoutSeconds: null,
      tags: null,
      install: null,
    })
  })

  /**
   * THE FIELD SURVIVES AN EDIT THAT IS NOT ABOUT IT.
   *
   * This rebuilder DELETES what it does not carry, and `PUT /api/agents` replaces the catalog
   * wholesale - which is how three presets lost their `instructionsFile` on a live instance,
   * silently and with no type error. `install` is worse placed than its siblings if it goes the
   * same way: `LoadOrSeed` never merges, so this dialog is the ONLY route by which an existing
   * instance ever gains an install link, and a rebuilder that dropped it would make the feature
   * unreachable on exactly the tenants that need it.
   */
  it('keeps an install link through an edit that changes something else', () => {
    const rebuilt = rebuildAgentDefinition({
      name: 'codex-headless',
      mode: 'Headless',
      fileName: 'codex',
      args: 'exec',
      systemPromptArguments: '',
      instructionsFile: 'AGENTS.md',
      usageFormat: '',
      env: '',
      timeout: '1800',
      tags: '',
      installUrl: 'https://example.invalid/codex',
      installHint: '',
      languageModel: true,
    })

    expect(rebuilt.install).toEqual({ url: 'https://example.invalid/codex' })
  })

  /**
   * A HINT WITH NOWHERE TO GO IS A REMEDY NOBODY CAN FOLLOW, and an `install` carrying an empty URL
   * is a link to nowhere - the guessed-URL failure arriving by the back door. Emptying the URL is
   * also how an operator REMOVES a link that has gone stale.
   */
  it.each(['', '   '])('drops the whole field when the URL is blank (%p)', (installUrl) => {
    const rebuilt = rebuildAgentDefinition({
      name: 'codex-headless',
      mode: 'Headless',
      fileName: 'codex',
      args: '',
      systemPromptArguments: '',
      instructionsFile: '',
      usageFormat: '',
      env: '',
      timeout: '',
      tags: '',
      installUrl,
      installHint: 'npm install -g something',
      languageModel: true,
    })

    expect(rebuilt.install).toBeNull()
  })
  it('carries a preset\'s isolation declaration through an edit unchanged', () => {
    const isolation = {
      arguments: ['--strict-mcp-config'],
      env: { ENABLE_CLAUDEAI_MCP_SERVERS: 'false' },
      allowedTools: ['Bash'],
      gaps: ['a known gap'],
    }

    const rebuilt = rebuildAgentDefinition({
      name: 'mine',
      mode: 'Headless',
      fileName: 'claude',
      args: '{isolation}\n-p',
      systemPromptArguments: '',
      instructionsFile: '',
      usageFormat: '',
      env: '',
      timeout: '',
      tags: '',
      installUrl: '',
      installHint: '',
      languageModel: true,
      isolation,
    })

    expect(rebuilt.isolation).toEqual(isolation)
  })

  it('carries a preset\'s update declaration through an edit unchanged', () => {
    const updates = { env: { DISABLE_AUTOUPDATER: '1' }, update: ['npm', 'install', '-g', 'x@latest'] }

    const rebuilt = rebuildAgentDefinition({
      name: 'mine',
      mode: 'Headless',
      fileName: 'claude',
      args: '-p',
      systemPromptArguments: '',
      instructionsFile: '',
      usageFormat: '',
      env: '',
      timeout: '',
      tags: '',
      installUrl: '',
      installHint: '',
      languageModel: true,
      updates,
    })

    expect(rebuilt.updates).toEqual(updates)
  })
})
